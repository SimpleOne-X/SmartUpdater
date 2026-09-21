# Shared helpers. ASCII only (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI). LF line endings.
#
# CALLING CONVENTION FOR ARRAY-RETURNING HELPERS (Read-EventLog, Get-EventLines, Get-ProcessesUnder,
# and any helper written after them) - read this before writing a scenario.
#
#   They return their items UNROLLED, like every ordinary PowerShell function: no item = nothing,
#   one item = that bare item, two or more = an array. THE CALLER WRAPS THE CALL IN @(...):
#
#       $lines = @(Read-EventLog $log)
#       Assert-True (@(Get-EventLines $lines 'exiting').Count -ge 1) 'no exiting event'
#       $procs = @(Get-ProcessesUnder $root)
#
#   Piping a call straight into another cmdlet is fine and needs no wrapper:
#       Get-EventLines $lines 'started' | Measure-Object
#
#   Without the @(...), '(f).Count' throws under Set-StrictMode -Version Latest for no item and for one
#   string item, and '(f)[0]' on one string item is its first CHARACTER. Do NOT "fix" that inside the helper
#   with 'return , $array' (the unary comma): the array then travels as ONE element, so @(f).Count is 1 for
#   no hit and '| Measure-Object' counts 1 whatever the size - an assertion like
#   'Assert-True (@(Get-EventLines ...).Count -ge 1)' becomes true forever, a silent false green.
#
#   S0 pins this for 0, 1 and 2 results, through @(...) and through the pipeline.
Set-StrictMode -Version Latest

function Write-Step([string]$Message) { Write-Host ('    - ' + $Message) }
function Write-Diag([string]$Message) { Write-Host ('      ! ' + $Message) }

function Assert-True($Condition, [string]$Message) {
    if (-not $Condition) { throw "ASSERT FAILED: $Message" }
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "ASSERT FAILED: $Message (expected '$Expected', actual '$Actual')" }
}

function Assert-FileExists([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "ASSERT FAILED: missing file $Path" }
}

function Assert-NoFile([string]$Path) {
    if (Test-Path -LiteralPath $Path) { throw "ASSERT FAILED: file should not exist: $Path" }
}

function Wait-Until {
    param(
        [Parameter(Mandatory)][scriptblock]$Condition,
        [int]$TimeoutSec = 120,
        [Parameter(Mandatory)][string]$Description,
        [int]$PollMs = 200
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        if (& $Condition) { return }
        if ((Get-Date) -ge $deadline) { throw "TIMEOUT after ${TimeoutSec}s waiting for: $Description" }
        Start-Sleep -Milliseconds $PollMs     # polling interval, NOT a synchronization guess
    }
}

# Runs a native command (a scriptblock such as { dotnet build ... }) and returns
# [pscustomobject]@{ ExitCode; Output } with stdout and stderr merged into Output as plain strings.
#
# Why this exists: the runner sets $ErrorActionPreference='Stop'. On Windows PowerShell 5.1 a native
# command whose stderr is redirected with 2>&1 emits ErrorRecords, and under 'Stop' the FIRST stderr
# line becomes a terminating NativeCommandError - the exit-code check after the call is never reached.
# dotnet, git and friends write progress or warnings to stderr all the time. So the preference is
# lowered to 'Continue' for the duration of the call and always restored.
function Invoke-Native([scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $global:LASTEXITCODE = 0        # under StrictMode an unset $LASTEXITCODE is an error
    try {
        $output = @(& $Command 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.ToString() } else { [string]$_ }
        })
        $exitCode = $global:LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function New-ScenarioRoot {
    $root = Join-Path $env:TEMP ('smartupdater-e2e-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    return $root
}

# The next two functions return their lines unrolled: wrap calls in @(...) (see the header of this file).
function Read-EventLog([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    # The sample flushes every line, but a line can still be observed half-written; drop malformed ones.
    Get-Content -LiteralPath $Path -Encoding UTF8 | Where-Object { $_ -match '^ts=\S+\s+event=\S+' }
}

function Get-EventValue([string]$Line, [string]$Key) {
    # Anchored on a word boundary so that 'version' never matches 'updated-from-version'.
    $m = [regex]::Match($Line, '(?:^|\s)' + [regex]::Escape($Key) + '=([^\s]*)')
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

function Get-EventLines([string[]]$Lines, [string]$EventName) {
    $Lines | Where-Object { (Get-EventValue $_ 'event') -eq $EventName }
}

function Add-Evidence([hashtable]$Context, [string]$Text) {
    if (-not $Context.ContainsKey('Evidence')) { $Context.Evidence = New-Object System.Collections.Generic.List[string] }
    $Context.Evidence.Add($Text) | Out-Null
}

function Write-ScenarioDiagnostics([hashtable]$Context) {
    # Every block has its own try/catch: a broken diagnostic must never hide the real failure.
    try {
        if ($Context.ContainsKey('EventLog') -and (Test-Path -LiteralPath $Context.EventLog)) {
            Write-Diag '--- event log (last 30 lines) ---'
            Get-Content -LiteralPath $Context.EventLog -Encoding UTF8 -Tail 30 | ForEach-Object { Write-Diag $_ }
        }
    } catch { Write-Diag ('event log diagnostics failed: ' + $_.Exception.Message) }
    try {
        if ($Context.ContainsKey('Server') -and $Context.Server) {
            Write-Diag '--- reports received by MockServer ---'
            Write-Diag (Get-MockServerReports $Context.Server | ConvertTo-Json -Depth 6 -Compress)
        }
    } catch { Write-Diag ('report diagnostics failed: ' + $_.Exception.Message) }
    try {
        if ($Context.ContainsKey('InstallDir') -and (Test-Path -LiteralPath $Context.InstallDir)) {
            Write-Diag '--- install directory ---'
            $base = (Resolve-Path -LiteralPath $Context.InstallDir).Path
            Get-ChildItem -LiteralPath $Context.InstallDir -Recurse -File -Force |
                ForEach-Object { Write-Diag (('{0,10}  {1}' -f $_.Length, $_.FullName.Substring($base.Length + 1))) }
        }
    } catch { Write-Diag ('install dir diagnostics failed: ' + $_.Exception.Message) }
}
