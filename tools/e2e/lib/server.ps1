# MockServer helpers: start / stop and the control plane (/_control/*). Needs common.ps1 (Wait-Until,
# Invoke-Native) and process.ps1 (Get-ProcessesUnder, Stop-ProcessesUnder). ASCII only, LF line endings.
#
# Calling convention: Get-MockServerReports returns its items UNROLLED (see the header of common.ps1), so
# the caller wraps it in @(...).
Set-StrictMode -Version Latest

# Built once per run (the first call builds, later calls use --no-build).
$script:MockServerBuilt = $false

function Get-MockServerProject([hashtable]$Context) {
    return (Join-Path $Context.RepoRoot 'samples\MockServer\MockServer.csproj')
}

# Kills a process and everything below it (dotnet run starts the real server as a child).
function Stop-ProcessTree([int]$ProcessId) {
    $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    $ids = New-Object System.Collections.Generic.List[int]
    $ids.Add($ProcessId)
    for ($i = 0; $i -lt $ids.Count; $i++) {
        foreach ($c in $all) {
            if ($c.ParentProcessId -eq $ids[$i] -and -not $ids.Contains([int]$c.ProcessId) -and $c.ProcessId -ne $PID) { $ids.Add([int]$c.ProcessId) }
        }
    }
    for ($i = $ids.Count - 1; $i -ge 0; $i--) {
        Stop-Process -Id $ids[$i] -Force -ErrorAction SilentlyContinue
    }
    foreach ($id in $ids) {
        Wait-Until -TimeoutSec 30 -Description "process $id exits" -Condition { -not (Get-Process -Id $id -ErrorAction SilentlyContinue) }
    }
}

function Start-MockServer {
    param(
        [Parameter(Mandatory)][hashtable]$Context,
        [Parameter(Mandatory)][string]$Root
    )
    $csproj = Get-MockServerProject $Context
    if (-not $script:MockServerBuilt) {
        $build = Invoke-Native { dotnet build $csproj -c Release --nologo }
        if ($build.ExitCode -ne 0) { throw ("MockServer build failed (exit {0}):`n{1}" -f $build.ExitCode, ($build.Output -join "`n")) }
        $script:MockServerBuilt = $true
    }

    # The stdout file lives under the scenario root, so cleanup removes it even if Stop-MockServer never runs.
    $stdout = Join-Path $Context.Root ('mockserver-' + [guid]::NewGuid().ToString('N') + '.out')
    $stderr = $stdout + '.err'
    $argList = @('run', '--project', ('"' + $csproj + '"'), '-c', 'Release', '--no-build', '--no-launch-profile', '--', '--root', ('"' + $Root + '"'), '--port', '0')
    $p = Start-Process -FilePath 'dotnet' -ArgumentList $argList -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    $null = $p.Handle      # keeps ExitCode readable after the process is gone
    $state = @{ Url = $null }
    try {
        Wait-Until -TimeoutSec 120 -Description 'MockServer prints LISTENING' -Condition {
            if (Test-Path -LiteralPath $stdout) {
                # Line by line: the line ends in CRLF, and -Raw with '$' would not match before the CR.
                foreach ($line in @(Get-Content -LiteralPath $stdout -Encoding UTF8)) {
                    $m = [regex]::Match($line, '^LISTENING (\S+)\s*$')
                    if ($m.Success) { $state.Url = $m.Groups[1].Value; return $true }
                }
            }
            if ($p.HasExited) {
                $out = if (Test-Path -LiteralPath $stdout) { (Get-Content -LiteralPath $stdout -Raw) } else { '' }
                $err = if (Test-Path -LiteralPath $stderr) { (Get-Content -LiteralPath $stderr -Raw) } else { '' }
                throw ("MockServer exited with code {0} before printing LISTENING.`nstdout: {1}`nstderr: {2}" -f $p.ExitCode, $out, $err)
            }
            return $false
        }
    }
    catch {
        try { Stop-ProcessTree $p.Id } catch { Write-Diag ('could not stop the MockServer: ' + $_.Exception.Message) }
        throw
    }

    $url = $state.Url
    if (-not $url.EndsWith('/')) { $url += '/' }
    return @{
        Process = $p; BaseUrl = $url
        FeedUrl = $url + 'releases.json'
        ReportUrl = $url + 'api/v1/update-reports'
        StdOut = $stdout; Root = $Root
    }
}

