# S1 - optional update, user answers "update now".
# ASCII only, LF line endings. Array-returning helpers are UNROLLED: every call is wrapped in @(...)
# (calling convention in the header of lib\common.ps1).

$ScenarioId = 'S1'
$ScenarioName = 'optional update accepted'

function Invoke-Scenario([hashtable]$Context) {
    Write-Step 'publish 1.0.0 and 1.1.0 (one file changed, one removed, one added)'
    $v1 = New-SampleRelease -Context $Context -Version '1.0.0'
    $v2 = New-SampleRelease -Context $Context -Version '1.1.0' -Mutate {
        param($dir)
        Set-Content -LiteralPath (Join-Path $dir 'appsettings.json') -Encoding Ascii -Value '{ "userNote": "shipped by 1.1.0" }'
        Set-Content -LiteralPath (Join-Path $dir 'release-notes.txt') -Encoding Ascii -Value 'added in 1.1.0'
        Remove-Item -LiteralPath (Join-Path $dir 'THIRD-PARTY-NOTICES.txt') -Force
    }

    Write-Step 'pack both versions'
    Invoke-Pack -Context $Context -PublishDir $v1 -Version '1.0.0' -Preserve @('appsettings.json') | Out-Null
    Invoke-Pack -Context $Context -PublishDir $v2 -Version '1.1.0' -Preserve @('appsettings.json') -Notes 'S1 release' | Out-Null

    Write-Step 'start MockServer over the releases directory'
    $server = Start-MockServer -Context $Context -Root $Context.ReleasesDir
    $Context.Server = $server
    try {
        Reset-MockServer $server

        Write-Step 'install 1.0.0 and let the "user" edit the preserve file'
        Install-Release -Context $Context -PublishDir $v1
        # Install-Release only copies the publish output, so the install has no .smartupdater\manifest.json
        # and the update would delete NOTHING (missing manifest = treat as a first
        # install). S1 must see THIRD-PARTY-NOTICES.txt disappear, so the install has to look the way
        # SmartUpdater itself would have left it: seed the manifest from the 1.0.0 package.
        Set-SeedManifest -Context $Context -Version '1.0.0' | Out-Null
        $userNote = '{ "userNote": "edited by the user" }'
        Set-Content -LiteralPath (Join-Path $Context.InstallDir 'appsettings.json') -Encoding Ascii -Value $userNote

        Write-Step 'start the sample; it will be offered 1.1.0 and answer "now"'
        $p = Start-Sample -Context $Context -FeedUrl $server.FeedUrl -AnswerModal now -ShotDir $Context.ShotDir `
             -ExtraArgs @('--report-url', $server.ReportUrl, '--poll-seconds', '2', '--jitter-seconds', '0', '--exit-after-seconds', '600')
        $oldPid = $p.Id

        Wait-SampleEvent -Context $Context -EventName 'started' -With @{ version = '1.0.0.0' } -TimeoutSec 120
        $Context.AppId = Get-EventValue @(Get-EventLines @(Read-EventLog $Context.EventLog) 'started')[0] 'app-id'
        Assert-True ($Context.AppId) 'the sample did not report its app-id'

        Write-Step 'the modal really pops up (optional mode)'
        Wait-SampleEvent -Context $Context -EventName 'offered' -With @{ version = '1.1.0.0'; mode = 'optional' } -TimeoutSec 180
        Wait-SampleEvent -Context $Context -EventName 'answered' -With @{ choice = 'now' } -TimeoutSec 60

        Write-Step 'the package reaches the point of no return and restarts the app'
        Wait-SampleEvent -Context $Context -EventName 'restarting' -With @{ version = '1.1.0.0' } -TimeoutSec 180
        Wait-ProcessExit $oldPid -TimeoutSec 120

        Write-Step 'the NEW process reports itself as 1.1.0.0, updated from 1.0.0.0'
        Wait-SampleEvent -Context $Context -EventName 'started' -With @{ version = '1.1.0.0'; 'updated-from' = '1.0.0.0' } -TimeoutSec 120
        $startedLines = @(Get-EventLines @(Read-EventLog $Context.EventLog) 'started')
        $newPid = Get-EventValue $startedLines[$startedLines.Count - 1] 'pid'
        Assert-True ([int]$newPid -ne $oldPid) 'the pid did not change - the app did not actually restart'

        Write-Step 'the install directory matches the 1.1.0 publish output, except the preserve file'
        Assert-InstallMatches -Context $Context -PublishDir $v2 -Except @('appsettings.json')
        Assert-Equal $userNote (Get-Content -LiteralPath (Join-Path $Context.InstallDir 'appsettings.json') -Raw).TrimEnd() `
                     'the preserve file was overwritten'
        Assert-FileExists (Join-Path $Context.InstallDir 'release-notes.txt')
        Assert-NoFile (Join-Path $Context.InstallDir 'THIRD-PARTY-NOTICES.txt')

        Write-Step 'no .sunew / .suold left behind, journal converged, state updated'
        Assert-NoSwapLeftovers -Context $Context
        Assert-JournalConverged -Context $Context
        # Exactly what the applier writes: the package manifest's version, verbatim ('1.1.0', not '1.1.0.0').
        # Normalising to four segments happens only on the client's decision boundaries (VersionNormalization);
        # the apply layer stores what the publisher wrote, and unit tests pin that
        # (tests\SmartUpdater.Tests\ApplyEndToEndTests.cs). The four-segment identity of the running
        # app is asserted above, on the 'started' event (version=1.1.0.0), which is where it matters.
        Assert-StateVersion -Context $Context -Version '1.1.0'
        Assert-DownloadCacheEmpty -Context $Context
        Assert-NoRealLocalAppData -Context $Context

        Write-Step 'MockServer received exactly one Updated report'
        Wait-Until -TimeoutSec 120 -Description 'an Updated report arrives' -Condition {
            @(Get-MockServerReports $server | Where-Object { $_.eventType -eq 'Updated' }).Count -ge 1
        }
        Assert-ReportSeen $server -EventType 'Updated' -From '1.0.0.0' -To '1.1.0.0' -IsSuccess $true

        Write-Step 'the local rolling log can reconstruct the whole run'
        # The package logs in Chinese, and these scripts must stay ASCII, so match the parts of a log line
        # that ARE ascii: the stage tag ('| Verify |', '| Commit |') and the 64-hex digest that the
        # verification step prints. Matching a literal 'sha256'/'hash' would never fire - the message is
        # "<chinese for 'package hash matches'>: <hex>" - and would only pin the scenario to a word the
        # package does not write.
        Assert-LogCovers -Context $Context -Patterns @(
            '1\.1\.0',                  # the candidate version was logged
            '\| Verify \| ',            # the verification step ran
            '\b[0-9a-f]{64}\b',         # ... and the digest it compared is in the log
            '\| Commit \| ',
            'journal: committing . done'    # the '.' is the arrow U+2192, which may not be written here
        )

        Write-Step 'no cross-thread violation was ever recorded'
        Assert-Equal 0 @(Get-EventLines @(Read-EventLog $Context.EventLog) 'cross-thread-violation').Count `
                     'an event handler ran off the UI thread'

        Write-Step 'screenshots'
        # The after-update shot is taken ~0.8 s after the new window is Shown (the sample waits for AntdUI to
        # paint its first frame), which is LATER than the 'started' event this scenario waited for. Wait for
        # the shot event itself - it is written only after TrySave returned true, so the png is complete.
        Wait-Until -TimeoutSec 120 -Description "event 'shot' for 03-after.png" -Condition {
            @(Get-EventLines @(Read-EventLog $Context.EventLog) 'shot' | Where-Object { $_ -match '03-after\.png' }).Count -ge 1
        }
        $before = Join-Path $Context.ShotDir '01-before.png'
        $modal = Join-Path $Context.ShotDir '02-modal.png'
        Assert-Screenshot $before
        Assert-Screenshot $modal
        Assert-Screenshot (Join-Path $Context.ShotDir '03-after.png')

        # The "before" shot is taken 0.8 s after Shown (AntdUI needs that long to paint its first frame),
        # but a local feed offers the update after ~0.3 s, so without a hold-back the shot would catch the modal and be
        # byte-identical to 02-modal.png. E2eHooks.WaitForFirstShotAsync holds the first check back until
        # the shot is on disk; these two assertions are what pins that - order first, bytes second.
        $lines = @(Read-EventLog $Context.EventLog)
        $beforeIndex = -1
        $offeredIndex = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($beforeIndex -lt 0 -and $lines[$i] -match 'event=shot ' -and $lines[$i] -match '01-before\.png') { $beforeIndex = $i }
            if ($offeredIndex -lt 0 -and $lines[$i] -match 'event=offered ') { $offeredIndex = $i }
        }
        Assert-True ($beforeIndex -ge 0) 'no shot event for 01-before.png'
        Assert-True ($offeredIndex -ge 0) 'no offered event in the event log'
        Assert-True ($beforeIndex -lt $offeredIndex) 'the before-update shot was taken after the update had been offered'
        Assert-True ((Get-FileHash -LiteralPath $before -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $modal -Algorithm SHA256).Hash) `
                    'the before-update shot is byte-identical to the modal shot (it caught the modal)'

        Add-Evidence $Context ('S1: 1.0.0 -> 1.1.0, old pid ' + $oldPid + ' -> new pid ' + $newPid)
        Add-Evidence $Context ('S1: shots at ' + $Context.ShotDir)
    }
    finally { Stop-MockServer $server }
}
