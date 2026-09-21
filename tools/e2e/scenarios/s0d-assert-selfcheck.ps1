# S0D - assertion library self-check. Uses made-up directories only (no build, no server, no window).
# Every assertion is proven in both directions: passes when it should, fails when it should.
# ASCII only, LF line endings.

$ScenarioId = 'S0D'
$ScenarioName = 'assertion library self-check'

function Assert-Throws([scriptblock]$Action, [string]$Because) {
    $threw = $false
    try { & $Action } catch { $threw = $true }
    Assert-True $threw "expected an assertion failure: $Because"
}

function Invoke-Scenario([hashtable]$Context) {
    $Context.InstallDir = Join-Path $Context.Root 'app'
    $Context.LocalAppData = Join-Path $Context.Root 'localappdata'
    $publish = Join-Path $Context.Root 'publish'
    New-Item -ItemType Directory -Force -Path (Join-Path $Context.InstallDir '.smartupdater'), $publish, $Context.LocalAppData | Out-Null
    Set-Content -LiteralPath (Join-Path $publish 'a.txt') -Encoding Ascii -Value 'same'
    Set-Content -LiteralPath (Join-Path $publish 'appsettings.json') -Encoding Ascii -Value '{"userNote":"shipped"}'
    Copy-Item (Join-Path $publish '*') $Context.InstallDir -Recurse -Force

    Write-Step 'Assert-InstallMatches passes on identical trees and ignores .smartupdater'
    Set-Content -LiteralPath (Join-Path $Context.InstallDir '.smartupdater\state.json') -Encoding Ascii -Value '{}'
    Assert-InstallMatches -Context $Context -PublishDir $publish

    Write-Step 'a changed file is caught'
    Set-Content -LiteralPath (Join-Path $Context.InstallDir 'a.txt') -Encoding Ascii -Value 'tampered'
    Assert-Throws { Assert-InstallMatches -Context $Context -PublishDir $publish } 'a.txt differs'
    Set-Content -LiteralPath (Join-Path $Context.InstallDir 'a.txt') -Encoding Ascii -Value 'same'

    Write-Step 'a preserve file may differ when listed in -Except, but must still exist'
    Set-Content -LiteralPath (Join-Path $Context.InstallDir 'appsettings.json') -Encoding Ascii -Value '{"userNote":"edited by the user"}'
    Assert-InstallMatches -Context $Context -PublishDir $publish -Except @('appsettings.json')
    Remove-Item -LiteralPath (Join-Path $Context.InstallDir 'appsettings.json') -Force
    Assert-Throws { Assert-InstallMatches -Context $Context -PublishDir $publish -Except @('appsettings.json') } 'preserve file deleted'
    Set-Content -LiteralPath (Join-Path $Context.InstallDir 'appsettings.json') -Encoding Ascii -Value '{"userNote":"edited"}'

    Write-Step 'an extra file in the install dir is caught'
    Set-Content -LiteralPath (Join-Path $Context.InstallDir 'stray.txt') -Encoding Ascii -Value 'x'
    Assert-Throws { Assert-InstallMatches -Context $Context -PublishDir $publish -Except @('appsettings.json') } 'stray file'
    Remove-Item -LiteralPath (Join-Path $Context.InstallDir 'stray.txt') -Force

    Write-Step 'differences are listed in the evidence table'
    Assert-True (@($Context.Evidence | Where-Object { $_ -like '*only-in-install: stray.txt' }).Count -ge 1) 'stray.txt missing from the evidence'
    Assert-True (@($Context.Evidence | Where-Object { $_ -like '*hash-differs: a.txt' }).Count -ge 1) 'a.txt missing from the evidence'

    Write-Step 'swap leftovers'
    Assert-NoSwapLeftovers -Context $Context
    Set-Content -LiteralPath (Join-Path $Context.InstallDir 'a.txt.suold') -Encoding Ascii -Value 'old'
    Assert-Throws { Assert-NoSwapLeftovers -Context $Context } '.suold left behind'
    Remove-Item -LiteralPath (Join-Path $Context.InstallDir 'a.txt.suold') -Force
    Set-Content -LiteralPath (Join-Path $Context.InstallDir 'a.txt.sunew') -Encoding Ascii -Value 'new'
    Assert-Throws { Assert-NoSwapLeftovers -Context $Context } '.sunew left behind'
    Remove-Item -LiteralPath (Join-Path $Context.InstallDir 'a.txt.sunew') -Force

    Write-Step 'journal convergence'
    Assert-JournalConverged -Context $Context
    Set-Content -LiteralPath (Join-Path $Context.InstallDir '.smartupdater\journal.json') -Encoding Ascii -Value '{"state":"committing"}'
    Assert-Throws { Assert-JournalConverged -Context $Context } 'journal still present'
    Remove-Item -LiteralPath (Join-Path $Context.InstallDir '.smartupdater\journal.json') -Force

    Write-Step 'state.json assertions use the camelCase field names'
    Set-Content -LiteralPath (Join-Path $Context.InstallDir '.smartupdater\state.json') -Encoding Ascii `
        -Value '{"currentVersion":"1.1.0.0","deviceGuid":"11111111-1111-1111-1111-111111111111","skippedVersions":["1.2.0.0"],"feedETag":null,"lastCheckedAt":null,"lastReportedAt":null}'
    Assert-StateVersion -Context $Context -Version '1.1.0.0'
    Assert-Throws { Assert-StateVersion -Context $Context -Version '1.0.0.0' } 'wrong version accepted'
    # [version] would call '1.1.0' and '1.1.0.0' equal-ish; the string comparison must not.
    Assert-Throws { Assert-StateVersion -Context $Context -Version '1.1.0' } 'a three-part version must not match 1.1.0.0'
    Assert-StateSkipped -Context $Context -Versions @('1.2.0.0')
    Assert-Throws { Assert-StateSkipped -Context $Context -Versions @('9.9.9.9') } 'wrong skip list accepted'
    Assert-Throws { Assert-StateSkipped -Context $Context -Versions @() } 'an empty expectation must not match a non-empty list'

    Write-Step 'download cache'
    $Context.AppId = 'MinimalApp.WinForms-deadbeef'
    Assert-DownloadCacheEmpty -Context $Context
    $updates = Join-Path (Join-Path $Context.LocalAppData $Context.AppId) 'updates'
    New-Item -ItemType Directory -Force -Path $updates | Out-Null
    Assert-DownloadCacheEmpty -Context $Context
    Set-Content -LiteralPath (Join-Path $updates 'pkg.zip') -Encoding Ascii -Value 'x'
    Assert-Throws { Assert-DownloadCacheEmpty -Context $Context } 'cache still holds a package'

    Write-Step 'screenshot validation'
    Add-Type -AssemblyName System.Drawing
    $shots = Join-Path $Context.Root 'shots'; New-Item -ItemType Directory -Force -Path $shots | Out-Null
    # BMP is uncompressed, so a blank one is big enough to pass the size check: only the colour check can reject it.
    $blank = New-Object System.Drawing.Bitmap 400, 300
    $g = [System.Drawing.Graphics]::FromImage($blank); $g.Clear([System.Drawing.Color]::White); $g.Dispose()
    $blankBmp = Join-Path $shots 'blank.bmp'; $blank.Save($blankBmp, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $blankPng = Join-Path $shots 'blank.png'; $blank.Save($blankPng, [System.Drawing.Imaging.ImageFormat]::Png); $blank.Dispose()
    Assert-True ((Get-Item -LiteralPath $blankBmp).Length -gt 5000) 'test image should be large enough to pass the size check'
    Assert-Throws { Assert-Screenshot $blankBmp } 'a single-colour image is not evidence'
    Assert-Throws { Assert-Screenshot $blankPng } 'a tiny single-colour png is not evidence'
    Assert-Throws { Assert-Screenshot (Join-Path $shots 'missing.png') } 'missing screenshot'
    $two = New-Object System.Drawing.Bitmap 400, 300
    $g = [System.Drawing.Graphics]::FromImage($two); $g.Clear([System.Drawing.Color]::White)
    $g.FillRectangle([System.Drawing.Brushes]::Black, 0, 0, 200, 300); $g.Dispose()
    $twoBmp = Join-Path $shots 'two.bmp'; $two.Save($twoBmp, [System.Drawing.Imaging.ImageFormat]::Bmp); $two.Dispose()
    Assert-Throws { Assert-Screenshot $twoBmp } 'two colours still count as blank'
    $real = New-Object System.Drawing.Bitmap 400, 300
    $g = [System.Drawing.Graphics]::FromImage($real); $g.Clear([System.Drawing.Color]::White)
    $g.FillRectangle([System.Drawing.Brushes]::Black, 0, 0, 130, 300)
    $g.FillRectangle([System.Drawing.Brushes]::Red, 130, 0, 130, 300); $g.Dispose()
    $realBmp = Join-Path $shots 'real.bmp'; $real.Save($realBmp, [System.Drawing.Imaging.ImageFormat]::Bmp); $real.Dispose()
    Assert-Screenshot $realBmp

    Write-Step 'the real %LOCALAPPDATA% guard'
    Assert-NoRealLocalAppData -Context $Context
    Assert-Throws { $Context.AppId = ''; Assert-NoRealLocalAppData -Context $Context } 'an empty AppId must fail, not pass silently'
    $Context.AppId = 'MinimalApp.WinForms-deadbeef'
    Assert-Throws { $Context.AppId = ''; Assert-DownloadCacheEmpty -Context $Context } 'an empty AppId must fail the cache check too'
    $Context.AppId = 'MinimalApp.WinForms-deadbeef'
    $realLocal = [Environment]::GetFolderPath('LocalApplicationData')
    $probe = 'SmartUpdaterS0D-' + [guid]::NewGuid().ToString('N')
    New-Item -ItemType Directory -Path (Join-Path $realLocal $probe) | Out-Null
    try {
        $Context.AppId = $probe
        Assert-Throws { Assert-NoRealLocalAppData -Context $Context } 'a directory in the real LocalAppData must be reported'
    }
    finally { Remove-Item -LiteralPath (Join-Path $realLocal $probe) -Force -Recurse -ErrorAction SilentlyContinue }
    $Context.AppId = 'MinimalApp.WinForms-deadbeef'

    Write-Step 'local log coverage'
    Assert-Throws { Assert-LogCovers -Context $Context -Patterns @('x') } 'no log files at all'
    $logs = Join-Path $Context.InstallDir '.smartupdater\logs'; New-Item -ItemType Directory -Force -Path $logs | Out-Null
    Set-Content -LiteralPath (Join-Path $logs 'updater-20260919.log') -Encoding UTF8 -Value @('10:00:00 [Information] check finished', '10:00:05 [Information] update applied 1\.1\.0\.0')
    Assert-LogCovers -Context $Context -Patterns @('check finished', 'update applied')
    Assert-Throws { Assert-LogCovers -Context $Context -Patterns @('check finished', 'rollback done') } 'one pattern is missing'

    Write-Step 'report matching (against a stand-in for the MockServer helper - no server is started here)'
    # Defined UNCONDITIONALLY, and inside Invoke-Scenario so that it shadows the real helper from lib\server.ps1
    # for the rest of this function only. (A definition guarded by Get-Command would never fire once
    # lib\server.ps1 is loaded: the real helper would run with $Server = $null
    # and Invoke-Control would reject the mandatory -Server parameter.) This self-check tests Assert-ReportSeen's
    # filtering, not the HTTP call, so it always uses its own fixed list of reports.
    function Get-MockServerReports($Server) {
        return @(
            [pscustomobject]@{ eventType = 'Heartbeat'; fromVersion = $null; toVersion = $null; stage = $null; isSuccess = $true },
            [pscustomobject]@{ eventType = 'Updated'; fromVersion = '1.0.0.0'; toVersion = '1.1.0.0'; stage = 'Finalize'; isSuccess = $true },
            [pscustomobject]@{ eventType = 'Failed'; fromVersion = '1.1.0.0'; toVersion = '1.2.0.0'; stage = 'Apply'; isSuccess = $false })
    }
    Assert-ReportSeen $null -EventType 'Updated'
    Assert-ReportSeen $null -EventType 'Updated' -From '1.0.0.0' -To '1.1.0.0' -Stage 'Finalize' -IsSuccess $true
    Assert-ReportSeen $null -EventType 'Failed' -IsSuccess $false
    Assert-Throws { Assert-ReportSeen $null -EventType 'Updated' -To '1.2.0.0' } 'wrong toVersion'
    Assert-Throws { Assert-ReportSeen $null -EventType 'Updated' -IsSuccess $false } 'wrong isSuccess'
    Assert-Throws { Assert-ReportSeen $null -EventType 'Rollback' } 'no such event'

    Add-Evidence $Context 'S0D: every assertion fires in both directions'
}
