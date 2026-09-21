# Sample launch / event-log / seed-state helpers. ASCII only, LF.
Set-StrictMode -Version Latest

function Start-Sample {
    param(
        [Parameter(Mandatory)][hashtable]$Context,
        [Parameter(Mandatory)][string]$FeedUrl,
        [ValidateSet('now', 'later', 'skip')][string]$AnswerModal,
        [string]$ShotDir,
        [switch]$HangShutdown,
        [string[]]$ExtraArgs = @()
    )
    $exe = Join-Path $Context.InstallDir 'MinimalApp.WinForms.exe'
    Assert-FileExists $exe
    # --event-log goes before $ExtraArgs so that a caller can override it.
    $argList = @(
        '--feed-url', $FeedUrl,
        '--event-log', $Context.EventLog,
        '--local-app-data', $Context.LocalAppData
    )
    if ($AnswerModal) { $argList += @('--answer-modal', $AnswerModal) }
    if ($ShotDir) { $argList += @('--shot-dir', $ShotDir) }
    if ($HangShutdown) { $argList += '--hang-shutdown' }
    $argList += $ExtraArgs
    return Start-Process -FilePath $exe -ArgumentList $argList -PassThru
}

# Waits until an event line with the given key=value pairs shows up in the sample's event log.
function Wait-SampleEvent {
    param([hashtable]$Context, [string]$EventName, [hashtable]$With = @{}, [int]$TimeoutSec = 180)
    $desc = "event '$EventName' " + (($With.GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value }) -join ' ')
    Wait-Until -TimeoutSec $TimeoutSec -Description $desc -Condition {
        $lines = @(Get-EventLines @(Read-EventLog $Context.EventLog) $EventName)
        foreach ($l in $lines) {
            $ok = $true
            foreach ($kv in $With.GetEnumerator()) {
                if ((Get-EventValue $l $kv.Key) -ne $kv.Value) { $ok = $false; break }
            }
            if ($ok) { return $true }
        }
        return $false
    }
}

# Copies .smartupdater/manifest.json out of the package that produced the current install, i.e. it leaves
# behind exactly what the applier would have written had SmartUpdater installed this version itself.
#
# Why this is needed: Install-Release only copies the publish output (no .smartupdater folder), which is
# the updater's "first install" path - and there, a missing local manifest means the next update DELETES
# NOTHING (treated as a first install: delete nothing, rewrite everything). A scenario
# that wants to see a file the old version owned disappear (S1: THIRD-PARTY-NOTICES.txt) must therefore
# install a version that already has its manifest, otherwise the stale file stays behind forever.
# The manifest is taken from the real package, never hand-written: a hand-written one could disagree with
# what the installed files actually are and would silently change what the update deletes.
function Set-SeedManifest {
    param(
        [Parameter(Mandatory)][hashtable]$Context,
        [Parameter(Mandatory)][string]$Version,
        [string]$PackageName = 'MinimalApp.WinForms'
    )
    $zip = Join-Path $Context.ReleasesDir ('packages\' + $PackageName + '-' + $Version + '.zip')
    Assert-FileExists $zip
    $dir = Join-Path $Context.InstallDir '.smartupdater'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $target = Join-Path $dir 'manifest.json'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entry = $archive.GetEntry('.smartupdater/manifest.json')
        if ($null -eq $entry) { throw "package $zip has no .smartupdater/manifest.json" }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
    finally { $archive.Dispose() }
    return $target
}

# Seeds .smartupdater/state.json before the first launch (gives a scenario a deterministic rollout bucket).
# Field names follow UpdateState (camelCase). UTF-8 without BOM, like the package's own writer.
function Set-SeedState {
    param(
        [Parameter(Mandatory)][hashtable]$Context,
        [Parameter(Mandatory)][string]$DeviceGuid,
        [Parameter(Mandatory)][string]$CurrentVersion,
        [string[]]$SkippedVersions = @()
    )
    $dir = Join-Path $Context.InstallDir '.smartupdater'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $skipped = '[' + ((@($SkippedVersions) | ForEach-Object { '"' + $_ + '"' }) -join ', ') + ']'
    $json = '{' + "`n" +
        '  "currentVersion": "' + $CurrentVersion + '",' + "`n" +
        '  "deviceGuid": "' + $DeviceGuid + '",' + "`n" +
        '  "skippedVersions": ' + $skipped + ',' + "`n" +
        '  "feedETag": null,' + "`n" +
        '  "lastCheckedAt": null,' + "`n" +
        '  "lastReportedAt": null' + "`n" +
        '}' + "`n"
    [System.IO.File]::WriteAllText((Join-Path $dir 'state.json'), $json, (New-Object System.Text.UTF8Encoding $false))
}
