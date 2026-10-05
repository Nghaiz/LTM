# tools/perf/thread_cpu.ps1 -- CPU of one process, thread by thread, every second (P33).
#
# WHY. The owner's report is that the client takes the whole CPU, and the metric for P33 is CPU
# use, not frame rate. Task Manager shows one number for the process; what decides the fix is
# WHICH threads burn it: the main thread, Unity's render thread, the graphics driver's own
# submission thread, the job workers. Each thread is named by its Windows thread description
# (Unity names its own: UnityGfxDeviceWorker, Job.Worker N, ...), the oldest unnamed thread is the
# main thread, and any other unnamed thread by the module it started in (nvwgf2umx.dll is
# NVIDIA's DX11 driver, igd* Intel's, amd*/ati* AMD's).
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
    const uint QueryLimitedInformation = 0x0800;

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
$modules = @($process.Modules | ForEach-Object {
    [pscustomobject]@{ Name = $_.ModuleName; Base = $_.BaseAddress.ToInt64(); End = $_.BaseAddress.ToInt64() + $_.ModuleMemorySize }
})
$names = @{}

function Label($thread, [int64] $oldestTicks) {
    if ($names.ContainsKey($thread.Id)) { return $names[$thread.Id] }
    $name = [IronfrontThreadNames]::Describe($thread.Id)
    if (-not $name) {
        try { if ($thread.StartTime.Ticks -eq $oldestTicks) { $name = "main" } } catch { }
    }
    if (-not $name) {
        $start = $thread.StartAddress.ToInt64()
        $module = $modules | Where-Object { $start -ge $_.Base -and $start -lt $_.End } | Select-Object -First 1
        $name = if ($module) { $module.Name } else { "unknown" }
    }
    $names[$thread.Id] = $name
    return $name
}

function Snapshot {
    $p = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    $oldest = ($p.Threads | ForEach-Object { try { $_.StartTime.Ticks } catch { [int64]::MaxValue } } | Measure-Object -Minimum).Minimum
    $table = @{}
    foreach ($t in $p.Threads) {
        try { $table[$t.Id] = @{ Name = (Label $t $oldest); Cpu = $t.TotalProcessorTime.TotalSeconds } } catch { }
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
