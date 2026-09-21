# S0 - framework self-check. Does not build, publish or start any server.
# It exists so that a broken helper library fails fast, before any real scenario runs.
# ASCII only, LF line endings.

$ScenarioId = 'S0'
$ScenarioName = 'framework self-check'

function Invoke-Scenario([hashtable]$Context) {
    $root = $Context.Root

    Write-Step 'scenario root is created and writable'
    Assert-True (Test-Path -LiteralPath $root) "scenario root does not exist: $root"
    Set-Content -LiteralPath (Join-Path $root 'probe.txt') -Value 'ok' -Encoding Ascii
    Assert-FileExists (Join-Path $root 'probe.txt')
    Assert-NoFile (Join-Path $root 'absent.txt')

    Write-Step 'Wait-Until returns as soon as the condition holds'
    $script:hits = 0
    Wait-Until -Condition { $script:hits++; $script:hits -ge 3 } -TimeoutSec 10 -Description 'counter reaches 3'
    Assert-Equal 3 $script:hits 'Wait-Until polled the wrong number of times'
    # Order inside the loop is: check the condition, check the deadline, and only then sleep. With a 5 s poll
    # interval, a condition that already holds must return at once instead of sleeping first.
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    Wait-Until -Condition { $true } -TimeoutSec 20 -PollMs 5000 -Description 'condition that already holds'
    Assert-True ($clock.Elapsed.TotalSeconds -lt 3) 'Wait-Until slept before checking a condition that already holds'

    Write-Step 'Wait-Until really times out and keeps the description'
    $timedOut = $false
    try { Wait-Until -Condition { $false } -TimeoutSec 1 -Description 'never true' }
    catch { $timedOut = $true; Assert-True ($_.Exception.Message -like '*never true*') 'timeout message lost the description' }
    Assert-True $timedOut 'Wait-Until did not time out'

    Write-Step 'event log parsing'
    $log = Join-Path $root 'events.log'
    Set-Content -LiteralPath $log -Encoding Ascii -Value @(
        'ts=2026-09-19T10:00:00.000Z event=started version=1.0.0 pid=1234',
        'ts=2026-09-19T10:00:05.000Z event=started version=1.1.0 pid=5678 updated-from=1.0.0',
        'this line is garbage and must be dropped'
    )
    $lines = @(Read-EventLog $log)
    Assert-Equal 2 $lines.Count 'incomplete lines must be dropped'
    Assert-Equal '1.1.0' (Get-EventValue $lines[1] 'version') 'wrong version'
    Assert-Equal '1.0.0' (Get-EventValue $lines[1] 'updated-from') 'wrong updated-from'
    # A missing key must be $null, not a substring match on another key.
    Assert-Equal $null (Get-EventValue $lines[0] 'updated-from') 'missing key must yield null'
    # ... and a key must not be found inside a longer key that merely ends the same way.
    Assert-Equal '1.0' (Get-EventValue 'ts=x event=started updated-from-version=0.9 version=1.0' 'version') 'key matched inside a longer key'
    Assert-Equal 2 @(Get-EventLines $lines 'started').Count 'Get-EventLines filtered wrongly'
    Assert-Equal 0 @(Get-EventLines $lines 'start').Count 'Get-EventLines must match the whole event name'

    Write-Step 'array-returning helpers report 0, 1 and 2 results correctly (convention: lib\common.ps1)'
    # The calling convention is documented at the top of lib\common.ps1: these helpers return their items
    # UNROLLED and callers wrap the call in @(...). A helper that returned 'return , $array' would make
    # @(f).Count be 1 for no hit and a pipeline see one element - a silent false green. Each helper is
    # therefore pinned for 0, 1 and 2 results, both through @(...) and through the pipeline.
    function Assert-ResultCount([string]$What, [int]$Expected, [scriptblock]$Call) {
        Assert-Equal $Expected (@(& $Call).Count) "${What}: @(call).Count"
        Assert-Equal $Expected ((& $Call | Measure-Object).Count) "${What}: call | Measure-Object"
    }
    $exitLine = 'ts=2026-09-19T10:00:09.000Z event=exiting pid=5678'
    $mixed = @($lines[0], $lines[1], $exitLine)
    Assert-ResultCount 'Get-EventLines, no hit' 0 { Get-EventLines $mixed 'zzz' }
    Assert-ResultCount 'Get-EventLines, one hit' 1 { Get-EventLines $mixed 'exiting' }
    Assert-ResultCount 'Get-EventLines, two hits' 2 { Get-EventLines $mixed 'started' }
    Assert-Equal $exitLine (@(Get-EventLines $mixed 'exiting')[0]) 'a single hit must be the whole line, not its first character'
    Assert-Equal $lines[1] (@(Get-EventLines $mixed 'started')[1]) 'two hits must be indexable'
    $log0 = Join-Path $root 'events-none.log'
    Set-Content -LiteralPath $log0 -Encoding Ascii -Value @('garbage only', 'ts=  event=')
    $log1 = Join-Path $root 'events-one.log'
    Set-Content -LiteralPath $log1 -Encoding Ascii -Value @($exitLine)
    Assert-ResultCount 'Read-EventLog, missing log' 0 { Read-EventLog (Join-Path $root 'no-such.log') }
    Assert-ResultCount 'Read-EventLog, no valid line' 0 { Read-EventLog $log0 }
    Assert-ResultCount 'Read-EventLog, one line' 1 { Read-EventLog $log1 }
    Assert-ResultCount 'Read-EventLog, two lines' 2 { Read-EventLog $log }
    Assert-Equal $exitLine (@(Read-EventLog $log1)[0]) 'a single line must be the whole line, not its first character'
    # A caller that forgot the @(...) around a zero-line read hands $null on; the filter must not choke on it.
    $forgotten = Read-EventLog $log0
    Assert-ResultCount 'Get-EventLines, unwrapped empty read' 0 { Get-EventLines $forgotten 'started' }

    Write-Step 'native command helper keeps stderr and exit code under ErrorActionPreference=Stop'
    Assert-Equal 'Stop' $ErrorActionPreference 'the runner is expected to run with ErrorActionPreference=Stop'
    # cmd.exe writes to stdout and to stderr, then exits 3. A bare '& ... 2>&1' would die with
    # NativeCommandError under Stop on Windows PowerShell 5.1.
    $native = Invoke-Native { cmd.exe /c 'echo to-stdout& echo to-stderr 1>&2& exit 3' }
    Assert-Equal 3 $native.ExitCode 'native exit code was lost'
    Assert-True (@($native.Output | Where-Object { $_ -like '*to-stdout*' }).Count -eq 1) 'native stdout was lost'
    Assert-True (@($native.Output | Where-Object { $_ -like '*to-stderr*' }).Count -eq 1) 'native stderr was lost'
    Assert-Equal 'Stop' $ErrorActionPreference 'Invoke-Native did not restore ErrorActionPreference'

    Write-Step 'process helpers find and kill a process by its location'
    $bat = Join-Path $root 'sleeper.cmd'
    Set-Content -LiteralPath $bat -Encoding Ascii -Value '@ping -n 60 127.0.0.1 > nul'
    $bat2 = Join-Path $root 'sleeper2.cmd'
    Set-Content -LiteralPath $bat2 -Encoding Ascii -Value '@ping -n 60 127.0.0.1 > nul'
    Assert-ResultCount 'Get-ProcessesUnder, no process' 0 { Get-ProcessesUnder $root }
    $p = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $bat -PassThru -WindowStyle Hidden
    $p2 = $null
    try {
        # cmd.exe itself lives in System32, so this child is found by its COMMAND LINE, not its
        # ExecutablePath - it asserts the fallback branch of Get-ProcessesUnder.
        Wait-Until -Condition { @(Get-ProcessesUnder $root).Count -ge 1 } -TimeoutSec 30 -Description 'child process visible'
        # Without -IncludeDescendants only the cmd.exe matches (its ping child mentions no path under $root),
        # so exactly one process is found here, and exactly two once a second sleeper runs.
        Assert-ResultCount 'Get-ProcessesUnder, one process' 1 { Get-ProcessesUnder $root }
        Assert-Equal $p.Id (@(Get-ProcessesUnder $root)[0].ProcessId) 'a single process must be the object itself, not a wrapper'
        $p2 = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $bat2 -PassThru -WindowStyle Hidden
        Wait-Until -Condition { @(Get-ProcessesUnder $root).Count -ge 2 } -TimeoutSec 30 -Description 'second child process visible'
        Assert-ResultCount 'Get-ProcessesUnder, two processes' 2 { Get-ProcessesUnder $root }
        # The ping that cmd.exe forks has no path under $root at all: only the parent chain reaches it.
        Wait-Until -Condition { @(Get-ProcessesUnder $root -IncludeDescendants).Count -ge 4 } -TimeoutSec 30 -Description 'grandchild pings visible'
        $tree = @(Get-ProcessesUnder $root -IncludeDescendants)
        $treePids = @($tree | ForEach-Object { $_.ProcessId })
        Stop-ProcessesUnder $root
        Wait-Until -Condition { @(Get-ProcessesUnder $root).Count -eq 0 } -TimeoutSec 30 -Description 'all processes gone'
        # Once the parent is dead a surviving ping no longer hangs off any process under $root, so it can only
        # be checked by the pids recorded above. Without the recursive kill it would outlive the scenario by
        # the rest of its 60 seconds.
        Wait-Until -Condition { @(Get-Process -Id $treePids -ErrorAction SilentlyContinue).Count -eq 0 } -TimeoutSec 30 -Description 'no orphaned descendants'
    }
    finally {
        foreach ($started in @($p, $p2)) {
            if ($started -and -not $started.HasExited) { Stop-Process -Id $started.Id -Force -ErrorAction SilentlyContinue }
        }
    }
    # A drive root must never be accepted as a "location": that would match every process on the box.
    $refused = $false
    try { Get-ProcessesUnder ([System.IO.Path]::GetPathRoot($root)) | Out-Null }
    catch { $refused = $true }
    Assert-True $refused 'a drive root must be refused as a process location'

    Write-Step 'a process that survives every kill makes Stop-ProcessesUnder throw'
    # A process nobody may kill (access denied, protected) cannot be created without side effects, so it is
    # simulated: inside the & { } block below, Stop-Process is shadowed by a function that does nothing, which
    # is what an access-denied kill looks like to the caller. The process itself is a real hidden cmd.exe.
    $stuckBat = Join-Path $root 'stuck.cmd'
    Set-Content -LiteralPath $stuckBat -Encoding Ascii -Value '@ping -n 60 127.0.0.1 > nul'
    $stuck = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $stuckBat -PassThru -WindowStyle Hidden
    try {
        Wait-Until -Condition { @(Get-ProcessesUnder $root).Count -ge 1 } -TimeoutSec 30 -Description 'stuck child process visible'
        $failure = & {
            function Stop-Process { [CmdletBinding()] param($Id, [switch]$Force) }
            try { Stop-ProcessesUnder $root -TimeoutSec 1; return $null } catch { return $_.Exception.Message }
        }
        Assert-True ($null -ne $failure) 'Stop-ProcessesUnder must throw when a process survives every kill'
        Assert-True ("$failure" -like ('*' + $stuck.Id + '*')) ('the failure must name the surviving pid ' + $stuck.Id + ': ' + $failure)
        Assert-True ([bool](Get-Process -Id $stuck.Id -ErrorAction SilentlyContinue)) 'the simulated kill must really have left the process running'
        # Same call with the real Stop-Process: must succeed silently and leave nothing behind.
        Stop-ProcessesUnder $root
        Wait-Until -Condition { @(Get-ProcessesUnder $root -IncludeDescendants).Count -eq 0 } -TimeoutSec 30 -Description 'stuck process really gone'
    }
    finally {
        if (Get-Process -Id $stuck.Id -ErrorAction SilentlyContinue) { Stop-Process -Id $stuck.Id -Force -ErrorAction SilentlyContinue }
    }

    Write-Step 'runner: a process that survives cleanup fails the run, a root that cannot be deleted only warns'
    # The real runner is run as a child three times, from a copy of the scripts with throw-away scenarios
    # (fixtures at the end of this file). Its scenario roots go to a private TEMP inside this scenario's root, so
    # that whatever a fixture leaves behind is still under $root and cleaned up by the outer runner as well.
    $e2eDir = Join-Path $Context.RepoRoot 'tools\e2e'
    $copy = Join-Path $root 'runner-copy'
    New-Item -ItemType Directory -Force -Path (Join-Path $copy 'scenarios') | Out-Null
    Copy-Item -LiteralPath (Join-Path $e2eDir 'lib') -Destination $copy -Recurse
    Copy-Item -LiteralPath (Join-Path $e2eDir 'run-e2e.ps1') -Destination $copy
    Set-Content -LiteralPath (Join-Path $copy 'scenarios\sx1-leak.ps1') -Encoding Ascii -Value $script:S0FixtureLeak
    Set-Content -LiteralPath (Join-Path $copy 'scenarios\sx2-locked.ps1') -Encoding Ascii -Value $script:S0FixtureLocked
    Set-Content -LiteralPath (Join-Path $copy 'scenarios\sx3-blind.ps1') -Encoding Ascii -Value $script:S0FixtureBlind
    Set-Content -LiteralPath (Join-Path $copy 'scenarios\sx4-killable.ps1') -Encoding Ascii -Value $script:S0FixtureKillable
    function Invoke-NestedRunner([string]$FixtureId, [string]$PrivateTemp) {
        New-Item -ItemType Directory -Force -Path $PrivateTemp | Out-Null
        $realTemp = $env:TEMP
        $env:TEMP = $PrivateTemp
        try {
            return Invoke-Native { powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $copy 'run-e2e.ps1') -Scenario $FixtureId -CleanupTimeoutSec 2 }
        }
        finally { $env:TEMP = $realTemp }
    }
    # Lines of the summary block only (after the marker), so a live progress line cannot stand in for it.
    function Get-SummaryLines($Run) {
        $all = @($Run.Output)
        $at = [System.Array]::IndexOf($all, '---- summary ----')
        Assert-True ($at -ge 0) ('no summary block in the runner output: ' + ($all -join ' | '))
        if ($at + 1 -lt $all.Count) { $all[($at + 1)..($all.Count - 1)] }
    }

    # A leaked process must fail the run whatever the reason the runner could not get rid of it: X1 makes the kill
    # itself fail (Stop-ProcessesUnder throws), X3 makes the kill step blind (only the runner's own second look
    # can notice). $Says is the wording expected in the FAIL line.
    function Assert-LeakFailsTheRun([string]$FixtureId, [string]$Says) {
        $nestedTemp = Join-Path $root ('nested-temp-' + $FixtureId)
        $run = Invoke-NestedRunner $FixtureId $nestedTemp
        try {
            $said = ($run.Output -join ' | ')
            Assert-True (@(Get-ProcessesUnder $nestedTemp).Count -ge 1) ("$FixtureId must really have left a process behind; runner said: $said")
            Assert-Equal 1 $run.ExitCode ("$FixtureId - a leaked process must make the run exit 1; runner said: $said")
            $summary = @(Get-SummaryLines $run)
            $failLines = @($summary | Where-Object { $_ -like "FAIL $FixtureId *" })
            Assert-Equal 1 $failLines.Count ("$FixtureId - the summary must carry one FAIL line; runner said: $said")
            Assert-True ($failLines[0] -like $Says) ("$FixtureId - the FAIL line must say what leaked: " + $failLines[0])
            $report = @(Get-Content -LiteralPath (Join-Path $copy 'e2e-report.txt'))
            Assert-Equal 1 @($report | Where-Object { $_ -like "FAIL $FixtureId *" }).Count "$FixtureId - the report must carry a FAIL line"
            Assert-Equal 1 @($report | Where-Object { $_ -eq 'TOTAL 1 scenario(s), 0 passed, 1 failed' }).Count "$FixtureId - the report total must count the leak as a failure"
        }
        finally {
            # Really kill what the fixture leaked: the runner under test was made unable to.
            try { Stop-ProcessesUnder $nestedTemp } catch { Write-Diag ('could not clean up the leak of ' + $FixtureId + ': ' + $_.Exception.Message) }
        }
        Assert-Equal 0 @(Get-ProcessesUnder $nestedTemp -IncludeDescendants).Count "the leak of $FixtureId must be cleaned up before S0 ends"
    }
    Assert-LeakFailsTheRun 'X1' '*cleanup failed*survived the kill*'
    Assert-LeakFailsTheRun 'X3' '*cleanup failed*still running*after cleanup*'

    # The normal case: the scenario leaves a killable process (as the real ones leave the sample running). It runs
    # from an executable inside the root, which locks that file, so the root can only be deleted if the runner kills
    # FIRST. The run must stay green, without a warning, and leave neither the process nor the directory.
    $killTemp = Join-Path $root 'nested-temp-X4'
    $kill = Invoke-NestedRunner 'X4' $killTemp
    try {
        $said = ($kill.Output -join ' | ')
        Assert-Equal 0 $kill.ExitCode ('a killable leftover process is cleaned up, not a failure; runner said: ' + $said)
        $summary = @(Get-SummaryLines $kill)
        Assert-Equal 1 @($summary | Where-Object { $_ -like 'PASS X4 *' }).Count ('X4 must pass; runner said: ' + $said)
        Assert-Equal 0 @($summary | Where-Object { $_ -like 'WARN *' }).Count ('killing first must let the root be deleted without a warning; runner said: ' + $said)
        Assert-Equal 0 @(Get-ChildItem -LiteralPath $killTemp -Directory -Filter 'smartupdater-e2e-*').Count 'the runner must delete the root after killing the process'
        Assert-Equal 0 @(Get-ProcessesUnder $killTemp -IncludeDescendants).Count 'the runner must kill the process the scenario left running'
    }
    finally {
        try { Stop-ProcessesUnder $killTemp } catch { Write-Diag ('could not clean up X4: ' + $_.Exception.Message) }
    }

    $lockedTemp = Join-Path $root 'nested-temp-locked'
    $locked = Invoke-NestedRunner 'X2' $lockedTemp
    $said = ($locked.Output -join ' | ')
    Assert-Equal 0 $locked.ExitCode ('a root that cannot be deleted must only warn, exit code stays 0; runner said: ' + $said)
    Assert-Equal 1 @(Get-ChildItem -LiteralPath $lockedTemp -Directory -Filter 'smartupdater-e2e-*').Count 'the fixture root must really have been left behind'
    $summary = @(Get-SummaryLines $locked)
    Assert-Equal 1 @($summary | Where-Object { $_ -like 'PASS X2 *' }).Count ('the scenario itself passed; runner said: ' + $said)
    $warnLines = @($summary | Where-Object { $_ -like 'WARN X2 *could not be removed*' })
    Assert-Equal 1 $warnLines.Count ('the summary must carry one WARN line; runner said: ' + $said)
    $report = @(Get-Content -LiteralPath (Join-Path $copy 'e2e-report.txt'))
    Assert-Equal 1 @($report | Where-Object { $_ -like 'WARN X2 *could not be removed*' }).Count 'the report must carry the WARN line'
    Assert-Equal 1 @($report | Where-Object { $_ -like 'TOTAL 1 scenario(s), 1 passed, 0 failed*' }).Count 'the report total must not count a warning as a failure'

    Add-Evidence $Context 'S0: helpers behave as specified'
}