# Stops the server tree and only returns once it is gone; then removes the stdout files.
function Stop-MockServer($Server) {
    if ($null -eq $Server) { return }
    try {
        if (-not $Server.Process.HasExited) { Stop-ProcessTree $Server.Process.Id }
        $Server.Process.WaitForExit()
    }
    finally {
        Remove-Item -LiteralPath $Server.StdOut -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath ($Server.StdOut + '.err') -Force -ErrorAction SilentlyContinue
    }
}

# Calls /_control/<Path>. Any non-2xx throws, with the response body (the mock server explains a 400 there).
# Returns the response body text ('' for 204).
function Invoke-Control {
    param(
        [Parameter(Mandatory)]$Server,
        [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'DELETE')][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        $Body = $null
    )
    $uri = $Server.BaseUrl + '_control/' + $Path
    $args2 = @{ Uri = $uri; Method = $Method; UseBasicParsing = $true }
    if ($Method -eq 'POST' -and $null -ne $Body) {
        $args2.Body = (ConvertTo-Json -InputObject $Body -Depth 8 -Compress)
        $args2.ContentType = 'application/json'
    }
    try { $response = Invoke-WebRequest @args2 }
    catch [System.Net.WebException] {
        $text = ''
        $webResponse = $_.Exception.Response
        if ($null -ne $webResponse) {
            $reader = New-Object System.IO.StreamReader($webResponse.GetResponseStream())
            try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
            throw ('control call {0} {1} failed with {2}: {3}' -f $Method, $uri, [int]$webResponse.StatusCode, $text)
        }
        throw ('control call {0} {1} failed: {2}' -f $Method, $uri, $_.Exception.Message)
    }
    return [string]$response.Content
}

function Get-MockServerReports($Server) {
    $text = Invoke-Control $Server -Method GET -Path 'reports'
    if ([string]::IsNullOrWhiteSpace($text)) { return }
    $items = ConvertFrom-Json -InputObject $text
    foreach ($i in @($items)) { $i }        # unrolled on purpose
}

function Clear-MockServerReports($Server) { Invoke-Control $Server -Method DELETE -Path 'reports' | Out-Null }
function Reset-MockServer($Server) { Invoke-Control $Server -Method POST -Path 'reset' | Out-Null }
function Set-FeedOverlay($Server, [Parameter(Mandatory)][hashtable]$Body) { Invoke-Control $Server -Method POST -Path 'feed-overlay' -Body $Body | Out-Null }
function Clear-FeedOverlay($Server) { Invoke-Control $Server -Method DELETE -Path 'feed-overlay' | Out-Null }
function Clear-Faults($Server) { Invoke-Control $Server -Method DELETE -Path 'faults' | Out-Null }
function Clear-Transfer($Server) { Invoke-Control $Server -Method DELETE -Path 'transfer' | Out-Null }

function Set-Fault {
    param(
        [Parameter(Mandatory)]$Server,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int]$Status,
        [int]$RetryAfterSeconds = -1,
        [int]$FailCount = -1
    )
    $body = @{ path = $Path; status = $Status }
    if ($RetryAfterSeconds -ge 0) { $body.retryAfterSeconds = $RetryAfterSeconds }
    if ($FailCount -ge 0) { $body.failCount = $FailCount }
    Invoke-Control $Server -Method POST -Path 'faults' -Body $body | Out-Null
}

function Set-Transfer {
    param(
        [Parameter(Mandatory)]$Server,
        [Parameter(Mandatory)][string]$Path,
        [int]$BytesPerSecond = -1,
        [long]$CutAfterBytes = -1
    )
    $body = @{ path = $Path }
    if ($BytesPerSecond -ge 0) { $body.bytesPerSecond = $BytesPerSecond }
    if ($CutAfterBytes -ge 0) { $body.cutAfterBytes = $CutAfterBytes }
    Invoke-Control $Server -Method POST -Path 'transfer' -Body $body | Out-Null
}
