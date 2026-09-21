<#
.SYNOPSIS
    Native AOT smoke test for the SmartUpdater package.

.DESCRIPTION
    Checks that the package (src\SmartUpdater) publishes under Native AOT without any IL warning and that a
    native host which walks the client's reachable code paths runs and prints AOT-SMOKE OK as its last line.

    The script is READ-ONLY with respect to -RepoRoot: it only copies files out of it. Every run
      1. copies Directory.Build.props, .editorconfig and src\SmartUpdater (bin/obj excluded) from
         -RepoRoot into a clean work directory ("repo-copy"). Directory.Build.targets,
         Directory.Packages.props, global.json and nuget.config are copied as well when the checkout
         has them, so that the copy is built with the same settings as the checkout. Any other
         Directory.* file below src\ is NOT copied and produces a warning;
      2. appends [assembly: InternalsVisibleTo("AotSmoke")] to the COPY of AssemblyInfo.cs;
      3. generates a console host "AotSmoke": its Program.cs is AotSmokeHost.Program.cs from this directory
         (FileReleaseFeed, validation, selection, options, signatures, report serialization, HTTP transports,
         SmartUpdaterApp.Run, single instance, UpdateClient.RunAsync), its csproj roots the whole package
         assembly (TrimmerRootAssembly) so that ILC analyses every method of it, not only the reachable ones;
      4. runs   dotnet publish -c Release -r win-x64 -p:PublishAot=true   inside the host directory;
      5. prints every output line that mentions warning / error / IL2026 / IL3050 (or any ILxxxx code);
      6. runs the native executable and checks that its last output line is exactly AOT-SMOKE OK;
      7. deletes the work directory (only if this script created it and it still carries the marker file).

    If the native publish fails (and the output carries no ILxxxx diagnostic), it also runs
      dotnet build -c Release -p:PublishAot=true   as the second-best check (AOT/trim analyzers only).
    A clean fallback build ends as UNVERIFIED (exit code 3): the native publish itself was not verified.

    The script installs nothing and changes no machine setting. Be aware that the implicit restore of
    "dotnet publish" DOES go online to download the ILCompiler and runtime packs when they are not in the
    NuGet cache yet (the first run can be slow). Its only environment tweak is process-local: when
    vswhere.exe is not resolvable through PATH (plain PowerShell instead of a VS Developer shell), the
    already-installed Visual Studio Installer directory is appended to PATH for the child processes and PATH
    is restored at exit (see the comment above the try block for the reason). Output is forced to English
    (DOTNET_CLI_UI_LANGUAGE / VSLANG / PreferredUILang) so that the diagnostic lines are greppable.

    Exit codes: 0 = PASS
                1 = FAIL (ILxxxx diagnostics / host failed / wrong output / publish timed out)
                2 = bad arguments or unsafe paths
                3 = UNVERIFIED (native toolchain missing: publish failed, but the fallback build is clean)

.PARAMETER RepoRoot
    Root of the checkout to verify (must contain Directory.Build.props and src\SmartUpdater\SmartUpdater.csproj).

.PARAMETER WorkDir
    Scratch directory. Default: %TEMP%\smartupdater-aot-smoke\work-<PID>-<8 random hex digits>.
    It must be outside RepoRoot, must not be a drive root and neither it nor RepoRoot may sit below a
    reparse point (junction / symlink). A pre-existing WorkDir is wiped only if it is empty or carries
    this script's marker file.

.PARAMETER LogPath
    File that receives the complete dotnet output. Default:
    %TEMP%\smartupdater-aot-smoke\aot-smoke-<yyyyMMdd-HHmmss>-<PID>.log (kept after the run, its path is
    printed). It must not be inside RepoRoot or WorkDir.

.PARAMETER KeepWorkDir
    Do not delete WorkDir at the end (for troubleshooting).

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\aot-smoke\run-aot-smoke.ps1 -RepoRoot .

