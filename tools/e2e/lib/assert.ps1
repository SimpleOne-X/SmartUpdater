# Assertion library for the e2e scenarios. Needs common.ps1 (Assert-True, Add-Evidence). ASCII only, LF line endings.
# Every assertion THROWS on failure (message starts with 'ASSERT FAILED:') and returns nothing on success.
# Field names: .smartupdater\state.json is camelCase (currentVersion, skippedVersions),
# the journal is .smartupdater\journal.json, swap leftovers are *.sunew / *.suold.
Set-StrictMode -Version Latest

# Relative path (forward slashes, lower case) -> SHA-256 (upper-case hex). Same contract as Get-DirectoryHashes
# (build.ps1); kept private here so that this library does not depend on another script's file.
# -Exclude accepts relative-path prefixes ending in '/' (for example '.smartupdater/') and exact relative paths.
function Get-AssertTreeHashes([string]$Directory, [string[]]$Exclude = @()) {
    $base = (Resolve-Path -LiteralPath $Directory).Path.TrimEnd('\')
    $result = @{}
    foreach ($file in @(Get-ChildItem -LiteralPath $base -Recurse -File -Force)) {
        $relative = $file.FullName.Substring($base.Length + 1).Replace('\', '/').ToLowerInvariant()
        $skip = $false
        foreach ($rule in $Exclude) {
            $r = $rule.Replace('\', '/').ToLowerInvariant()
            if ($r.EndsWith('/')) { if ($relative.StartsWith($r)) { $skip = $true } }
            elseif ($relative -eq $r) { $skip = $true }
        }
        if ($skip) { continue }
        $result[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return $result
}

function Get-AssertStateFile([hashtable]$Context) {
    $path = Join-Path $Context.InstallDir '.smartupdater\state.json'
    Assert-FileExists $path
    return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

# Compares the install directory with the publish directory in BOTH directions (a file that should have been
# deleted but is still there is a failure, too). -Except lists relative paths (for example a 'preserve'
# file the user edited): they are only required to EXIST in the install directory, their hash is not compared.
# 'Except' means "do not compare content", it does NOT mean "ignore": a missing file still fails.
# Every difference goes to the evidence table, at most 20 per category.
function Assert-InstallMatches([hashtable]$Context, [string]$PublishDir, [string[]]$Except = @()) {
    $exceptKeys = @($Except | ForEach-Object { $_.Replace('\', '/').ToLowerInvariant() })
    $installHashes = Get-AssertTreeHashes $Context.InstallDir @('.smartupdater/')
    $publishHashes = Get-AssertTreeHashes $PublishDir
    $onlyInInstall = New-Object System.Collections.Generic.List[string]
    $onlyInPublish = New-Object System.Collections.Generic.List[string]
    $differs = New-Object System.Collections.Generic.List[string]
    foreach ($key in ($installHashes.Keys | Sort-Object)) {
        if ($exceptKeys -contains $key) { continue }
        if (-not $publishHashes.ContainsKey($key)) { $onlyInInstall.Add($key) }
    }
    foreach ($key in ($publishHashes.Keys | Sort-Object)) {
        if (-not $installHashes.ContainsKey($key)) { $onlyInPublish.Add($key); continue }
        if ($exceptKeys -contains $key) { continue }
        if ($installHashes[$key] -ne $publishHashes[$key]) { $differs.Add($key) }
    }
    foreach ($key in $exceptKeys) {
        # An excepted file that is not in the publish directory is still expected to exist in the install directory.
        if (-not $installHashes.ContainsKey($key) -and -not $onlyInPublish.Contains($key)) { $onlyInPublish.Add($key) }
    }
    $total = $onlyInInstall.Count + $onlyInPublish.Count + $differs.Count
    Add-Evidence $Context ('install vs publish: {0} installed file(s), {1} published file(s), {2} difference(s)' -f $installHashes.Count, $publishHashes.Count, $total)
    if ($total -eq 0) { return }
    foreach ($group in @(@('only-in-install', $onlyInInstall), @('only-in-publish', $onlyInPublish), @('hash-differs', $differs))) {
        foreach ($item in @($group[1] | Select-Object -First 20)) { Add-Evidence $Context ('  {0}: {1}' -f $group[0], $item) }
        if ($group[1].Count -gt 20) { Add-Evidence $Context ('  {0}: ... and {1} more' -f $group[0], ($group[1].Count - 20)) }
    }
    throw ('ASSERT FAILED: install directory does not match the publish directory (only-in-install {0}, only-in-publish {1}, hash-differs {2})' -f $onlyInInstall.Count, $onlyInPublish.Count, $differs.Count)
}

# No half-swapped file may remain: BOTH suffixes are checked.
function Assert-NoSwapLeftovers([hashtable]$Context) {
    $left = @(Get-ChildItem -LiteralPath $Context.InstallDir -Recurse -File -Force |
        Where-Object { $_.Name -like '*.sunew' -or $_.Name -like '*.suold' })
    if ($left.Count -gt 0) {
        $names = @($left | Select-Object -First 20 | ForEach-Object { $_.FullName })
        foreach ($n in $names) { Add-Evidence $Context ('  swap leftover: ' + $n) }
        throw ('ASSERT FAILED: {0} swap leftover(s) (*.sunew / *.suold) in the install directory, first: {1}' -f $left.Count, $names[0])
    }
}

function Assert-JournalConverged([hashtable]$Context) {
    $path = Join-Path $Context.InstallDir '.smartupdater\journal.json'
    if (Test-Path -LiteralPath $path) { throw "ASSERT FAILED: journal.json still exists (the update did not converge): $path" }
}

# Compares the currentVersion STRING. Not [version]: PowerShell's [version] treats 1.1.0 and 1.1.0.0 as different
# in some places and equal in others, which would hide a wrong-shaped value.
function Assert-StateVersion([hashtable]$Context, [string]$Version) {
    $state = Get-AssertStateFile $Context
    $actual = $null
    if ($state.PSObject.Properties['currentVersion']) { $actual = $state.currentVersion }
    if (-not ([string]$actual -ceq $Version)) { throw "ASSERT FAILED: state.json currentVersion (expected '$Version', actual '$actual')" }
}

function Assert-StateSkipped([hashtable]$Context, [string[]]$Versions) {
    $state = Get-AssertStateFile $Context
    $actual = @()
    if ($state.PSObject.Properties['skippedVersions'] -and $null -ne $state.skippedVersions) { $actual = @($state.skippedVersions | ForEach-Object { [string]$_ }) }
    $expected = @($Versions | ForEach-Object { [string]$_ })
    $a = (@($actual | Sort-Object) -join ',')
    $e = (@($expected | Sort-Object) -join ',')
    if ($a -cne $e) { throw "ASSERT FAILED: state.json skippedVersions (expected [$e], actual [$a])" }
}

function Get-AssertAppId([hashtable]$Context) {
    $appId = $null
    if ($Context.ContainsKey('AppId')) { $appId = $Context.AppId }
    if ([string]::IsNullOrWhiteSpace($appId)) { throw 'ASSERT FAILED: the AppId was not read from the sample event log (Context.AppId is empty)' }
    return [string]$appId
}

function Assert-DownloadCacheEmpty([hashtable]$Context) {
    $appId = Get-AssertAppId $Context
    $dir = Join-Path (Join-Path $Context.LocalAppData $appId) 'updates'
    if (-not (Test-Path -LiteralPath $dir)) { return }
    $items = @(Get-ChildItem -LiteralPath $dir -Recurse -Force)
    if ($items.Count -gt 0) { throw ('ASSERT FAILED: download cache is not empty ({0} item(s), first: {1})' -f $items.Count, $items[0].FullName) }
}

# The REAL %LOCALAPPDATA% (Known Folder API, same as the sample) must not have a directory for this run's AppId.
# An empty AppId is a failure by itself: "did not read it" must never pass silently.
function Assert-NoRealLocalAppData([hashtable]$Context) {
    $appId = Get-AssertAppId $Context
    $real = [Environment]::GetFolderPath('LocalApplicationData')
    $leak = Join-Path $real $appId
    if (Test-Path -LiteralPath $leak) { throw "ASSERT FAILED: the real LocalAppData contains a directory for this run: $leak" }
}

# Passes when at least one report received by the MockServer matches EVERY given filter.
function Assert-ReportSeen($Server, [string]$EventType, [string]$From, [string]$To, [string]$Stage, $IsSuccess = $null) {
    $reports = @(Get-MockServerReports $Server)
    $hits = @($reports | Where-Object {
        $r = $_
        $ok = $true
        if ($EventType -and [string]$r.eventType -ne $EventType) { $ok = $false }
        if ($ok -and $From -and [string]$r.fromVersion -ne $From) { $ok = $false }
        if ($ok -and $To -and [string]$r.toVersion -ne $To) { $ok = $false }
        if ($ok -and $Stage -and [string]$r.stage -ne $Stage) { $ok = $false }
        if ($ok -and $null -ne $IsSuccess -and [bool]$r.isSuccess -ne [bool]$IsSuccess) { $ok = $false }
        $ok
    })
    if ($hits.Count -eq 0) {
        $seen = @($reports | ForEach-Object { '{0}/{1}->{2}/{3}/{4}' -f $_.eventType, $_.fromVersion, $_.toVersion, $_.stage, $_.isSuccess }) -join '; '
        throw ("ASSERT FAILED: no report matches eventType='$EventType' from='$From' to='$To' stage='$Stage' isSuccess='$IsSuccess'; received: [$seen]")
    }
}

# Exists, is larger than 5000 bytes and is not (nearly) a single colour: the sample is at most 60 x 60 grid
# points (step = side / 60); 2 colours or fewer counts as blank.
function Assert-Screenshot([string]$Path) {
    Assert-FileExists $Path
    $length = (Get-Item -LiteralPath $Path).Length
    if ($length -le 5000) { throw "ASSERT FAILED: screenshot is too small to be real ($length bytes): $Path" }
    Add-Type -AssemblyName System.Drawing
    $bitmap = $null
    try {
        $bitmap = New-Object System.Drawing.Bitmap $Path
        $stepX = [Math]::Max(1, [int][Math]::Floor($bitmap.Width / 60))
        $stepY = [Math]::Max(1, [int][Math]::Floor($bitmap.Height / 60))
        $colors = @{}
        for ($y = 0; $y -lt $bitmap.Height; $y += $stepY) {
            for ($x = 0; $x -lt $bitmap.Width; $x += $stepX) { $colors[$bitmap.GetPixel($x, $y).ToArgb()] = $true }
        }
        if ($colors.Count -le 2) { throw "ASSERT FAILED: screenshot looks blank (only $($colors.Count) sampled colour(s)): $Path" }
    }
    finally { if ($bitmap) { $bitmap.Dispose() } }
}

# The local rolling log must be able to reconstruct the whole run: every pattern (regex) has to match
# somewhere in the concatenated .smartupdater\logs\updater-*.log files.
function Assert-LogCovers([hashtable]$Context, [string[]]$Patterns) {
    $logDir = Join-Path $Context.InstallDir '.smartupdater\logs'
    $files = @()
    if (Test-Path -LiteralPath $logDir) { $files = @(Get-ChildItem -LiteralPath $logDir -Filter 'updater-*.log' -File) }
    if ($files.Count -eq 0) { throw "ASSERT FAILED: no updater-*.log under $logDir" }
    $text = (@($files | Sort-Object Name | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }) -join "`n")
    $missing = @($Patterns | Where-Object { $text -notmatch $_ })
    if ($missing.Count -gt 0) { throw ('ASSERT FAILED: the local log does not cover: ' + ($missing -join ' | ')) }
}
