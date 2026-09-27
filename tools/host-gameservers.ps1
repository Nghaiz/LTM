#!/usr/bin/env pwsh
# tools/host-gameservers.ps1 -- run the Dustbowl and Island game servers as native processes on
# this Windows host, registered with the fly master, instead of as pods in the VMware VM.
#
# WHY THE GAME SERVERS LEFT THE VM (measured 2026-09-27). The VM (192.168.94.130, 12 vCPU) also
# runs the DevOps Learning Platform: a kubeadm control plane, Calico, Prometheus, Loki, Grafana.
# With two players in an Island match the VM's load average went from 4 to 100, CPU pressure
# (/proc/pressure/cpu "some") sat at 88%, kernel time at 57%, and the platform's own probes began
# failing. The Island server fell to 3-5 frames a second -- mean frame 200-300 ms against a 33 ms
# tick -- which is every symptom of the 2026-09-27 playtest at once: bots and vehicles in slow
# motion, a tank that barely moves, a helicopter the client keeps snapping back, the local body
# corrected up and down. The same build on this host holds its frame rate. Capping the Unity job
# workers (NetServerBootstrap.CapJobWorkers) fixed the servers' own waste; it cannot fix a VM that
# is saturated by another project.
#
# WHAT IT DOES. Starts one batchmode Ironfront.exe per map (the player build doubles as the
# server, exactly as tools/playtest-local.ps1 uses it), each advertising -PublicIp so players on
# the Radmin network reach it directly -- no VMware NAT in the path, so the vmnat UDP stall
# (tools/check-vmnat-udp.ps1) cannot happen here. Idempotent: a map whose server is already
# running is left alone. The ports default to 27115/27116 because vmnat still holds 27015/27016
# on this host for the VM's copies.
#
# THE VM COPIES MUST BE SCALED TO ZERO while these run, or the master has two servers per map
# and a room may land on the starving one:
#   ssh nghaiz@192.168.94.130 kubectl -n ironfront scale deployment/game-server-island deployment/game-server-dustbowl --replicas=0
# and back to 1 to return to the VM.
#
# The shared secret comes from IRONFRONT_SHARED_SECRET, else from the repo's gitignored .env; it
# must be the one the fly master signs tickets with, or every join fails with InvalidTicket.
#
# Usage:
#   pwsh tools/host-gameservers.ps1            # start both maps (idempotent)
#   pwsh tools/host-gameservers.ps1 -Status    # what is running, and the last frame line of each
#   pwsh tools/host-gameservers.ps1 -Stop