.NOTES
    Behavior notes
      - The host csproj carries <TrimmerRootAssembly Include="SimpleOneX.SmartUpdater" />; the host Program.cs calls
        the client types.
      - LogPath under RepoRoot (or RepoRoot itself) -> exit 2; the full dotnet output is kept by default in
        %TEMP%\smartupdater-aot-smoke\aot-smoke-<time>-<PID>.log and its path is printed.
      - Containment checks use GetFullPath and keep the trailing separator of drive roots; WorkDir may not be a
        drive root; a reparse point in the ancestry of WorkDir or RepoRoot -> exit 2.
      - The try block starts right after the environment is saved, before WorkDir and the marker file are
        created; finally restores the environment and removes only a WorkDir this script created.
      - The default WorkDir lives in %TEMP% (never in the repository) and is named work-<PID>-<random>.
      - The copy list is hard-coded (see above); other Directory.* files below src\ trigger a warning; the target
        framework is read from the copied Directory.Build.props instead of being hard-coded.
      - The publish has a 20 minute timeout (process tree killed, FAIL); exit codes 0 / 1 / 2 / 3; no parameter
        is Mandatory, the script validates them itself.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,

    [string]$WorkDir,

    [string]$LogPath,

    [switch]$KeepWorkDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Defaults are computed here and not in the param() block: under 'powershell.exe -File' the value of
# $PSScriptRoot is still empty while param() defaults are evaluated.
$ScriptDirectory = $PSScriptRoot
if (-not $ScriptDirectory) { $ScriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path }

$HostName = 'AotSmoke'
$ExpectedLastLine = 'AOT-SMOKE OK'
$MarkerName = '.aot-smoke-workdir'
$PublishTimeoutMilliseconds = 20 * 60 * 1000
$RunTimeoutMilliseconds = 5 * 60 * 1000
$RootFilesToCopy = @('Directory.Build.props', '.editorconfig', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'nuget.config')
$HostProgramSource = Join-Path $ScriptDirectory 'AotSmokeHost.Program.cs'
$TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'smartupdater-aot-smoke'
$ProcessId = [System.Diagnostics.Process]::GetCurrentProcess().Id

# Stops MSBuild from picking up a Directory.Build.props somewhere above the work directory for the host
# project (repo-copy has its own, closer one, which is what the package project uses).
$HostGuardProps = '<Project />'

function Exit-Usage {
    param([string]$Message)
    Write-Host "run-aot-smoke: $Message"
    exit 2
}

function Write-Section {
    param([string]$Title)
    Write-Host ''
    Write-Host "==== $Title ===="
}

