# tools/sample-host-load.ps1 -- what is using this machine's CPU and GPU, every few seconds.
#
# WHY. A frame-time line says a game process stalled; it cannot say what took the machine from
# it. On 2026-09-27 a two-client lane-B run showed the host at 100% CPU while the three Ironfront
# processes together used about five of 32 logical processors -- the rest went to something
# Get-Process cannot see, because a Hyper-V guest's CPU (Docker Desktop's WSL2 VM, and every
# container in it: the game servers AND the self-hosted GitHub runner) is not charged to any
# Windows process. This samples both sides so a stall can be matched to its cause.
#
# One JSON object per line: host CPU, hypervisor guest CPU (in logical processors), GPU 3D
# utilisation, the top processes by CPU in the window, and per-container CPU from docker stats.
#
# Usage:
#   pwsh tools/sample-host-load.ps1 -Out artifacts/profile/r3/host.jsonl -Seconds 300
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Out,
    [int] $Seconds = 300,
    [int] $IntervalSeconds = 5,
    [int] $TopProcesses = 6
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
if (Test-Path $Out) { Remove-Item $Out }

$logical = [Environment]::ProcessorCount
$counters = @(
    '\Processor(_Total)\% Processor Time',
    '\Hyper-V Hypervisor Logical Processor(_Total)\% Guest Run Time',
    '\GPU Engine(*engtype_3D)\Utilization Percentage'
)

function Snapshot {
    $table = @{}
    foreach ($p in Get-Process) {
        if ($null -ne $p.CPU) { $table[$p.Id] = @{ Name = $p.ProcessName; Cpu = $p.CPU } }
    }
    return $table
}

$start = Get-Date
$before = Snapshot
$beforeAt = Get-Date

while (((Get-Date) - $start).TotalSeconds -lt $Seconds) {
    $samples = (Get-Counter -Counter $counters -SampleInterval $IntervalSeconds -MaxSamples 1 -ErrorAction SilentlyContinue).CounterSamples
    $after = Snapshot
    $afterAt = Get-Date
    $wall = ($afterAt - $beforeAt).TotalSeconds

    $top = foreach ($id in $after.Keys) {
        if ($before.ContainsKey($id)) {
            $cores = ($after[$id].Cpu - $before[$id].Cpu) / $wall
            if ($cores -gt 0.05) { [pscustomobject]@{ name = "$($after[$id].Name):$id"; cores = [math]::Round($cores, 2) } }
        }
    }
    $top = @($top | Sort-Object cores -Descending | Select-Object -First $TopProcesses)

    $containers = @{}
    foreach ($line in (docker stats --no-stream --format "{{.Name}} {{.CPUPerc}}" 2>$null)) {
        $parts = $line -split ' '
        if ($parts.Count -ge 2) { $containers[$parts[0]] = [double]($parts[1].TrimEnd('%')) / 100 }
    }

    # [double] and ?? 0: a counter that returned nothing this window (the GPU one does between
    # processes) is a zero, and [math]::Round has no overload for $null.
    # The full counter name: '*processor(_total)*' also matches the Hyper-V logical-processor path.
    $hostCpu = [double](($samples | Where-Object Path -like '*\processor(_total)\% processor time').CookedValue ?? 0)
    $guest   = [double](($samples | Where-Object Path -like '*guest run time*').CookedValue ?? 0)
    $gpu     = [double](($samples | Where-Object Path -like '*engtype_3d*' | Measure-Object CookedValue -Sum).Sum ?? 0)

    $row = [ordered]@{
        t          = [math]::Round(($afterAt - $start).TotalSeconds)
        wall       = $afterAt.ToUniversalTime().ToString("HH:mm:ss")
        hostCpuPct = [math]::Round($hostCpu, 1)
        guestLps   = [math]::Round($guest * $logical / 100, 1)
        gpu3dPct   = [math]::Round($gpu, 1)
        top        = $top
        containers = $containers
    }
    ($row | ConvertTo-Json -Compress -Depth 4) | Out-File $Out -Append -Encoding utf8

    $before = $after
    $beforeAt = $afterAt
}
