# tools/perf/thread_cpu.ps1 -- CPU of one process, thread by thread, every second (P33).
#
# WHY. The owner's report is that the client takes the whole CPU, and the metric for P33 is CPU
# use, not frame rate. Task Manager shows one number for the process; what decides the fix is
# WHICH threads burn it: the main thread, Unity's render thread, the graphics driver's own
# submission thread, the job workers. Each thread is named by its Windows thread description
# (Unity names its own: UnityGfxDeviceWorker, Job.Worker N, ...), an unnamed one by the module it
# started in: the game's own .exe is the main thread, nvwgf2umx.dll NVIDIA's DX11 driver, igd*
# Intel's, amd*/ati* AMD's. The module list is read again whenever a thread starts outside it: a
# driver loads after the process starts, and a list read at launch names its threads "unknown".
#
# One JSON object a line: the wall clock, the seconds since the process started (to line up with
# a player log's `t=` stamps, which count from engine start), the process's cores, and cores per
# thread name. tools/perf/thread_ab.py groups them by GpuCostProbe state.
#
# Usage:
#   pwsh tools/perf/thread_cpu.ps1 -Id <pid> -Out tmp/perf/threads-c1.jsonl -Seconds 600
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [int] $Id,
    [Parameter(Mandatory = $true)] [string] $Out,
    [int] $Seconds = 600,
    [double] $IntervalSeconds = 1.0
)

$ErrorActionPreference = "Stop"
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class IronfrontThreadNames
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern int GetThreadDescription(IntPtr handle, out IntPtr description);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationThread(IntPtr handle, int infoClass, out IntPtr info, int length, IntPtr returned);
    const uint QueryLimitedInformation = 0x0800;
    const uint QueryInformation = 0x0040;
    const int Win32StartAddress = 9;

    // ProcessThread.StartAddress reads 0 for every thread of a Unity player; asked directly with
    // full query access, Windows answers.
    public static long StartAddress(int id)
    {
        IntPtr handle = OpenThread(QueryInformation, false, (uint)id);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            IntPtr address;
            return NtQueryInformationThread(handle, Win32StartAddress, out address, IntPtr.Size, IntPtr.Zero) == 0 ? address.ToInt64() : 0;
        }
        finally { CloseHandle(handle); }
    }

    public static string Describe(int id)
    {
        IntPtr handle = OpenThread(QueryLimitedInformation, false, (uint)id);
        if (handle == IntPtr.Zero) return "";
        try
        {
            IntPtr text;
            if (GetThreadDescription(handle, out text) < 0 || text == IntPtr.Zero) return "";
            string name = Marshal.PtrToStringUni(text);
            LocalFree(text);
            return name ?? "";
        }
        finally { CloseHandle(handle); }
    }
}
"@

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
if (Test-Path $Out) { Remove-Item $Out }

$process = Get-Process -Id $Id
$started = $process.StartTime
$exeName = [System.IO.Path]::GetFileName($process.Path)
$script:modules = @()
$script:modulesFresh = $false
$names = @{}

function Read-Modules {
    $script:modules = @((Get-Process -Id $Id).Modules | ForEach-Object {
        [pscustomobject]@{ Name = $_.ModuleName; Base = $_.BaseAddress.ToInt64(); End = $_.BaseAddress.ToInt64() + $_.ModuleMemorySize }
    })
}

function Module-At([int64] $address) {
    return $script:modules | Where-Object { $address -ge $_.Base -and $address -lt $_.End } | Select-Object -First 1
}

function Label($thread) {
    if ($names.ContainsKey($thread.Id)) { return $names[$thread.Id] }
    $name = [IronfrontThreadNames]::Describe($thread.Id)
    if (-not $name) {
        $start = [IronfrontThreadNames]::StartAddress($thread.Id)
        $module = Module-At $start
        # Once a snapshot at most: a thread that starts in no module would otherwise re-read them.
        if (-not $module -and -not $script:modulesFresh) { Read-Modules; $script:modulesFresh = $true; $module = Module-At $start }
        $name = if (-not $module) { "unknown" } elseif ($module.Name -eq $exeName) { "main" } else { $module.Name }
    }
    $names[$thread.Id] = $name
    return $name
}

function Snapshot {
    $p = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    $script:modulesFresh = $false
    $table = @{}
    foreach ($t in $p.Threads) {
        try { $table[$t.Id] = @{ Name = (Label $t); Cpu = $t.TotalProcessorTime.TotalSeconds } } catch { }
    }
    return @{ At = Get-Date; Total = $p.TotalProcessorTime.TotalSeconds; Threads = $table }
}

$deadline = (Get-Date).AddSeconds($Seconds)
$before = Snapshot
while ($before -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds ([int]($IntervalSeconds * 1000))
    $after = Snapshot
    if (-not $after) { break }
    $wall = ($after.At - $before.At).TotalSeconds
    $byName = @{}
    # Not $id: PowerShell names are case-insensitive, and $id IS the -Id parameter.
    foreach ($threadId in $after.Threads.Keys) {
        if (-not $before.Threads.ContainsKey($threadId)) { continue }
        $cores = ($after.Threads[$threadId].Cpu - $before.Threads[$threadId].Cpu) / $wall
        $name = $after.Threads[$threadId].Name
        $byName[$name] = [math]::Round(($byName[$name] + $cores), 4)
    }
    $line = [ordered]@{
        wall = [math]::Round(([DateTimeOffset]$after.At).ToUnixTimeMilliseconds() / 1000.0, 3)
        uptime = [math]::Round(($after.At - $started).TotalSeconds, 2)
        total = [math]::Round(($after.Total - $before.Total) / $wall, 4)
        threads = $byName
    }
    ($line | ConvertTo-Json -Compress -Depth 3) | Add-Content -Path $Out
    $before = $after
}