function Get-NormalizedPath {
    param([string]$Path)
    $unresolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    $full = [System.IO.Path]::GetFullPath($unresolved)
    $root = [System.IO.Path]::GetPathRoot($full)
    # A drive root keeps its trailing separator: 'C:\'.TrimEnd('\') would become 'C:' (the current directory of C:).
    if ($full.Length -gt $root.Length) { $full = $full.TrimEnd('\') }
    return $full
}

function Test-IsSameOrBelow {
    param([string]$Path, [string]$Ancestor)
    $left = $Path
    if (-not $left.EndsWith('\')) { $left += '\' }
    $right = $Ancestor
    if (-not $right.EndsWith('\')) { $right += '\' }
    return $left.StartsWith($right, [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-IsDriveRoot {
    param([string]$Path)
    $root = [System.IO.Path]::GetPathRoot($Path)
    return ($Path.TrimEnd('\') -ieq $root.TrimEnd('\'))
}

# Returns the first existing directory (the path itself or one of its ancestors) that is a reparse point, or $null.
function Find-ReparsePoint {
    param([string]$Path)
    $current = $Path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { return $current }
        }
        $parent = [System.IO.Path]::GetDirectoryName($current)
        if (-not $parent -or $parent -ieq $current) { break }
        $current = $parent
    }
    return $null
}

function Write-TextFile {
    param([string]$Path, [string]$Content)
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    $text = $Content.Replace("`r`n", "`n")
    if (-not $text.EndsWith("`n")) { $text += "`n" }
    [System.IO.File]::WriteAllText($Path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

function Remove-Directory {
    param([string]$Path)
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        if (-not (Test-Path -LiteralPath $Path)) { return $true }
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
        }
        catch {
            Start-Sleep -Milliseconds (400 * $attempt)
        }
    }
    return (-not (Test-Path -LiteralPath $Path))
}

function Copy-SourceTree {
    param([string]$From, [string]$To)
    $root = $From.TrimEnd('\')
    $skipped = @('bin', 'obj')
    $count = 0
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
        $relative = $file.FullName.Substring($root.Length + 1)
        $segments = $relative.Split('\')
        $insideSkippedDirectory = $false
        for ($i = 0; $i -lt $segments.Length - 1; $i++) {
            if ($skipped -contains $segments[$i]) { $insideSkippedDirectory = $true; break }
        }
        if ($insideSkippedDirectory) { continue }
        $target = Join-Path $To $relative
        $targetDirectory = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $targetDirectory)) {
            New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $target
        $count++
    }
    return $count
}

function Write-Command {
    param([string]$CommandLine, [string]$WorkingDirectory)
    Write-Host "> $CommandLine"
    Write-Host "  (working directory: $WorkingDirectory)"
}

# Runs a program with stdout/stderr redirected into files, so that PowerShell 5.1 never turns a stderr line into
# a NativeCommandError (which would be a terminating error under $ErrorActionPreference = 'Stop').
function Invoke-Process {
    param([string]$Tag, [string]$FilePath, [string]$WorkingDirectory, [string[]]$Arguments = @(), [int]$TimeoutMilliseconds)
    $stdoutFile = Join-Path $script:WorkDir "$Tag.stdout.log"
    $stderrFile = Join-Path $script:WorkDir "$Tag.stderr.log"
    $startParameters = @{
        FilePath               = $FilePath
        WorkingDirectory       = $WorkingDirectory
        NoNewWindow            = $true
        PassThru               = $true
        RedirectStandardOutput = $stdoutFile
        RedirectStandardError  = $stderrFile
    }
    if ($Arguments.Count -gt 0) { $startParameters['ArgumentList'] = $Arguments }
    # No 'Start-Process -Wait': Windows PowerShell 5.1 then waits for every descendant process as well, and the
    # native link step leaves vctip.exe (the MSVC telemetry helper, spawned by link.exe) running for a long time,
    # which hangs the script after a perfectly good publish. WaitForExit() waits for the started process only.
    $process = Start-Process @startParameters
    $null = $process.Handle    # keep the handle open, otherwise ExitCode can come back empty
    $timedOut = $false
    if (-not $process.WaitForExit($TimeoutMilliseconds)) {
        $timedOut = $true
        # Only the process this script started (and its children), addressed by PID.
        Start-Process -FilePath 'taskkill.exe' -ArgumentList @('/PID', [string]$process.Id, '/T', '/F') -NoNewWindow -Wait | Out-Null
        [void]$process.WaitForExit(30000)
    }
    # The dotnet CLI writes UTF-8 into redirected streams; Windows PowerShell 5.1 would otherwise read ANSI.
    $stdout = @()
    $stderr = @()
    if (Test-Path -LiteralPath $stdoutFile) { $stdout = @(Get-Content -LiteralPath $stdoutFile -Encoding UTF8) }
    if (Test-Path -LiteralPath $stderrFile) { $stderr = @(Get-Content -LiteralPath $stderrFile -Encoding UTF8) }
    $exitCode = -1
    if (-not $timedOut) { $exitCode = $process.ExitCode }
    return [pscustomobject]@{ ExitCode = $exitCode; TimedOut = $timedOut; StdOut = $stdout; StdErr = $stderr; Lines = @($stdout + $stderr) }
}

function Get-DiagnosticLines {
    param([string[]]$Lines)
    return @($Lines | Where-Object { $_ -match '(?i)warning|error|IL2026|IL3050' } | Select-Object -Unique)
}

function Get-IlLines {
    param([string[]]$Lines)
    return @($Lines | Where-Object { $_ -match '\bIL\d{4}\b' } | Select-Object -Unique)
}

function Show-Diagnostics {
    param($Result)
    # @() around the call: a function that returns an empty array yields $null, and $null.Count throws under strict mode.
    $diagnostics = @(Get-DiagnosticLines $Result.Lines)
    Write-Host "exit code: $($Result.ExitCode)$(if ($Result.TimedOut) { ' (TIMED OUT, process tree killed)' })"
    Write-Host '--- output lines mentioning warning / error / IL2026 / IL3050 ---'
    if ($diagnostics.Count -eq 0) {
        Write-Host '(none)'
    }
    else {
        foreach ($line in $diagnostics) { Write-Host $line }
    }
    Write-Host '--- last lines of the output ---'
    foreach ($line in @($Result.Lines | Select-Object -Last 8)) { Write-Host $line }
}

# ---------------------------------------------------------------------------------------------
# Argument validation (nothing has been created yet, so bailing out needs no cleanup)
# ---------------------------------------------------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Exit-Usage "'dotnet' is not on PATH." }
if (-not $RepoRoot) { Exit-Usage 'parameter -RepoRoot is required.' }
if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) { Exit-Usage "RepoRoot '$RepoRoot' is not an existing directory." }
if (-not (Test-Path -LiteralPath $HostProgramSource -PathType Leaf)) { Exit-Usage "host source '$HostProgramSource' is missing." }

$RepoRoot = Get-NormalizedPath $RepoRoot
foreach ($required in @('Directory.Build.props', 'src\SmartUpdater\SmartUpdater.csproj')) {
    if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot $required) -PathType Leaf)) {
        Exit-Usage "'$RepoRoot' does not look like a SmartUpdater checkout: $required is missing."
    }
}

$RunTag = '{0}-{1}' -f $ProcessId, ([System.Guid]::NewGuid().ToString('N').Substring(0, 8))
if (-not $WorkDir) { $WorkDir = Join-Path $TempRoot "work-$RunTag" }
$WorkDir = Get-NormalizedPath $WorkDir
if (-not $LogPath) { $LogPath = Join-Path $TempRoot ('aot-smoke-{0}-{1}.log' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $ProcessId) }
$LogPath = Get-NormalizedPath $LogPath

if (Test-IsDriveRoot $WorkDir) { Exit-Usage "WorkDir '$WorkDir' is a drive root; refusing to use it." }
if (Test-IsSameOrBelow $WorkDir $RepoRoot) { Exit-Usage 'WorkDir must be outside RepoRoot: the script must not touch the checkout.' }
if (Test-IsSameOrBelow $RepoRoot $WorkDir) { Exit-Usage 'RepoRoot must not be inside WorkDir: WorkDir is deleted at the end.' }
if (Test-IsSameOrBelow $LogPath $RepoRoot) { Exit-Usage "LogPath '$LogPath' must not be inside RepoRoot: the script must not create files in the checkout." }
if (Test-IsSameOrBelow $LogPath $WorkDir) { Exit-Usage "LogPath '$LogPath' must not be inside WorkDir: WorkDir is deleted at the end." }
foreach ($candidate in @(@('RepoRoot', $RepoRoot), @('WorkDir', $WorkDir))) {
    $reparse = Find-ReparsePoint $candidate[1]
    if ($reparse) { Exit-Usage "$($candidate[0]) '$($candidate[1])' is or sits below a reparse point ('$reparse'); refusing to continue." }
}

$marker = Join-Path $WorkDir $MarkerName
if (Test-Path -LiteralPath $WorkDir) {
    $carriesMarker = Test-Path -LiteralPath $marker -PathType Leaf
    $isEmpty = -not (Get-ChildItem -LiteralPath $WorkDir -Force | Select-Object -First 1)
    if (-not ($carriesMarker -or $isEmpty)) {
        Exit-Usage "WorkDir '$WorkDir' exists, is not empty and was not created by this script; refusing to delete it."
    }
}

# ---------------------------------------------------------------------------------------------
# Work
# ---------------------------------------------------------------------------------------------
$failures = New-Object System.Collections.Generic.List[string]
$fullLog = New-Object System.Collections.Generic.List[string]
$nativePublishVerified = $false
$unverified = $false
$workDirCreated = $false
$cleanupNote = ''
$logNote = ''

$environmentNames = @('DOTNET_CLI_UI_LANGUAGE', 'VSLANG', 'PreferredUILang', 'PATH')
$savedEnvironment = @{}
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [System.Environment]::GetEnvironmentVariable($name, 'Process') }

try {
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'
    $env:VSLANG = '1033'
    $env:PreferredUILang = 'en-US'

    # Why PATH is touched: the ILCompiler targets run findvcvarsall.bat, which calls Visual Studio's own
    # vcvarsall.bat; somewhere in that chain the bare name 'vswhere.exe' is invoked. In a plain PowerShell
    # session the Visual Studio Installer directory (where the already-installed vswhere.exe lives) is not on
    # PATH, so cmd prints "'vswhere.exe' is not recognized ..." to stderr. MSBuild captures stderr together
    # with stdout, the text ends up inside the linker path, and the link step dies with MSB3073 (exit code 3).
    # A VS Developer PowerShell has that directory on PATH. We add it for this process only (nothing is
    # installed or configured on the machine) and restore PATH in the finally block below.
    $installerDirectory = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    $pathNote = 'vswhere.exe already resolvable through PATH; PATH left alone'
    if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
        if (Test-Path -LiteralPath (Join-Path $installerDirectory 'vswhere.exe') -PathType Leaf) {
            $env:PATH = $env:PATH.TrimEnd(';') + ';' + $installerDirectory
            $pathNote = "vswhere.exe was not on PATH; appended '$installerDirectory' to PATH for this process only (restored at exit)"
        }
        else {
            $pathNote = "vswhere.exe was not found on PATH nor in '$installerDirectory'; PATH left alone (the native link step may fail)"
        }
    }

    if (Test-Path -LiteralPath $WorkDir) {
        if (-not (Remove-Directory $WorkDir)) { Exit-Usage "Could not clear the old WorkDir '$WorkDir'." }
    }
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
    Set-Content -LiteralPath $marker -Value 'created by run-aot-smoke.ps1; safe to delete' -Encoding ASCII
    $workDirCreated = $true

    Write-Section 'SmartUpdater Native AOT smoke'
    Write-Host "RepoRoot (read-only): $RepoRoot"
    Write-Host "WorkDir             : $WorkDir"
    Write-Host "LogPath             : $LogPath"
    Write-Host "dotnet SDK          : $(& dotnet --version)"
    Write-Host "PATH                : $pathNote"
    $linkers = @(Get-Command link.exe -All -ErrorAction SilentlyContinue | ForEach-Object { $_.Source })
    if ($linkers.Count -eq 0) {
        Write-Host 'link.exe on PATH    : (none; the SDK locates the MSVC linker through vswhere)'
    }
    else {
        Write-Host "link.exe on PATH    : $($linkers -join '; ')"
        if ($linkers | Where-Object { $_ -match '\\usr\\bin\\' }) {
            Write-Host 'WARNING: a coreutils link.exe (Git usr\bin) is on PATH; if the link step fails, run this script from plain PowerShell instead of Git Bash.'
        }
    }

    # -- 1. copy sources -------------------------------------------------------------------------
    Write-Section 'Copy sources from RepoRoot (read-only) into WorkDir\repo-copy'
    $repoCopy = Join-Path $WorkDir 'repo-copy'
    $hostDirectory = Join-Path $WorkDir $HostName
    New-Item -ItemType Directory -Force -Path $repoCopy | Out-Null

    $copiedRootFiles = @()
    foreach ($name in $RootFilesToCopy) {
        $from = Join-Path $RepoRoot $name
        if (Test-Path -LiteralPath $from -PathType Leaf) {
            Copy-Item -LiteralPath $from -Destination (Join-Path $repoCopy $name)
            $copiedRootFiles += $name
        }
    }
    if ($copiedRootFiles -notcontains '.editorconfig') {
        Write-Host 'WARNING: .editorconfig is missing in RepoRoot; the copy is built without it.'
    }
    $copiedCount = Copy-SourceTree -From (Join-Path $RepoRoot 'src\SmartUpdater') -To (Join-Path $repoCopy 'src\SmartUpdater')
    Write-Host "root files copied : $($copiedRootFiles -join ', ')"
    Write-Host "src\SmartUpdater  : $copiedCount file(s) copied (bin/obj excluded)"

    $packageSource = (Join-Path $RepoRoot 'src\SmartUpdater').TrimEnd('\') + '\'
    foreach ($extra in @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'src') -Recurse -File -Force -Filter 'Directory.*' -ErrorAction SilentlyContinue)) {
        if ($extra.FullName.StartsWith($packageSource, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($extra.FullName -match '\\(bin|obj)\\') { continue }
        Write-Host "WARNING: '$($extra.FullName)' was not copied and may change the build settings of the package."
    }

    # The target framework comes from the copied Directory.Build.props, not from a constant.
    $propsText = [System.IO.File]::ReadAllText((Join-Path $repoCopy 'Directory.Build.props'))
    $tfmMatch = [System.Text.RegularExpressions.Regex]::Match($propsText, '<TargetFramework>\s*([^<\s]+)\s*</TargetFramework>')
    if (-not $tfmMatch.Success) { throw 'Directory.Build.props does not declare <TargetFramework>.' }
    $targetFramework = $tfmMatch.Groups[1].Value
    Write-Host "target framework  : $targetFramework (from Directory.Build.props)"

    # -- 2. InternalsVisibleTo in the COPY only ---------------------------------------------------
    $assemblyInfo = Join-Path $repoCopy 'src\SmartUpdater\AssemblyInfo.cs'
    $existing = ''
    if (Test-Path -LiteralPath $assemblyInfo) { $existing = [System.IO.File]::ReadAllText($assemblyInfo) }
    if ($existing -match 'using\s+System\.Runtime\.CompilerServices\s*;') {
        $attribute = '[assembly: InternalsVisibleTo("AotSmoke")]'
    }
    else {
        $attribute = '[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AotSmoke")]'
    }
    if ($existing.Length -gt 0 -and -not $existing.EndsWith("`n")) { $existing += "`n" }
    Write-TextFile -Path $assemblyInfo -Content ($existing + $attribute + "`n")
    Write-Host "appended to the copy of AssemblyInfo.cs: $attribute"

    # -- 3. host project --------------------------------------------------------------------------
    $hostCsproj = @"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>$targetFramework</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\repo-copy\src\SmartUpdater\SmartUpdater.csproj" />
  </ItemGroup>

  <ItemGroup>
    <TrimmerRootAssembly Include="SimpleOneX.SmartUpdater" />
  </ItemGroup>

</Project>
"@
    Write-TextFile -Path (Join-Path $WorkDir 'Directory.Build.props') -Content $HostGuardProps
    Write-TextFile -Path (Join-Path $hostDirectory 'AotSmoke.csproj') -Content $hostCsproj
    New-Item -ItemType Directory -Force -Path $hostDirectory | Out-Null
    Copy-Item -LiteralPath $HostProgramSource -Destination (Join-Path $hostDirectory 'Program.cs')
    Write-Host 'generated host    : AotSmoke\AotSmoke.csproj + AotSmoke\Program.cs (from AotSmokeHost.Program.cs)'

    # -- 4. publish -------------------------------------------------------------------------------
    Write-Section 'dotnet publish (Native AOT)'
    Write-Command -CommandLine 'dotnet publish -c Release -r win-x64 -p:PublishAot=true' -WorkingDirectory $hostDirectory
    $publish = Invoke-Process -Tag 'publish' -FilePath 'dotnet' -WorkingDirectory $hostDirectory `
        -Arguments @('publish', '-c', 'Release', '-r', 'win-x64', '-p:PublishAot=true') -TimeoutMilliseconds $PublishTimeoutMilliseconds
    $fullLog.AddRange([string[]]@('===== dotnet publish -c Release -r win-x64 -p:PublishAot=true ====='))
    $fullLog.AddRange([string[]]@($publish.Lines))
    Show-Diagnostics $publish

    $ilLines = @(Get-IlLines $publish.Lines)
    if ($publish.TimedOut) {
        $failures.Add('dotnet publish did not finish within 20 minutes (process tree killed)')
    }
    elseif ($publish.ExitCode -ne 0) {
        $failures.Add("dotnet publish failed (exit code $($publish.ExitCode)); native publish NOT verified")
    }
    if ($ilLines.Count -gt 0) {
        $failures.Add("$($ilLines.Count) distinct output line(s) carry an ILxxxx diagnostic")
    }

    # -- 4b. fallback when the native publish failed ---------------------------------------------
    if ((-not $publish.TimedOut) -and ($publish.ExitCode -ne 0)) {
        Write-Section 'Fallback check: dotnet build -c Release -p:PublishAot=true (analyzers only)'
        Write-Command -CommandLine 'dotnet build -c Release -p:PublishAot=true' -WorkingDirectory $hostDirectory
        $build = Invoke-Process -Tag 'fallback-build' -FilePath 'dotnet' -WorkingDirectory $hostDirectory `
            -Arguments @('build', '-c', 'Release', '-p:PublishAot=true') -TimeoutMilliseconds $PublishTimeoutMilliseconds
        $fullLog.AddRange([string[]]@('===== dotnet build -c Release -p:PublishAot=true (fallback) ====='))
        $fullLog.AddRange([string[]]@($build.Lines))
        Show-Diagnostics $build
        $buildIlLines = @(Get-IlLines $build.Lines)
        Write-Host "fallback build: exit code $($build.ExitCode), distinct ILxxxx lines: $($buildIlLines.Count)"
        if ($build.ExitCode -ne 0) { $failures.Add("fallback build failed too (exit code $($build.ExitCode))") }
        if ($buildIlLines.Count -gt 0) { $failures.Add("fallback build reports $($buildIlLines.Count) distinct ILxxxx line(s)") }
        # Publish failed, but neither the publish output nor the fallback build carries an IL diagnostic and the
        # fallback compiles: the native toolchain is the culprit. That is not a pass and not a code defect.
        if (($ilLines.Count -eq 0) -and ($build.ExitCode -eq 0) -and ($buildIlLines.Count -eq 0)) {
            $unverified = $true
        }
    }
    elseif (-not $publish.TimedOut) {
        # -- 5. run the native executable ---------------------------------------------------------
        Write-Section 'Run the published native executable'
        $publishDirectory = Join-Path $hostDirectory "bin\Release\$targetFramework\win-x64\publish"
        $executable = Join-Path $publishDirectory 'AotSmoke.exe'
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            $failures.Add("published executable not found: $executable")
        }
        else {
            $isManaged = $true
            try {
                [void][System.Reflection.AssemblyName]::GetAssemblyName($executable)
            }
            catch {
                $exception = $_.Exception
                if (($exception -is [System.BadImageFormatException]) -or ($exception.InnerException -is [System.BadImageFormatException])) {
                    $isManaged = $false
                }
                else {
                    throw
                }
            }
            $managedDll = Join-Path $publishDirectory 'AotSmoke.dll'
            $sizeKiB = [math]::Round((Get-Item -LiteralPath $executable).Length / 1KB)
            Write-Host "executable        : $executable ($sizeKiB KiB)"
            Write-Host "native image      : $(if ($isManaged) { 'NO - it is a managed assembly' } else { 'yes (not a managed assembly)' })"
            Write-Host "AotSmoke.dll next to it: $(Test-Path -LiteralPath $managedDll)"
            if ($isManaged -or (Test-Path -LiteralPath $managedDll)) {
                $failures.Add('the published output is not a pure native image')
            }

            Write-Command -CommandLine $executable -WorkingDirectory $publishDirectory
            $run = Invoke-Process -Tag 'run' -FilePath $executable -WorkingDirectory $publishDirectory -TimeoutMilliseconds $RunTimeoutMilliseconds
            $fullLog.AddRange([string[]]@('===== AotSmoke.exe ====='))
            $fullLog.AddRange([string[]]@($run.Lines))
            $outputLines = @($run.StdOut | Where-Object { $_.Trim().Length -gt 0 })
            $printed = ''
            if ($outputLines.Count -gt 0) { $printed = $outputLines[$outputLines.Count - 1].Trim() }
            Write-Host "exit code: $($run.ExitCode)$(if ($run.TimedOut) { ' (TIMED OUT, process tree killed)' })"
            Write-Host 'output   :'
            foreach ($line in @($run.StdOut | Select-Object -First 20)) { Write-Host "  $line" }
            if ($run.StdErr.Count -gt 0) {
                Write-Host 'stderr   :'
                foreach ($line in @($run.StdErr | Select-Object -First 12)) { Write-Host "  $line" }
            }
            if ($run.TimedOut) { $failures.Add('the native executable did not finish within 5 minutes') }
            elseif ($run.ExitCode -ne 0) { $failures.Add("the native executable exited with code $($run.ExitCode)") }
            if ($printed -cne $ExpectedLastLine) { $failures.Add("the native executable's last output line was '$printed' instead of '$ExpectedLastLine'") }
            if ((-not $run.TimedOut) -and ($run.ExitCode -eq 0) -and ($printed -ceq $ExpectedLastLine) -and -not $isManaged) { $nativePublishVerified = $true }
        }
    }
}
catch {
    $failures.Add("unexpected script error: $($_.Exception.Message)")
    Write-Host ($_ | Out-String)
}
finally {
    foreach ($name in $environmentNames) {
        [System.Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    if ($fullLog.Count -gt 0) {
        try {
            $logDirectory = Split-Path -Parent $LogPath
            if (-not (Test-Path -LiteralPath $logDirectory)) { New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null }
            Set-Content -LiteralPath $LogPath -Value $fullLog -Encoding UTF8
            $logNote = "full dotnet output saved to: $LogPath"
        }
        catch {
            $logNote = "WARNING: could not write the log file '$LogPath': $($_.Exception.Message)"
        }
    }
    else {
        $logNote = 'no dotnet output was produced, no log file written'
    }
    if (-not $workDirCreated) {
        $cleanupNote = 'work directory was never created'
    }
    elseif ($KeepWorkDir) {
        $cleanupNote = "work directory kept: $WorkDir"
    }
    elseif (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
        $cleanupNote = "WARNING: marker file is gone, work directory NOT removed: $WorkDir"
    }
    elseif (Remove-Directory $WorkDir) {
        $cleanupNote = 'work directory removed'
    }
    else {
        $cleanupNote = "WARNING: could not remove the work directory: $WorkDir"
    }
}

# ---------------------------------------------------------------------------------------------
# Verdict
# ---------------------------------------------------------------------------------------------
Write-Section 'RESULT'
Write-Host "RepoRoot (untouched): $RepoRoot"
Write-Host "cleanup             : $cleanupNote"
Write-Host "log                 : $logNote"
if ($failures.Count -eq 0 -and $nativePublishVerified) {
    Write-Host "PASS: native AOT publish succeeded, no ILxxxx diagnostics, the native executable printed $ExpectedLastLine."
    exit 0
}
if ($unverified) {
    Write-Host 'UNVERIFIED: the native AOT publish failed without any ILxxxx diagnostic (native toolchain missing or broken?),'
    Write-Host '            but the fallback build with -p:PublishAot=true is clean (0 distinct ILxxxx lines).'
    Write-Host '            The native executable was NOT built or run. Reasons recorded:'
    foreach ($failure in $failures) { Write-Host "  - $failure" }
    exit 3
}
Write-Host 'FAIL:'
foreach ($failure in $failures) { Write-Host "  - $failure" }
if ($failures.Count -eq 0) { Write-Host '  - the native run could not be confirmed (no further details were recorded)' }
exit 1
