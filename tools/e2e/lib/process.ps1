# Process helpers. Needs common.ps1 (Wait-Until, Write-Diag). ASCII only, LF line endings.
# Processes are located by the path of their executable:
# after an update the sample restarts itself, so the pid returned by Start-Process is stale.
Set-StrictMode -Version Latest

# 'C:\a\b' -> 'C:\a\b\'. Works for a path that does not exist (yet / any more). Refuses a drive root:
# matching "everything under C:\" would select, and Stop-ProcessesUnder would kill, every process.
function Get-ProcessLocationPrefix([string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Root)) { throw 'process location must not be empty' }
    $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root)
    $prefix = $full.TrimEnd('\') + '\'
    if ([System.IO.Path]::GetPathRoot($prefix) -eq $prefix) { throw "refusing a drive root as a process location: $Root" }
    return $prefix
}

# Returns the processes whose executable lives under $Root, or whose command line mentions $Root
# (needed for hosts such as cmd.exe / dotnet.exe that live elsewhere and are given a path under $Root).
# -IncludeDescendants also returns every child, grandchild, ... of those (a child's own path and
# command line may not mention $Root at all, e.g. the ping that a .cmd file starts).
# The current process is never returned. The result is UNROLLED: wrap the call in @(...) before using
# .Count or [0] (calling convention in the header of common.ps1).
function Get-ProcessesUnder([string]$Root, [switch]$IncludeDescendants) {
    $prefix = Get-ProcessLocationPrefix $Root
    $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    $selected = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    foreach ($p in $all) {
        if ($p.ProcessId -eq $PID) { continue }
        if (($p.ExecutablePath -and $p.ExecutablePath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) -or
            ($p.CommandLine -and $p.CommandLine.IndexOf($prefix, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            $seen[[int]$p.ProcessId] = $true
            $selected.Add($p)
        }
    }
    if ($IncludeDescendants) {
        # Breadth-first, so $selected ends up parents-before-children. A child must not be older than
        # its parent: that would be a recycled pid, not a real parent link.
        for ($i = 0; $i -lt $selected.Count; $i++) {
            $parent = $selected[$i]
            foreach ($c in $all) {
                if ($c.ProcessId -eq $PID -or $seen.ContainsKey([int]$c.ProcessId)) { continue }
                if ($c.ParentProcessId -ne $parent.ProcessId) { continue }
                if ($c.CreationDate -and $parent.CreationDate -and $c.CreationDate -lt $parent.CreationDate) { continue }
                $seen[[int]$c.ProcessId] = $true
                $selected.Add($c)
            }
        }
    }
    return $selected.ToArray()      # unrolled: callers wrap the call in @(...) (see the header of common.ps1)
}

# Kills every process under $Root together with all of its descendants, then waits until none is left
# (an executable that is still running keeps its file locked, which would defeat the directory delete
# that follows). Returns nothing when the tree is gone. THROWS when, after $TimeoutSec, a process is
# still there (kill refused, protected process, ...): the message lists each survivor with the last kill
# error. A survivor is a leak, so the runner turns that exception into a failed run (see run-e2e.ps1);
# a scenario that calls this mid-way lets it propagate and fails the same way.
function Stop-ProcessesUnder([string]$Root, [int]$TimeoutSec = 15) {
    $lastError = @{}
    try {
        Wait-Until -TimeoutSec $TimeoutSec -Description "processes under $Root exit" -Condition {
            $left = @(Get-ProcessesUnder $Root -IncludeDescendants)
            # Children before parents, so that a parent cannot spawn a replacement mid-way.
            for ($i = $left.Count - 1; $i -ge 0; $i--) {
                try { Stop-Process -Id $left[$i].ProcessId -Force -ErrorAction Stop }
                catch { $lastError[[int]$left[$i].ProcessId] = $_.Exception.Message }
            }
            $left.Count -eq 0
        }
    }
    catch {
        # Timed out. Judge by a fresh look, not by the last snapshot: the final round of kills may have worked.
        $survivors = @(Get-ProcessesUnder $Root -IncludeDescendants)
        if ($survivors.Count -gt 0) {
            $each = @($survivors | ForEach-Object {
                $text = '{0} ({1})' -f $_.ProcessId, $_.Name
                if ($lastError.ContainsKey([int]$_.ProcessId)) { $text += ': ' + $lastError[[int]$_.ProcessId] }
                $text
            })
            throw ('{0} process(es) under {1} survived the kill for {2}s: {3}' -f $survivors.Count, $Root, $TimeoutSec, ($each -join '; '))
        }
    }
}

function Wait-ProcessExit([int]$ProcessId, [int]$TimeoutSec = 60) {
    Wait-Until -Condition { -not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) } `
               -TimeoutSec $TimeoutSec -Description "process $ProcessId exits"
}
