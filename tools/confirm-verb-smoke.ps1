<#
.SYNOPSIS
    Smoke test for the "confirm yes|no" verb and the "appraise id:<guid>" form,
    WITHOUT a running acclient.exe.

.DESCRIPTION
    Covers everything about these two features that is testable off-client:

      * the launcher command router reaches the new confirm branch (proved by the filter
        log line it emits, with a control that shows an unrouted verb behaves differently),
      * argument parsing for confirm (yes/no/y/n, force, at:X,Y) and for appraise id:
        (decimal, unsigned decimal, and 0x hex above 0x7FFFFFFF),
      * the outstanding-confirmation state machine as the heartbeat and dumpstate see it,
      * the ConfirmationAnswer chat-log record.

    What it CANNOT cover, because it needs a live client and a live server: whether the
    posted mouse click actually lands on the client's confirmation panel, and whether the
    server then accepts the answer. See TESTING_CHANNEL.md section 14.

.EXAMPLE
    C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -File tools\confirm-verb-smoke.ps1
#>

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "filter-smoke.ps1")

$script:Failures = 0
function Check {
    param([string] $Name, [bool] $Ok, [string] $Detail)
    if ($Ok) { Write-Output ("  PASS  " + $Name + $(if ($Detail) { " -- " + $Detail } else { "" })) }
    else { Write-Output ("  FAIL  " + $Name + $(if ($Detail) { " -- " + $Detail } else { "" })); $script:Failures++ }
}

Assert-FilterSmoke32Bit
$asm = Import-ThwargFilter
Write-Output ("Loaded " + $asm.FullName)

$confirmerType = $asm.GetType("ThwargFilter.Confirmer", $true)
$appraiserType = $asm.GetType("ThwargFilter.Appraiser", $true)
$parserType = $asm.GetType("ThwargFilter.ThwargFilterCommandParser", $true)
$execType = $asm.GetType("ThwargFilter.ThwargFilterCommandExecutor", $true)
$statusType = $asm.GetType("ThwargFilter.HeartbeatGameStatus", $true)
$flags = Get-FilterBindingFlags

Write-Output ""
Write-Output "1. Heartbeat contract"
Check "heartbeat file version bumped" ($statusType.GetField("MASTER_FILE_VERSION").GetValue($null) -eq "1.6") ("version = " + $statusType.GetField("MASTER_FILE_VERSION").GetValue($null))
Check "compat prefix unchanged" ($statusType.GetField("MASTER_FILE_VERSION_COMPAT").GetValue($null) -eq "1")
foreach ($f in @("ConfirmationState", "ConfirmationType", "ConfirmationContext", "ConfirmationText", "ConfirmationAnswer")) {
    Check ("heartbeat field " + $f) ($null -ne $statusType.GetField($f))
}

Write-Output ""
Write-Output "2. Cold state, before any confirmation has been seen"
$statusArgs = [object[]]@($null, 0, 0, $null, $null)
$getStatus = $confirmerType.GetMethod("GetStatus", $flags)
$getStatus.Invoke($null, $statusArgs) | Out-Null
Check "cold ConfirmationState is 'none'" ($statusArgs[0] -eq "none") ("state = " + $statusArgs[0])

$dict = New-Object "System.Collections.Generic.Dictionary[string,object]"
$notes = New-Object "System.Collections.Generic.List[string]"
Invoke-FilterStatic $confirmerType "AddState" @($dict, $notes) | Out-Null
Check "dumpstate carries a confirmation section" ($dict.ContainsKey("confirmation"))
if ($dict.ContainsKey("confirmation")) {
    $section = $dict["confirmation"]
    Check "confirmation section state" ($section["state"] -eq "none") ("keys = " + (($section.Keys) -join ","))
}
Check "no notes raised" ($notes.Count -eq 0)