[CmdletBinding()]
param(
    [switch] $Stop,
    [switch] $Status,
    [string] $PlayerPath = "build/windows/Ironfront.exe",
    [string] $MasterHost = "kien-master-2026.fly.dev",
    [int]    $MasterPort = 443,
    [string] $PublicIp = "26.18.240.157",
    [int]    $DustbowlPort = 27115,
    [int]    $IslandPort = 27116,
    # Unity sizes its job pool to the machine: 31 workers on this host, per process.
    [int]    $JobWorkers = 4,
    [string] $LogDir = "tmp/host-gameservers"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$maps = @(
    @{ Scene = "Dustbowl"; MapId = 1; Port = $DustbowlPort },
    @{ Scene = "Island";   MapId = 2; Port = $IslandPort }
)

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$logRoot = (Resolve-Path $LogDir).Path

function Get-MapServer([string] $Scene) {
    $log = Join-Path $logRoot "$Scene.log"
    Get-CimInstance Win32_Process -Filter "Name = 'Ironfront.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains("-batchmode") -and $_.CommandLine.Contains($log) }
}

function Get-LastFrameLine([string] $Scene) {
    $log = Join-Path $logRoot "$Scene.log"
    if (-not (Test-Path $log)) { return "" }
    $line = Select-String -Path $log -Pattern "^\[frames\]" | Select-Object -Last 1
    if ($line) { return $line.Line } else { return "" }
}

if ($Status) {
    foreach ($map in $maps) {
        $proc = Get-MapServer $map.Scene
        $state = if ($proc) { "running pid $($proc.ProcessId)" } else { "stopped" }
        Write-Host ("[host-gs] {0,-8} udp {1}: {2}" -f $map.Scene, $map.Port, $state)
        $frames = Get-LastFrameLine $map.Scene
        if ($frames) { Write-Host "          $frames" }
    }
    exit 0
}

if ($Stop) {
    foreach ($map in $maps) {
        $proc = Get-MapServer $map.Scene
        if (-not $proc) { continue }
        Stop-Process -Id $proc.ProcessId -Force
        Write-Host "[host-gs] stopped $($map.Scene) (pid $($proc.ProcessId))"
    }
    exit 0
}

$player = Join-Path $repoRoot $PlayerPath
if (-not (Test-Path $player)) { throw "no player build at $PlayerPath -- run tools/build-player.ps1 first." }

$secret = $env:IRONFRONT_SHARED_SECRET
if (-not $secret -and (Test-Path (Join-Path $repoRoot ".env"))) {
    $line = Get-Content (Join-Path $repoRoot ".env") | Where-Object { $_ -match '^IRONFRONT_SHARED_SECRET=' } | Select-Object -First 1
    if ($line) { $secret = ($line -replace '^IRONFRONT_SHARED_SECRET=', '').Trim() }
}
if (-not $secret) { throw "IRONFRONT_SHARED_SECRET is not set and .env has none; the master would refuse every ticket." }

# A shell that ran a client or a lane-B set carries variables that turn this process into
# something else: IRONFRONT_ROLE=client declines to host, IRONFRONT_LANEB_ROLE hands it to the harness.
foreach ($stale in @("IRONFRONT_ROLE", "IRONFRONT_LANEB_ROLE", "IRONFRONT_LANEB_LABEL", "IRONFRONT_LANEB_SCENE",
                     "IRONFRONT_LANEB_PROGRAMME", "IRONFRONT_LANEB_OUTPUT", "IRONFRONT_CLIENT_HOST",
                     "IRONFRONT_CLIENT_PORT", "IRONFRONT_LOG_SHOTS", "IRONFRONT_LOG_LOADOUT", "IRONFRONT_LOG_VEHICLE")) {
    Remove-Item ("Env:" + $stale) -ErrorAction SilentlyContinue
}

$env:IRONFRONT_SHARED_SECRET                     = $secret
$env:IRONFRONT_MASTER_HOST                       = $MasterHost
$env:IRONFRONT_MASTER_PORT                       = "$MasterPort"
$env:IRONFRONT_GAMESERVER_MASTER_TLS             = "1"
$env:IRONFRONT_GAMESERVER_MASTER_TLS_TARGET_HOST = $MasterHost
$env:IRONFRONT_GAMESERVER_PUBLIC_IP              = $PublicIp
$env:IRONFRONT_GAMESERVER_TRANSPORT              = "udp"
$env:IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS = "0"
$env:IRONFRONT_GAMESERVER_MAX_CONNECTIONS        = "16"
$env:IRONFRONT_GAMESERVER_MAX_PLAYERS            = "16"
# One line every five seconds per server: the frame rate is the first thing to read when a
# match feels slow, and it costs nothing.
$env:IRONFRONT_LOG_FRAMES                        = "1"

foreach ($map in $maps) {
    $existing = Get-MapServer $map.Scene
    if ($existing) {
        Write-Host "[host-gs] $($map.Scene) already running (pid $($existing.ProcessId))"
        continue
    }

    $env:IRONFRONT_GAMESERVER_SCENE    = $map.Scene
    $env:IRONFRONT_GAMESERVER_MAP_IDS  = "$($map.MapId)"
    $env:IRONFRONT_GAMESERVER_UDP_PORT = "$($map.Port)"
    $log = Join-Path $logRoot "$($map.Scene).log"

    $proc = Start-Process -FilePath $player -PassThru -WindowStyle Hidden -ArgumentList @(
        "-batchmode", "-nographics", "-job-worker-count", "$JobWorkers", "-logFile", $log)
    Write-Host "[host-gs] started $($map.Scene) on udp $($map.Port) (pid $($proc.Id)), log $log"
}

# Registration is the proof, not the process: a server that cannot reach the master or whose
# secret is wrong keeps running and hosts nobody.
$deadline = (Get-Date).AddSeconds(90)
$pending = @($maps | ForEach-Object { $_.Scene })
while ($pending.Count -gt 0 -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $pending = @($pending | Where-Object {
        $log = Join-Path $logRoot "$_.log"
        -not ((Test-Path $log) -and (Select-String -Path $log -Pattern "reporting to master as server" -Quiet))
    })
}

if ($pending.Count -gt 0) {
    Write-Host "[host-gs] not registered with $MasterHost after 90 s: $($pending -join ', '). Read the logs in $logRoot." -ForegroundColor Red
    exit 1
}
Write-Host "[host-gs] both maps registered with ${MasterHost}:$MasterPort, advertising $PublicIp."
