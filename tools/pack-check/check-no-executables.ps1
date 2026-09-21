<#
.SYNOPSIS
    Checks that the SmartUpdater NuGet package contains no executable (*.exe) and does carry the library dll.

.DESCRIPTION
    Runs   dotnet pack src\SmartUpdater\SmartUpdater.csproj -c Release -o <temp dir>   and lists every entry
    of the resulting .nupkg. The check FAILs when
      - dotnet pack fails or does not finish within 10 minutes,
      - no .nupkg is produced,
      - any entry matches *.exe (case-insensitive; every offender is printed),
      - lib/<TFM>/SimpleOneX.SmartUpdater.dll is missing (TFM is read from Directory.Build.props).

    The package output goes to %TEMP%\smartupdater-pack-check\<PID>-<random>, which is deleted at the end.
    NOTE: dotnet pack restores the project, so it creates obj/ restore artifacts inside -RepoRoot
    (src\SmartUpdater\obj). They are covered by .gitignore; nothing else in RepoRoot is written.

    Exit codes: 0 = PASS, 1 = FAIL, 2 = bad arguments.

.PARAMETER RepoRoot
    Root of the checkout. Default: two levels above this script (tools\pack-check -> repository root).

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\pack-check\check-no-executables.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptDirectory = $PSScriptRoot
if (-not $ScriptDirectory) { $ScriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent (Split-Path -Parent $ScriptDirectory) }

function Exit-Usage {
    param([string]$Message)
    Write-Host "check-no-executables: $Message"
    exit 2
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Exit-Usage "'dotnet' is not on PATH." }
if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) { Exit-Usage "RepoRoot '$RepoRoot' is not an existing directory." }
$RepoRoot = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $RepoRoot).ProviderPath)
$project = Join-Path $RepoRoot 'src\SmartUpdater\SmartUpdater.csproj'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { Exit-Usage "src\SmartUpdater\SmartUpdater.csproj not found below '$RepoRoot'." }
$propsPath = Join-Path $RepoRoot 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) { Exit-Usage "Directory.Build.props not found below '$RepoRoot'." }
$tfmMatch = [System.Text.RegularExpressions.Regex]::Match([System.IO.File]::ReadAllText($propsPath), '<TargetFramework>\s*([^<\s]+)\s*</TargetFramework>')
if (-not $tfmMatch.Success) { Exit-Usage 'Directory.Build.props does not declare <TargetFramework>.' }
$targetFramework = $tfmMatch.Groups[1].Value

$processId = [System.Diagnostics.Process]::GetCurrentProcess().Id
$random = [System.Guid]::NewGuid().ToString('N').Substring(0, 8)
$outputDirectory = Join-Path (Join-Path ([System.IO.Path]::GetTempPath()) 'smartupdater-pack-check') "$processId-$random"

$failures = New-Object System.Collections.Generic.List[string]
$packageName = ''

try {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    Write-Host "RepoRoot : $RepoRoot"
    Write-Host "TFM      : $targetFramework"
    Write-Host "pack out : $outputDirectory"
    Write-Host '> dotnet pack src\SmartUpdater\SmartUpdater.csproj -c Release -o <pack out>'

    $stdoutFile = Join-Path $outputDirectory 'pack.stdout.log'
    $stderrFile = Join-Path $outputDirectory 'pack.stderr.log'
    $process = Start-Process -FilePath 'dotnet' -WorkingDirectory $RepoRoot -NoNewWindow -PassThru `
        -ArgumentList @('pack', $project, '-c', 'Release', '-o', $outputDirectory) `
        -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile
    $null = $process.Handle
    if (-not $process.WaitForExit(600000)) {
        Start-Process -FilePath 'taskkill.exe' -ArgumentList @('/PID', [string]$process.Id, '/T', '/F') -NoNewWindow -Wait | Out-Null
        $failures.Add('dotnet pack did not finish within 10 minutes (process tree killed)')
    }
    else {
        $packLines = @()
        foreach ($file in @($stdoutFile, $stderrFile)) {
            if (Test-Path -LiteralPath $file) { $packLines += @(Get-Content -LiteralPath $file -Encoding UTF8) }
        }
        foreach ($line in @($packLines | Select-Object -Last 6)) { Write-Host "  $line" }
        Write-Host "dotnet pack exit code: $($process.ExitCode)"
        if ($process.ExitCode -ne 0) { $failures.Add("dotnet pack failed (exit code $($process.ExitCode))") }
    }

    if ($failures.Count -eq 0) {
        $packages = @(Get-ChildItem -LiteralPath $outputDirectory -Filter '*.nupkg' -File | Where-Object { $_.Name -notlike '*.symbols.nupkg' -and $_.Name -notlike '*.snupkg' })
        if ($packages.Count -ne 1) {
            $failures.Add("expected exactly one .nupkg, found $($packages.Count)")
        }
        else {
            $packageName = $packages[0].Name
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $archive = [System.IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
            try {
                $entries = @($archive.Entries | ForEach-Object { $_.FullName })
            }
            finally {
                $archive.Dispose()
            }
            Write-Host "entries of ${packageName}:"
            foreach ($entry in $entries) { Write-Host "  $entry" }
            $executables = @($entries | Where-Object { $_ -like '*.exe' })
            foreach ($executable in $executables) {
                Write-Host "EXECUTABLE ENTRY: $executable"
                $failures.Add("package contains an executable: $executable")
            }
            $expectedDll = "lib/$targetFramework/SimpleOneX.SmartUpdater.dll"
            if ($entries -notcontains $expectedDll) { $failures.Add("package does not contain $expectedDll") }
        }
    }
}
catch {
    $failures.Add("unexpected script error: $($_.Exception.Message)")
    Write-Host ($_ | Out-String)
}
finally {
    $removed = $false
    for ($attempt = 1; $attempt -le 5 -and -not $removed; $attempt++) {
        try {
            if (Test-Path -LiteralPath $outputDirectory) { Remove-Item -LiteralPath $outputDirectory -Recurse -Force -ErrorAction Stop }
            $removed = $true
        }
        catch {
            Start-Sleep -Milliseconds (300 * $attempt)
        }
    }
    if (-not $removed) { Write-Host "WARNING: could not remove $outputDirectory" }
}

if ($failures.Count -eq 0) {
    Write-Host "PASS: no .exe inside $packageName"
    exit 0
}
Write-Host 'FAIL:'
foreach ($failure in $failures) { Write-Host "  - $failure" }
exit 1
