# Publish / mutate / pack helpers. ASCII only, LF.
Set-StrictMode -Version Latest

$script:SolutionBuilt = $false

# Runs a native command; throws with the full output when the exit code is not 0.
# Delegates to Invoke-Native (common.ps1), which lowers $ErrorActionPreference for the call: on Windows
# PowerShell 5.1 a redirected stderr line would otherwise become a terminating NativeCommandError.
function Invoke-Checked([string]$What, [scriptblock]$Action) {
    $result = Invoke-Native $Action
    if ($result.ExitCode -ne 0) {
        throw ($What + ' failed with exit code ' + $result.ExitCode + ':' + [Environment]::NewLine + ($result.Output -join [Environment]::NewLine))
    }
    return $result.Output
}

# Fixed, increasing UTC timestamp per version so that the feed is byte-for-byte reproducible.
# 1.0.0 -> 2026-09-01, 1.1.0 -> 2026-09-02, 1.2.0 -> 2026-09-03; any other version: 2026-09-01 plus (major+minor+patch) days.
function Get-ReleasedAt([string]$Version) {
    $parts = @($Version.Split('.') | ForEach-Object { [int]$_ })
    $sum = 0
    for ($i = 0; $i -lt [Math]::Min(3, $parts.Count); $i++) { $sum += $parts[$i] }
    $known = @{ '1.0.0' = 0; '1.1.0' = 1; '1.2.0' = 2 }
    $days = $sum
    if ($known.ContainsKey($Version)) { $days = $known[$Version] }
    $date = (New-Object System.DateTime 2026, 9, 1, 0, 0, 0, ([System.DateTimeKind]::Utc)).AddDays($days)
    return $date.ToString('yyyy-MM-ddTHH:mm:ss', [System.Globalization.CultureInfo]::InvariantCulture) + 'Z'
}

function New-SampleRelease {
    param(
        [Parameter(Mandatory)][hashtable]$Context,
        [Parameter(Mandatory)][string]$Version,     # three segments, e.g. 1.1.0
        [scriptblock]$Mutate
    )
    if (-not $script:SolutionBuilt) {
        $sln = Join-Path $Context.RepoRoot 'SmartUpdater.slnx'
        Invoke-Checked 'solution build' { dotnet build $sln -c Release --nologo } | Out-Null
        $script:SolutionBuilt = $true
    }
    $csproj = Join-Path $Context.RepoRoot 'samples\MinimalApp.WinForms\MinimalApp.WinForms.csproj'
    $dir = Join-Path $Context.Root ('publish\' + $Version)
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Invoke-Checked "publish $Version" { dotnet publish $csproj -c Release -r win-x64 --no-self-contained -p:Version=$Version -o $dir --nologo } | Out-Null
    if ($Mutate) { & $Mutate $dir }
    return $dir
}

function Invoke-Pack {
    param(
        [Parameter(Mandatory)][hashtable]$Context,
        [Parameter(Mandatory)][string]$PublishDir,
        [Parameter(Mandatory)][string]$Version,
        [ValidateSet('optional', 'mandatory')][string]$Mode,
        [int]$RolloutPercent = -1,
        [string]$MinUpdatableFrom,
        [string]$Notes,
        [string[]]$Preserve = @(),
        [int]$PollIntervalSeconds = -1,
        [int]$JitterWindowSeconds = -1,
        [switch]$Force
    )
    $releases = Join-Path $Context.Root 'releases'
    New-Item -ItemType Directory -Force -Path $releases | Out-Null
    $Context.ReleasesDir = $releases
    $Context.FeedPath = Join-Path $releases 'releases.json'

    $packer = Join-Path $Context.RepoRoot 'tools\e2e\PackerHost\PackerHost.csproj'
    $argList = @(
        'pack', '--input', $PublishDir, '--version', $Version, '--output', $releases,
        '--package-name', 'MinimalApp.WinForms', '--released-at', (Get-ReleasedAt $Version)
    )
    if ($Mode) { $argList += @('--mode', $Mode) }
    if ($RolloutPercent -ge 0) { $argList += @('--rollout-percent', [string]$RolloutPercent) }
    if ($MinUpdatableFrom) { $argList += @('--min-updatable-from', $MinUpdatableFrom) }
    if ($Notes) { $argList += @('--notes', $Notes) }
    foreach ($p in $Preserve) { $argList += @('--preserve', $p) }
    if ($PollIntervalSeconds -ge 0) { $argList += @('--poll-interval-seconds', [string]$PollIntervalSeconds) }
    if ($JitterWindowSeconds -ge 0) { $argList += @('--jitter-window-seconds', [string]$JitterWindowSeconds) }
    if ($Force) { $argList += '--force' }

    # The Packer itself is GUI-only; PackerHost hands the same arguments to its pack engine and returns its exit code.
    Invoke-Checked "pack $Version" { dotnet run --project $packer -c Release -- @argList } | Out-Null
    return $Context.FeedPath
}

function Install-Release {
    param(
        [Parameter(Mandatory)][hashtable]$Context,
        [Parameter(Mandatory)][string]$PublishDir
    )
    $Context.InstallDir = Join-Path $Context.Root 'app'
    $Context.LocalAppData = Join-Path $Context.Root 'localappdata'
    $Context.EventLog = Join-Path $Context.Root 'events.log'
    $Context.ShotDir = Join-Path $Context.Root 'shots'
    foreach ($d in @($Context.InstallDir, $Context.LocalAppData, $Context.ShotDir)) {
        New-Item -ItemType Directory -Force -Path $d | Out-Null
    }
    # Copy only: a first install has no .smartupdater folder, which is exactly the updater's "first install" path.
    Copy-Item -Path (Join-Path $PublishDir '*') -Destination $Context.InstallDir -Recurse -Force
}

# relative path (forward slashes, lower case) -> SHA-256 (upper-case hex).
# -Exclude: an entry ending in '/' is a prefix, anything else must equal the whole key.
function Get-DirectoryHashes {
    param(
        [Parameter(Mandatory, Position = 0)][string]$Directory,
        [string[]]$Exclude = @()
    )
    $base = (Resolve-Path -LiteralPath $Directory).Path.TrimEnd('\')
    $result = @{}
    foreach ($f in @(Get-ChildItem -LiteralPath $base -Recurse -File -Force)) {
        $key = $f.FullName.Substring($base.Length + 1).Replace('\', '/').ToLowerInvariant()
        $skip = $false
        foreach ($e in $Exclude) {
            $ex = $e.ToLowerInvariant()
            if ($ex.EndsWith('/')) { if ($key.StartsWith($ex)) { $skip = $true; break } }
            elseif ($key -eq $ex) { $skip = $true; break }
        }
        if ($skip) { continue }
        $result[$key] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
    }
    return $result
}
