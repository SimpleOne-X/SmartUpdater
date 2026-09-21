# S0B - build/pack self-check. Publishes two versions, packs them, and asserts the shape of the
# artefacts. It does NOT start the sample or a server, so it stays fast and has no UI dependency.

$ScenarioId = 'S0B'
$ScenarioName = 'publish and pack self-check'

function Invoke-Scenario([hashtable]$Context) {
    Write-Step 'publish 1.0.0'
    $v1 = New-SampleRelease -Context $Context -Version '1.0.0'
    Assert-FileExists (Join-Path $v1 'MinimalApp.WinForms.exe')
    Assert-FileExists (Join-Path $v1 'MinimalApp.WinForms.dll')
    Assert-FileExists (Join-Path $v1 'AntdUI.dll')
    Assert-FileExists (Join-Path $v1 'SimpleOneX.SmartUpdater.dll')
    Assert-FileExists (Join-Path $v1 'appsettings.json')
    Assert-FileExists (Join-Path $v1 'THIRD-PARTY-NOTICES.txt')
    # GenerateDocumentationFile=false : no sample .xml must reach the package.
    Assert-NoFile (Join-Path $v1 'MinimalApp.WinForms.xml')

    Write-Step 'publish 1.1.0 with one file changed, one added, one removed'
    $v2 = New-SampleRelease -Context $Context -Version '1.1.0' -Mutate {
        param($dir)
        Set-Content -LiteralPath (Join-Path $dir 'appsettings.json') -Encoding Ascii -Value '{ "userNote": "shipped by 1.1.0" }'
        Set-Content -LiteralPath (Join-Path $dir 'release-notes.txt') -Encoding Ascii -Value 'added in 1.1.0'
        Remove-Item -LiteralPath (Join-Path $dir 'THIRD-PARTY-NOTICES.txt') -Force
    }

    Write-Step 'AntdUI.dll is byte-identical across versions (the skip-if-same-hash material)'
    $h1 = Get-DirectoryHashes $v1
    $h2 = Get-DirectoryHashes $v2
    # A wrong key would give $null on both sides and Assert-Equal would pass: check presence first.
    Assert-True ($null -ne $h1['antdui.dll']) 'antdui.dll missing from the 1.0.0 hash table (key casing?)'
    Assert-True ($null -ne $h2['antdui.dll']) 'antdui.dll missing from the 1.1.0 hash table (key casing?)'
    Assert-Equal $h1['antdui.dll'] $h2['antdui.dll'] 'AntdUI.dll changed between versions'
    Assert-True ($null -ne $h1['minimalapp.winforms.dll']) 'minimalapp.winforms.dll missing from the 1.0.0 hash table'
    Assert-True ($null -ne $h2['minimalapp.winforms.dll']) 'minimalapp.winforms.dll missing from the 1.1.0 hash table'
    Assert-True ($h1['minimalapp.winforms.dll'] -ne $h2['minimalapp.winforms.dll']) 'the app dll must differ (version is baked in)'

    Write-Step 'Get-DirectoryHashes: keys are lower case with forward slashes; -Exclude takes prefixes and exact names'
    # PowerShell hashtables are case-insensitive, so the key casing must be checked explicitly (-cne).
    Assert-Equal 0 @($h1.Keys | Where-Object { $_ -cne $_.ToLowerInvariant() }).Count 'every hash key must be lower case'
    $scratch = Join-Path $Context.Root 'hashprobe'
    New-Item -ItemType Directory -Force -Path (Join-Path $scratch 'Sub\Deep') | Out-Null
    Set-Content -LiteralPath (Join-Path $scratch 'Top.TXT') -Encoding Ascii -Value 'a'
    Set-Content -LiteralPath (Join-Path $scratch 'Sub\Deep\Inner.txt') -Encoding Ascii -Value 'b'
    Set-Content -LiteralPath (Join-Path $scratch 'Sub\Other.txt') -Encoding Ascii -Value 'c'
    $all = Get-DirectoryHashes $scratch
    Assert-Equal 3 $all.Count 'probe directory has three files'
    Assert-True ($all.ContainsKey('sub/deep/inner.txt')) 'nested key must use forward slashes'
    Assert-True ($all.ContainsKey('top.txt')) 'top-level key present'
    Assert-Equal 64 $all['top.txt'].Length 'SHA-256 hex length'
    Assert-Equal $all['top.txt'].ToUpperInvariant() $all['top.txt'] 'hash must be upper-case hex'
    $noSub = Get-DirectoryHashes $scratch -Exclude @('sub/')
    Assert-Equal 1 $noSub.Count 'prefix exclude must drop everything under sub/'
    $noTop = Get-DirectoryHashes $scratch -Exclude @('TOP.txt')
    Assert-Equal 2 $noTop.Count 'exact-name exclude must drop only that file'
    Assert-True (-not $noTop.ContainsKey('top.txt')) 'excluded file must be gone'

    Write-Step 'pack both versions into one feed'
    $feed = Invoke-Pack -Context $Context -PublishDir $v1 -Version '1.0.0' -Preserve @('appsettings.json')
    $feed = Invoke-Pack -Context $Context -PublishDir $v2 -Version '1.1.0' -Preserve @('appsettings.json') -Notes 'e2e self check'
    Assert-FileExists $feed

    Write-Step 'a failing Packer run must throw with its exit code (not be swallowed)'
    $threw = $null
    try { Invoke-Pack -Context $Context -PublishDir (Join-Path $Context.Root 'no-such-dir') -Version '9.9.9' | Out-Null }
    catch { $threw = $_.Exception.Message }
    Assert-True ($null -ne $threw) 'Invoke-Pack must throw when the Packer exits non-zero'
    Assert-True ($threw -match 'failed with exit code [1-9]') 'the thrown message must carry the Packer exit code'

    $doc = Get-Content -LiteralPath $feed -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Equal 1 $doc.schemaVersion 'schemaVersion'
    Assert-Equal 2 @($doc.releases).Count 'two releases expected'
    # FeedWriter sorts descending.
    Assert-Equal '1.1.0' $doc.releases[0].version 'feed must be sorted descending'
    Assert-Equal '1.0.0' $doc.releases[1].version 'feed must be sorted descending'
    Assert-True ($doc.releases[0].package.sha256.Length -eq 64) 'sha256 must be present and 64 hex chars'
    Assert-Equal 'optional' $doc.releases[0].mode 'default mode'
    Assert-Equal 100 $doc.releases[0].rolloutPercent 'default rolloutPercent'

    Write-Step 'the package really contains the manifest and marks appsettings.json as preserve'
    $zip = Join-Path $Context.Root 'releases\packages\MinimalApp.WinForms-1.1.0.zip'
    Assert-FileExists $zip
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entry = @($archive.Entries | Where-Object { $_.FullName -eq '.smartupdater/manifest.json' })
        Assert-Equal 1 $entry.Count 'zip must contain .smartupdater/manifest.json'
        $reader = New-Object System.IO.StreamReader($entry[0].Open(), (New-Object System.Text.UTF8Encoding $false))
        $manifest = $reader.ReadToEnd() | ConvertFrom-Json
        $reader.Dispose()
        Assert-Equal '1.1.0' $manifest.version 'manifest version'
        $app = @($manifest.files | Where-Object { $_.path -eq 'appsettings.json' })
        Assert-Equal 1 $app.Count 'appsettings.json must be listed in the manifest'
        Assert-Equal 'preserve' $app[0].policy 'appsettings.json must be preserve'
        # The manifest never lists itself.
        Assert-Equal 0 @($manifest.files | Where-Object { $_.path -like '.smartupdater/*' }).Count 'manifest must not list itself'
        Assert-Equal 1 @($manifest.files | Where-Object { $_.path -eq 'release-notes.txt' }).Count 'added file missing from manifest'
    }
    finally { $archive.Dispose() }

    Write-Step 'Install-Release leaves no local manifest; Set-SeedManifest puts the packages own one there'
    Install-Release -Context $Context -PublishDir $v1
    $localManifest = Join-Path $Context.InstallDir '.smartupdater\manifest.json'
    # Pins the premise of Set-SeedManifest: a plain copy install has no manifest, so the
    # next update would delete nothing. If Install-Release ever starts writing one, this line fails and
    # whoever changed it has to re-check every scenario that expects files to be removed.
    Assert-NoFile $localManifest
    Set-SeedManifest -Context $Context -Version '1.0.0' | Out-Null
    Assert-FileExists $localManifest
    $seeded = Get-Content -LiteralPath $localManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Equal '1.0.0' $seeded.version 'seeded manifest must be the 1.0.0 one'
    Assert-Equal 1 @($seeded.files | Where-Object { $_.path -eq 'THIRD-PARTY-NOTICES.txt' }).Count `
                 'the seeded manifest must own the file that 1.1.0 removes'
    Assert-Equal 0 @($seeded.files | Where-Object { $_.path -eq 'release-notes.txt' }).Count `
                 'the seeded manifest must not know about a file that only 1.1.0 ships'
    $missing = $null
    try { Set-SeedManifest -Context $Context -Version '9.9.9' | Out-Null } catch { $missing = $_.Exception.Message }
    Assert-True ($null -ne $missing) 'Set-SeedManifest must throw when the package does not exist'

    Add-Evidence $Context ('S0B: published 1.0.0 and 1.1.0, AntdUI.dll sha256=' + $h1['antdui.dll'])
}