# Fixtures for the runner check above. Each is a complete scenario file that S0 writes into a copy of the runner.
# X1 cannot be cleaned up: it shadows Stop-Process with a no-op and leaves a hidden process running.
$script:S0FixtureLeak = @'
$ScenarioId = 'X1'
$ScenarioName = 'fixture leaks a process that cannot be killed'
function Stop-Process { [CmdletBinding()] param($Id, [switch]$Force) }
function Invoke-Scenario([hashtable]$Context) {
    $cmd = Join-Path $Context.Root 'leak.cmd'
    Set-Content -LiteralPath $cmd -Encoding Ascii -Value '@ping -n 60 127.0.0.1 > nul'
    Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $cmd -WindowStyle Hidden | Out-Null
    Wait-Until -Condition { @(Get-ProcessesUnder $Context.Root).Count -ge 1 } -TimeoutSec 30 -Description 'leaked process visible'
}
'@
# X3 leaves the same process, but replaces Stop-ProcessesUnder by a no-op that reports success: the kill step
# is blind, and only the runner's own check after the kill can see the leak.
$script:S0FixtureBlind = @'
$ScenarioId = 'X3'
$ScenarioName = 'fixture leaves a process the kill step never sees'
function Stop-ProcessesUnder { [CmdletBinding()] param($Root, $TimeoutSec) }
function Invoke-Scenario([hashtable]$Context) {
    $cmd = Join-Path $Context.Root 'leak.cmd'
    Set-Content -LiteralPath $cmd -Encoding Ascii -Value '@ping -n 60 127.0.0.1 > nul'
    Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $cmd -WindowStyle Hidden | Out-Null
    Wait-Until -Condition { @(Get-ProcessesUnder $Context.Root).Count -ge 1 } -TimeoutSec 30 -Description 'leaked process visible'
}
'@
# X4 leaves a process that CAN be killed: a copy of ping.exe run from inside the root, whose image file stays locked
# while it runs.
$script:S0FixtureKillable = @'
$ScenarioId = 'X4'
$ScenarioName = 'fixture leaves a killable process running from its root'
function Invoke-Scenario([hashtable]$Context) {
    $exe = Join-Path $Context.Root 'pinger.exe'
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\PING.EXE') -Destination $exe
    Start-Process -FilePath $exe -ArgumentList '-n', '60', '127.0.0.1' -WindowStyle Hidden | Out-Null
    Wait-Until -Condition { @(Get-ProcessesUnder $Context.Root).Count -ge 1 } -TimeoutSec 30 -Description 'process from the root visible'
}
'@
# X2 passes, but keeps a file open without sharing, so the runner cannot delete its scenario root.
$script:S0FixtureLocked = @'
$ScenarioId = 'X2'
$ScenarioName = 'fixture keeps its root directory locked'
function Invoke-Scenario([hashtable]$Context) {
    $script:lockHandle = [System.IO.File]::Open((Join-Path $Context.Root 'locked.bin'), [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
}
'@
