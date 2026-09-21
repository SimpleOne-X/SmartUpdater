<#
    SmartUpdater end-to-end runner.
    Usage: powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\e2e\run-e2e.ps1 [-Scenario S1,S2] [-KeepArtifacts]
    ASCII only. LF line endings. Exit code 0 = every selected scenario passed, 1 = any failure or bad -Scenario id.

    A scenario is a file tools\e2e\scenarios\s*.ps1 that defines $ScenarioId, $ScenarioName (English:
    it goes into the ASCII report) and function Invoke-Scenario([hashtable]$Context). Scenarios are found by
    scanning the directory, in ordinal file-name order; there is no hard-coded list.

    Cleanup after every scenario: first kill every process under the scenario root, then delete the
    root (unless -KeepArtifacts).
      - A process that could not be killed, or that is still running under the root afterwards, is a leak: the
        scenario is reported as FAIL (even if its own steps passed) and the run exits 1.
      - A root that could not be deleted (antivirus, a lingering handle) is only a WARN line in the summary and
        in e2e-report.txt; the verdict and the exit code do not change.
    Both are recorded on the scenario context (CleanupFailures / CleanupWarnings, lists of strings).
#>
[CmdletBinding()]
param(
    [string[]]$Scenario = @(),
    [switch]$KeepArtifacts,
    # How long the cleanup after each scenario keeps trying to kill what the scenario left running.
    [ValidateRange(1, 600)][int]$CleanupTimeoutSec = 15
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'lib\common.ps1')
. (Join-Path $here 'lib\process.ps1')
. (Join-Path $here 'lib\build.ps1')
. (Join-Path $here 'lib\install.ps1')
. (Join-Path $here 'lib\server.ps1')
. (Join-Path $here 'lib\assert.ps1')

$repoRoot = (Resolve-Path (Join-Path $here '..\..')).Path
$reportPath = Join-Path $here 'e2e-report.txt'

