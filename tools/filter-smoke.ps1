<#
.SYNOPSIS
    Test fixture for exercising ThwargFilter code paths WITHOUT a running acclient.exe.

.DESCRIPTION
    ThwargFilter is a .NET 2.0 x86 assembly that normally lives inside the game process,
    injected by Decal. That makes it awkward to test: most of its interesting code is
    internal, and it references Decal assemblies that are not loadable in a normal host.

    This script solves the three problems that make that hard, so a smoke test can just
    dot-source it and get straight to the code under test. Each is a real trap that has
    cost time more than once:

    1. THE 32-BIT REQUIREMENT.
       ThwargFilter.dll is x86. Loading it from 64-bit PowerShell fails with
       BadImageFormatException ("an attempt was made to load a program with an incorrect
       format"). Run smoke tests through the 32-bit host:
           C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe
       Use Assert-FilterSmoke32Bit below to fail loudly instead of mysteriously.

    2. THE DECAL ASSEMBLY REDIRECT.
       Decal.Adapter 2.9.7.5 (the vendored copy this project builds against) references
       Decal.Interop.* 2.9.7.5, but a Decal install typically registers a DIFFERENT version
       in the GAC (2.9.8.3 at time of writing). Inside acclient the real Decal runtime
       supplies these; in a test host the load simply fails. Register-DecalAssemblyRedirect
       resolves them by simple name, ignoring version, and points Decal.Adapter itself at
       the installed copy.

    3. JIT-TIME EXCEPTIONS BYPASS try/catch.
       This is the one that misleads people. If a method touches a type from an assembly
       that cannot load, the failure happens when the METHOD IS JITTED, before its body
       runs, so the method's own try/catch never fires. The symptom is a
       FileNotFoundException escaping a method that visibly cannot throw, and an empty log.
       That means "the redirect is missing", NOT "the guard is broken". Without the
       redirect these tests report false failures.

    Also included: Invoke-FilterStatic / Invoke-FilterMember, which unwrap PowerShell's
    PSObject before calling reflection. New-Object returns a PSObject-wrapped instance, and
    MethodInfo.Invoke rejects it with a confusing message like
    "Object of type 'System.Management.Automation.PSObject' cannot be converted to
    type 'System.Collections.Generic.Dictionary`2[...]'".

.EXAMPLE
    # From 32-bit PowerShell:
    . "$PSScriptRoot\..\tools\filter-smoke.ps1"
    Assert-FilterSmoke32Bit
    $asm = Import-ThwargFilter
    $tracker = $asm.GetType("ThwargFilter.LoginStageTracker")
    Invoke-FilterStatic $tracker "GetStage" @()

.NOTES
    Read-only fixture: it loads the built DLL and calls into it. It does not deploy
    anything and does not touch the registry.
#>

$script:DefaultFilterBinPath =
    Join-Path (Split-Path -Parent $PSScriptRoot) "ThwargLauncher\ThwargFilter\bin\Debug"

# Reflection flags covering the internal/private members most of this assembly uses.
$script:FilterBindingFlags =
    [System.Reflection.BindingFlags]::NonPublic -bor `
    [System.Reflection.BindingFlags]::Public -bor `
    [System.Reflection.BindingFlags]::Static -bor `
    [System.Reflection.BindingFlags]::Instance

function Get-FilterBindingFlags {
    return $script:FilterBindingFlags
}

<#
.SYNOPSIS
    Fail fast if running in a 64-bit host, where the x86 filter DLL cannot load.
#>
function Assert-FilterSmoke32Bit {
    if ([IntPtr]::Size -ne 4) {
        throw ("ThwargFilter.dll is x86 and cannot load in this {0}-bit host. " +
               "Re-run with C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe") -f ([IntPtr]::Size * 8)
    }
}

<#
.SYNOPSIS
    Resolve Decal assemblies by simple name, ignoring version mismatches.
.DESCRIPTION
    Must be called BEFORE any code path that touches Decal types is JITted. See note 3 in
    the file header: without this, methods fail at JIT time and their try/catch never runs.
#>
function Register-DecalAssemblyRedirect {
    param(
        [string] $DecalInstallPath = "C:\Program Files (x86)\Decal 3.0"
    )

    if ($script:DecalRedirectRegistered) { return }

    $map = @{}
    Get-ChildItem "C:\Windows\Microsoft.NET\assembly\GAC_MSIL" -Recurse -Filter "Decal.Interop.*.dll" -ErrorAction SilentlyContinue |
        ForEach-Object { $map[$_.BaseName] = $_.FullName }

    $adapterPath = Join-Path $DecalInstallPath "Decal.Adapter.dll"
    if (Test-Path $adapterPath) {
        # Decal.Interop 2.9.8.3 references Decal.Adapter 2.9.8.3 back, so the installed
        # adapter must be resolvable too or the redirect chain dead-ends.
        $map["Decal.Adapter"] = $adapterPath
    }

    $global:ThwargFilterDecalMap = $map
    $resolver = [System.ResolveEventHandler] {
        param($sender, $eventArgs)
        $simpleName = ($eventArgs.Name -split ',')[0]
        if ($global:ThwargFilterDecalMap.ContainsKey($simpleName)) {
            return [System.Reflection.Assembly]::LoadFrom($global:ThwargFilterDecalMap[$simpleName])
        }
        return $null
    }
    [System.AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)

    $script:DecalRedirectRegistered = $true
    Write-Verbose ("Decal redirect registered for {0} assemblies" -f $map.Count)
}

<#
.SYNOPSIS
    Load ThwargFilter.dll (and its dependencies) and return the Assembly.
#>
function Import-ThwargFilter {
    param(
        [string] $BinPath = $script:DefaultFilterBinPath
    )

    Assert-FilterSmoke32Bit
    Register-DecalAssemblyRedirect

    $filterPath = Join-Path $BinPath "ThwargFilter.dll"
    if (-not (Test-Path $filterPath)) {
        throw "ThwargFilter.dll not found at '$filterPath'. Build the filter first."
    }

    $jsonPath = Join-Path $BinPath "Newtonsoft.Json.dll"
    if (Test-Path $jsonPath) {
        [System.Reflection.Assembly]::LoadFrom($jsonPath) | Out-Null
    }
    return [System.Reflection.Assembly]::LoadFrom($filterPath)
}

<#
.SYNOPSIS
    Strip PowerShell's PSObject wrapper so reflection sees the real CLR object.
#>
function ConvertTo-FilterArgArray {
    param([object[]] $Arguments)

    if ($null -eq $Arguments) { return New-Object object[] 0 }
    $argv = New-Object object[] $Arguments.Count
    for ($i = 0; $i -lt $Arguments.Count; $i++) {
        $value = $Arguments[$i]
        if ($null -ne $value -and $null -ne $value.PSObject -and $null -ne $value.PSObject.BaseObject) {
            $value = $value.PSObject.BaseObject
        }
        $argv[$i] = $value
    }
    return $argv
}

<#
.SYNOPSIS
    Call a static method (including internal/private ones) on a filter type.
#>
function Invoke-FilterStatic {
    param(
        [Parameter(Mandatory=$true)] [Type] $Type,
        [Parameter(Mandatory=$true)] [string] $MethodName,
        [object[]] $Arguments = @()
    )

    $method = $Type.GetMethod($MethodName, $script:FilterBindingFlags)
    if ($null -eq $method) { throw "No method '$MethodName' on $($Type.FullName)" }
    return $method.Invoke($null, (ConvertTo-FilterArgArray $Arguments))
}

<#
.SYNOPSIS
    Call an instance method (including internal/private ones) on a filter object.
#>
function Invoke-FilterMember {
    param(
        [Parameter(Mandatory=$true)] [object] $Instance,
        [Parameter(Mandatory=$true)] [string] $MethodName,
        [object[]] $Arguments = @()
    )

    $target = $Instance
    if ($null -ne $target.PSObject -and $null -ne $target.PSObject.BaseObject) {
        $target = $target.PSObject.BaseObject
    }
    $method = $target.GetType().GetMethod($MethodName, $script:FilterBindingFlags)
    if ($null -eq $method) { throw "No method '$MethodName' on $($target.GetType().FullName)" }
    return $method.Invoke($target, (ConvertTo-FilterArgArray $Arguments))
}

<#
.SYNOPSIS
    Construct a filter type that has a non-public constructor.
#>
function New-FilterObject {
    param(
        [Parameter(Mandatory=$true)] [Type] $Type,
        [object[]] $Arguments = @()
    )

    if ($Arguments.Count -eq 0) {
        return [System.Activator]::CreateInstance($Type, $true)
    }
    $argv = ConvertTo-FilterArgArray $Arguments
    foreach ($ctor in $Type.GetConstructors($script:FilterBindingFlags)) {
        if ($ctor.GetParameters().Count -eq $argv.Count) {
            return $ctor.Invoke($argv)
        }
    }
    throw "No constructor on $($Type.FullName) taking $($argv.Count) argument(s)"
}

<#
.SYNOPSIS
    Path of the filter log for THIS process, which is where filter output lands.
.DESCRIPTION
    The filter names its log by process id, so in a test host that is the PowerShell pid.
#>
function Get-FilterLogPath {
    return Join-Path $env:APPDATA ("ThwargLauncher\Logs\ThwargFilter_{0}_log.txt" -f $PID)
}

<#
.SYNOPSIS
    Delete this process's filter log so a test starts from a clean slate.
#>
function Clear-FilterLog {
    $path = Get-FilterLogPath
    if (Test-Path $path) { Remove-Item $path -Force }
}

<#
.SYNOPSIS
    Return filter log lines, optionally filtered by a regex.
#>
function Get-FilterLogLines {
    param([string] $Pattern)

    $path = Get-FilterLogPath
    if (-not (Test-Path $path)) { return @() }
    $lines = Get-Content $path
    if ([string]::IsNullOrEmpty($Pattern)) { return $lines }
    return ($lines | Where-Object { $_ -match $Pattern })
}

<#
.SYNOPSIS
    Path of the JSONL chat/observation log for THIS process.
#>
function Get-FilterChatLogPath {
    return Join-Path $env:APPDATA ("ThwargLauncher\Running\chatlog_{0}.jsonl" -f $PID)
}

<#
.SYNOPSIS
    Delete this process's chat log (and any rotated sibling).
#>
function Clear-FilterChatLog {
    $path = Get-FilterChatLogPath
    $rotated = Join-Path $env:APPDATA ("ThwargLauncher\Running\chatlog_{0}.1.jsonl" -f $PID)
    foreach ($p in @($path, $rotated)) {
        if (Test-Path $p) { Remove-Item $p -Force }
    }
}

<#
.SYNOPSIS
    Read the JSONL chat log back as objects, newest last.
#>
function Get-FilterChatLogEntries {
    param([string] $TypeFilter)

    $path = Get-FilterChatLogPath
    if (-not (Test-Path $path)) { return @() }
    $entries = @()
    foreach ($line in (Get-Content $path)) {
        if ([string]::IsNullOrEmpty($line)) { continue }
        try { $entries += ($line | ConvertFrom-Json) } catch { }
    }
    if (-not [string]::IsNullOrEmpty($TypeFilter)) {
        $entries = $entries | Where-Object { $_.type -eq $TypeFilter }
    }
    return $entries
}
