# S0C - MockServer self-check: start on a dynamic port, serve a feed, honour ETag, accept a report,
# inject a fault and recover. No sample, no publish. ASCII only, LF line endings.

$ScenarioId = 'S0C'
$ScenarioName = 'MockServer self-check'

function Invoke-Scenario([hashtable]$Context) {
    $srvRoot = Join-Path $Context.Root 'wwwroot'
    New-Item -ItemType Directory -Force -Path (Join-Path $srvRoot 'packages') | Out-Null
    Set-Content -LiteralPath (Join-Path $srvRoot 'releases.json') -Encoding Ascii -Value '{"schemaVersion":1,"channel":"stable","releases":[]}'

    Write-Step 'a server that cannot start fails fast with its own output (no 120 s wait, no hang)'
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $startError = ''
    try { Start-MockServer -Context $Context -Root (Join-Path $Context.Root 'does-not-exist') | Out-Null }
    catch { $startError = $_.Exception.Message }
    Assert-True ($startError -like '*before printing LISTENING*') "expected a start failure that names the missing LISTENING line, got: $startError"
    Assert-True ($startError -like '*does-not-exist*') 'the start failure must carry the server stderr (it names the bad root)'
    Assert-True ($clock.Elapsed.TotalSeconds -lt 90) 'a dead server must be noticed at once, not after the LISTENING timeout'

    Write-Step 'server starts on a dynamic loopback port'
    $server = Start-MockServer -Context $Context -Root $srvRoot
    $Context.Server = $server
    try {
        Assert-True ($server.BaseUrl -match '^http://127\.0\.0\.1:\d+/$') "unexpected base url: $($server.BaseUrl)"
        Assert-True ($server.BaseUrl -notmatch ':0/$') 'port 0 was not replaced by the real port'
        Assert-Equal ($server.BaseUrl + 'releases.json') $server.FeedUrl 'feed url'
        Assert-Equal ($server.BaseUrl + 'api/v1/update-reports') $server.ReportUrl 'report url'
        Write-Step ('LISTENING ' + $server.BaseUrl)
        Add-Evidence $Context ('LISTENING ' + $server.BaseUrl)

        Write-Step 'the server command line is findable under the scenario root (cleanup safety net)'
        $found = @(Get-ProcessesUnder $Context.Root -IncludeDescendants | Where-Object { $_.Name -eq 'dotnet.exe' -or $_.Name -like 'MockServer*' })
        Assert-True ($found.Count -ge 1) 'no MockServer process is discoverable under the scenario root'

        Write-Step 'feed is served and carries a strong ETag'
        $r1 = Invoke-WebRequest -Uri $server.FeedUrl -UseBasicParsing
        Assert-Equal 200 ([int]$r1.StatusCode) 'feed status'
        $etag = $r1.Headers['ETag']
        Assert-True ($etag -and -not $etag.StartsWith('W/')) "expected a strong ETag, got '$etag'"

        Write-Step 'If-None-Match yields 304 with an empty body'
        $status304 = 0
        try {
            $r2 = Invoke-WebRequest -Uri $server.FeedUrl -Headers @{ 'If-None-Match' = $etag } -UseBasicParsing
            $status304 = [int]$r2.StatusCode
        }
        catch [System.Net.WebException] { $status304 = [int]$_.Exception.Response.StatusCode }
        Assert-Equal 304 $status304 'expected 304'

        Write-Step 'report endpoint accepts a POST and it can be read back'
        Clear-MockServerReports $server
        $body = '{"eventType":"Heartbeat","deviceGuid":"11111111-1111-1111-1111-111111111111"}'
        $r3 = Invoke-WebRequest -Uri $server.ReportUrl -Method Post -Body $body -ContentType 'application/json' -UseBasicParsing
        Assert-Equal 202 ([int]$r3.StatusCode) 'report must be 202 Accepted'
        $reports = @(Get-MockServerReports $server)
        Assert-Equal 1 $reports.Count 'one report expected'
        Assert-Equal 'Heartbeat' $reports[0].eventType 'eventType round-trip'
        Clear-MockServerReports $server
        Assert-Equal 0 @(Get-MockServerReports $server).Count 'reports were not cleared'

        Write-Step 'fault injection and recovery'
        Set-Fault $server -Path '/releases.json' -Status 503 -RetryAfterSeconds 1
        $status = 0
        try { Invoke-WebRequest -Uri $server.FeedUrl -UseBasicParsing | Out-Null }
        catch [System.Net.WebException] { $status = [int]$_.Exception.Response.StatusCode }
        Assert-Equal 503 $status 'fault was not applied'
        # The control plane must stay reachable while a fault is armed.
        Reset-MockServer $server
        Assert-Equal 200 ([int](Invoke-WebRequest -Uri $server.FeedUrl -UseBasicParsing).StatusCode) 'reset did not restore the feed'

        Write-Step 'Clear-Faults lifts a fault; a bad control request throws with the server message'
        Set-Fault $server -Path '/releases.json' -Status 500 -FailCount 5
        Clear-Faults $server
        Assert-Equal 200 ([int](Invoke-WebRequest -Uri $server.FeedUrl -UseBasicParsing).StatusCode) 'Clear-Faults did not lift the fault'
        $threw = $false
        try { Set-Fault $server -Path '/releases.json' -Status 200 }
        catch { $threw = $true }
        Assert-True $threw 'a 400 from the control plane must throw'

        Write-Step 'transfer settings can be set and cleared'
        Set-Transfer $server -Path '/packages/x.zip' -BytesPerSecond 1024 -CutAfterBytes 10
        Clear-Transfer $server

        Write-Step 'feed overlay changes the ETag (so the client really re-reads it)'
        $before = (Invoke-WebRequest -Uri $server.FeedUrl -UseBasicParsing).Headers['ETag']
        Set-FeedOverlay $server -Body @{ body = '{"schemaVersion":1,"channel":"stable","releases":[]} ' }
        $after = (Invoke-WebRequest -Uri $server.FeedUrl -UseBasicParsing).Headers['ETag']
        Assert-True ($before -ne $after) 'overlay must change the ETag'
        Set-FeedOverlay $server -Body @{ body = ' {"schemaVersion":1,"channel":"stable","releases":[]}' }
        $again = (Invoke-WebRequest -Uri $server.FeedUrl -UseBasicParsing).Headers['ETag']
        Assert-True ($after -ne $again) 'two same-length overlays in a row must not share an ETag'
        Clear-FeedOverlay $server
    }
    finally { Stop-MockServer $server }

    Write-Step 'server process is gone as soon as Stop-MockServer returns'
    Assert-True $server.Process.HasExited 'MockServer host process is still running right after Stop-MockServer'
    Assert-Equal 0 @(Get-ProcessesUnder $Context.Root -IncludeDescendants).Count 'a process is still running under the scenario root'
    Assert-NoFile $server.StdOut
    Add-Evidence $Context ('S0C: MockServer at ' + $server.BaseUrl)
}