# 'powershell.exe -File x.ps1 -Scenario S1,S2' hands the value over as ONE string 'S1,S2'
# (only a call from inside PowerShell binds a real array), so split on commas ourselves.
$Scenario = @($Scenario | ForEach-Object { $_ -split '[,;]' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

function Format-Seconds([double]$Seconds) {
    return $Seconds.ToString('0.0', [System.Globalization.CultureInfo]::InvariantCulture) + 's'
}

function ConvertTo-OneLine([string]$Text) {
    return (($Text -split '\r?\n' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) -join ' | ')
}

# Reads every scenario file once, in its own scope, and returns @{ File; Id; Name } for each. Reading all of
# them (not only the selected ones) makes a broken scenario file fail loudly instead of being skipped, and
# gives the complete id list to validate -Scenario against before anything is run.
function Get-ScenarioCatalog([string]$Directory) {
    $names = [string[]]@(Get-ChildItem -LiteralPath $Directory -Filter 's*.ps1' -File | ForEach-Object { $_.Name })
    [System.Array]::Sort($names, [System.StringComparer]::Ordinal)
    $catalog = New-Object System.Collections.Generic.List[object]
    foreach ($name in $names) {
        $file = Join-Path $Directory $name
        # Forget the previous file's function, or a file that lacks its own would silently inherit it.
        Remove-Item -LiteralPath 'Function:\Invoke-Scenario' -ErrorAction SilentlyContinue
        $ScenarioId = $null; $ScenarioName = $null
        . $file
        if (-not $ScenarioId) { throw "scenario file has no `$ScenarioId: $name" }
        if (-not $ScenarioName) { throw "scenario file has no `$ScenarioName: $name" }
        if (-not (Test-Path -LiteralPath 'Function:\Invoke-Scenario')) { throw "scenario file has no function Invoke-Scenario: $name" }
        foreach ($entry in $catalog) {
            if ($entry.Id -eq $ScenarioId) { throw "duplicate scenario id $ScenarioId in $($entry.Name) and $name" }
        }
        $catalog.Add([pscustomobject]@{ File = $file; Name = $name; Id = $ScenarioId; Title = $ScenarioName })
    }
    return $catalog.ToArray()       # unrolled, like every helper: the caller wraps the call in @(...)
}

# Kills what the scenario left running, then deletes its root. Never throws: every problem is recorded on the
# context instead. A leaked process goes to CleanupFailures (it fails the run), a root that will not go away
# goes to CleanupWarnings (it does not).
function Invoke-ScenarioCleanup([hashtable]$Context, [bool]$Keep, [int]$TimeoutSec) {
    $root = $Context.Root
    # Always kill first: a running process keeps its files locked and would block the delete.
    $stopFailed = $false
    try { Stop-ProcessesUnder $root -TimeoutSec $TimeoutSec }
    catch { $stopFailed = $true; $Context.CleanupFailures.Add((ConvertTo-OneLine $_.Exception.Message)) }
    if (-not $stopFailed) {
        # A second, independent look. Stop-ProcessesUnder judges by its own snapshots; a process that
        # appeared after the last one, or a Stop-ProcessesUnder that is wrong, must not slip through.
        try {
            $left = @(Get-ProcessesUnder $root -IncludeDescendants)
            if ($left.Count -gt 0) {
                $each = @($left | ForEach-Object { '{0} ({1})' -f $_.ProcessId, $_.Name })
                $Context.CleanupFailures.Add(('{0} process(es) still running under {1} after cleanup: {2}' -f $left.Count, $root, ($each -join '; ')))
            }
        }
        catch { $Context.CleanupFailures.Add(('could not verify that no process is left under the scenario root: ' + (ConvertTo-OneLine $_.Exception.Message))) }
    }
    if ($Keep) { Write-Host ('     artifacts kept at ' + $root); return }
    $why = ''
    try { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Stop }
    catch { $why = ' (' + (ConvertTo-OneLine $_.Exception.Message) + ')' }
    if (Test-Path -LiteralPath $root) { $Context.CleanupWarnings.Add('scenario root could not be removed: ' + $root + $why) }
}

function Write-E2eReport([string]$Path, [object[]]$Results) {
    $out = New-Object System.Collections.Generic.List[string]
    $out.Add('SmartUpdater end-to-end report ' + (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss'))
    $out.Add('')
    foreach ($r in $Results) { foreach ($l in $r.Lines) { $out.Add($l) } }      # verdict line, then its WARN lines
    $failed = @($Results | Where-Object { $_.Status -eq 'FAIL' }).Count
    $warnings = 0
    foreach ($r in $Results) { $warnings += $r.Warnings }
    $out.Add('')
    $total = 'TOTAL ' + @($Results).Count + ' scenario(s), ' + (@($Results).Count - $failed) + ' passed, ' + $failed + ' failed'
    if ($warnings -gt 0) { $total += ', ' + $warnings + ' warning(s)' }
    $out.Add($total)
    foreach ($r in $Results) {
        if ($r.Context.ContainsKey('Evidence') -and $r.Context.Evidence.Count -gt 0) {
            $out.Add('')
            $out.Add('== ' + $r.Id + ' ' + $r.Title + ' ==')
            foreach ($e in $r.Context.Evidence) { $out.Add($e) }
        }
    }
    # ASCII on purpose (same rule as the scripts); anything else becomes '?'. WriteAllText with the ASCII
    # encoding writes no BOM.
    $text = (($out -join "`n") + "`n") -replace '[^\x00-\x7F]', '?'
    [System.IO.File]::WriteAllText($Path, $text, [System.Text.Encoding]::ASCII)
}

$catalog = @(Get-ScenarioCatalog (Join-Path $here 'scenarios'))
if ($catalog.Count -eq 0) {
    Write-Host 'no scenario files found in tools\e2e\scenarios'
    exit 1
}
$known = @($catalog | ForEach-Object { $_.Id })
if ($Scenario.Count -gt 0) {
    # A typo must fail, otherwise 'selected nothing' would look like 'everything passed'.
    $missing = @($Scenario | Where-Object { $known -notcontains $_ })
    if ($missing.Count -gt 0) {
        Write-Host ('unknown scenario id(s): ' + ($missing -join ', ') + '; known: ' + ($known -join ', '))
        exit 1
    }
}
$selected = @($catalog | Where-Object { ($Scenario.Count -eq 0) -or ($Scenario -contains $_.Id) })

$results = New-Object System.Collections.Generic.List[object]

foreach ($entry in $selected) {
    Remove-Item -LiteralPath 'Function:\Invoke-Scenario' -ErrorAction SilentlyContinue
    $ScenarioId = $null; $ScenarioName = $null
    . $entry.File                                           # defines Invoke-Scenario in THIS scope

    $root = New-ScenarioRoot
    $context = @{
        Root = $root; RepoRoot = $repoRoot; Id = $ScenarioId
        CleanupFailures = New-Object 'System.Collections.Generic.List[string]'
        CleanupWarnings = New-Object 'System.Collections.Generic.List[string]'
    }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $status = 'PASS'; $reason = ''
    Write-Host ('RUN  ' + $ScenarioId + ' ' + $ScenarioName)
    try { Invoke-Scenario $context }
    catch {
        $status = 'FAIL'
        $reason = $_.Exception.Message
        Write-Diag $reason
        try { Write-ScenarioDiagnostics $context } catch { Write-Diag ('diagnostics failed: ' + $_.Exception.Message) }
    }
    finally {
        $sw.Stop()
        # Cleanup never replaces the scenario's own verdict by throwing; what it finds is on the context.
        try { Invoke-ScenarioCleanup $context ([bool]$KeepArtifacts) $CleanupTimeoutSec }
        catch { $context.CleanupFailures.Add('cleanup crashed: ' + (ConvertTo-OneLine $_.Exception.Message)) }
    }

    # The verdict is final only now: a leaked process fails a scenario whose own steps passed.
    if ($context.CleanupFailures.Count -gt 0) {
        $cleanup = 'cleanup failed: ' + ($context.CleanupFailures.ToArray() -join '; ')
        if ($status -eq 'FAIL') { $reason = (ConvertTo-OneLine $reason) + ' | ' + $cleanup } else { $reason = $cleanup }
        $status = 'FAIL'
    }
    $line = $status + ' ' + $ScenarioId + ' ' + $ScenarioName + ' (' + (Format-Seconds $sw.Elapsed.TotalSeconds) + ')'
    if ($status -eq 'FAIL') { $line += ': ' + (ConvertTo-OneLine $reason) }
    $lines = New-Object 'System.Collections.Generic.List[string]'
    $lines.Add($line)
    foreach ($w in $context.CleanupWarnings) { $lines.Add('WARN ' + $ScenarioId + ' ' + $ScenarioName + ': ' + $w) }
    $results.Add([pscustomobject]@{
        Id = $ScenarioId; Title = $ScenarioName; Status = $status
        Lines = $lines.ToArray(); Warnings = $context.CleanupWarnings.Count; Context = $context
    })
    foreach ($l in $lines) { Write-Host $l }
}

$failedCount = @($results | Where-Object { $_.Status -eq 'FAIL' }).Count
Write-Host ''
Write-Host '---- summary ----'
foreach ($r in $results) { foreach ($l in $r.Lines) { Write-Host $l } }
# ToArray(): on Windows PowerShell 5.1 under StrictMode, @($genericList).Count throws "Argument types do not match".
Write-E2eReport $reportPath $results.ToArray()
Write-Host ('report written to ' + $reportPath)
if ($failedCount -gt 0) { exit 1 }
exit 0