Write-Output ""
Write-Output "3. appraise id: guid parsing (TryParseId)"
$tryParseId = $appraiserType.GetMethod("TryParseId", $flags)
function Test-Guid {
    param([string] $Text, [bool] $ExpectOk, [int] $Expected)
    $a = [object[]]@($Text, 0)
    $ok = $tryParseId.Invoke($null, $a)
    if (-not $ExpectOk) { return (-not $ok) }
    return ($ok -and ($a[1] -eq $Expected))
}
Check "decimal signed"            (Test-Guid "1073741825" $true 1073741825)
Check "decimal negative (Decal's own signed form)" (Test-Guid "-2147481121" $true (-2147481121))
Check "unsigned decimal above int.MaxValue" (Test-Guid "2147486175" $true (-2147481121))
Check "0x hex below 0x80000000"   (Test-Guid "0x4000001A" $true 1073741850)
# 0x80000ADF is 2147486431 unsigned, which is -2147480865 once reinterpreted as Int32.
Check "0x hex above 0x7FFFFFFF"   (Test-Guid "0x80000ADF" $true (-2147480865))
Check "garbage rejected"          (Test-Guid "not-an-id" $false 0)
Check "empty rejected"            (Test-Guid "" $false 0)

Write-Output ""
Write-Output "4. confirm at:X,Y parsing (TryParsePoint)"
$tryParsePoint = $confirmerType.GetMethod("TryParsePoint", $flags)
$p = [object[]]@("401,322", 0, 0)
$okPoint = $tryParsePoint.Invoke($null, $p)
Check "at:401,322 parses" ($okPoint -and $p[1] -eq 401 -and $p[2] -eq 322) ("x = " + $p[1] + ", y = " + $p[2])
$p2 = [object[]]@("401", 0, 0)
Check "at:401 (missing y) rejected" (-not $tryParsePoint.Invoke($null, $p2))

Write-Output ""
Write-Output "5. Launcher command routing, with a control"
Clear-FilterLog
Clear-FilterChatLog
$exec = New-FilterObject $execType
$parser = New-FilterObject $parserType @($exec)

# No Confirmer wired: the confirm branch reports that and returns. An UNROUTED verb
# instead falls through to the executor, which dispatches into the client and cannot
# work here. The two outcomes differ, which is what proves the routing.
Invoke-FilterMember $parser "ExecuteCommandFromLauncher" @("confirm yes") | Out-Null
$routed = (Get-FilterLogLines "confirm requested but no Confirmer is wired up").Count -gt 0
Check "'confirm yes' reached the confirm branch" $routed

$fellThrough = $false
try { Invoke-FilterMember $parser "ExecuteCommandFromLauncher" @("someunroutedverb") | Out-Null }
catch { $fellThrough = $true }
$controlLines = (Get-FilterLogLines "someunroutedverb").Count
Check "control: an unrouted verb behaves differently" ($fellThrough -or $controlLines -gt 0) ("threw = " + $fellThrough)

Write-Output ""
Write-Output "6. confirm argument handling end to end"
$confirmer = New-FilterObject $confirmerType
$parserType.GetProperty("Confirm", $flags).SetValue($parser, $confirmer, $null)

Clear-FilterChatLog
Invoke-FilterMember $parser "ExecuteCommandFromLauncher" @("confirm sideways") | Out-Null
$badArgs = @(Get-FilterChatLogEntries "ConfirmationAnswer")
$sawBadArgs = $false
foreach ($entry in $badArgs) { if ($entry.outcome -eq "badargs" -and $entry.source -eq "confirmation") { $sawBadArgs = $true } }
Check "'confirm sideways' is rejected as badargs" $sawBadArgs ("records = " + $badArgs.Count)

Clear-FilterChatLog
Invoke-FilterMember $parser "ExecuteCommandFromLauncher" @("/tf confirm yes at:401,322") | Out-Null
$accepted = @(Get-FilterChatLogEntries "ConfirmationAnswer")
$rejected = $false
foreach ($entry in $accepted) { if ($entry.outcome -eq "badargs") { $rejected = $true } }
Check "'/tf confirm yes at:401,322' is accepted (queued, not rejected)" (-not $rejected)
Check "queue is logged" ((Get-FilterLogLines "confirm: queued answer 'yes'").Count -gt 0)

Clear-FilterLog
Clear-FilterChatLog

Write-Output ""
if ($script:Failures -eq 0) { Write-Output "ALL CHECKS PASSED" }
else { Write-Output ("FAILURES: " + $script:Failures) }
exit $script:Failures
