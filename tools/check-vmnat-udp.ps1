#!/usr/bin/env pwsh
# Checks that VMware NAT still forwards the game servers' UDP ports into the VM, and restarts the
# NAT service when it does not.
#
# WHY THIS EXISTS. The game servers run in the VMware VM (192.168.94.130) behind VMware NAT, which
# forwards UDP 27015/27016 from the host's public address into it (C:\ProgramData\VMware\
# vmnetnat.conf, [incomingudp]). Twice -- 2026-09-17 and 2026-09-27 -- vmnat stopped forwarding
# while the service still said Running and still listened on both ports. Nothing else notices:
# the master link is outbound TCP, so both game servers stay registered, logins and room joins
# succeed, and every player then fails at the UDP handshake with
#
#     The game server refused the connection (Timeout).
#
# A port check cannot see it, because vmnat is the thing listening. The one measurement that
# can: send datagrams and count what the VM's kernel received. Through the public address a
# stalled vmnat delivers +0; straight to the VM the same probe delivers ~+200.
#
# WHAT IT DOES. Probes each port through -PublicIp, and once straight to the VM as the control
# (a stalled VM or a dead ssh would otherwise read as a stalled NAT). With -Restart, a stall
# restarts "VMware NAT Service" elevated -- Windows shows a UAC prompt -- and probes again. The
# game servers lose their outbound master link on a restart and re-register by themselves within
# about 30 s. Emits one JSON object so the caller reasons over facts.
#
# Usage:  pwsh tools/check-vmnat-udp.ps1 [-Restart] [-PublicIp 26.18.240.157] [-Ports 27015,27016]

[CmdletBinding()]
param(
    [string] $PublicIp = "26.18.240.157",
    [string] $VmHost   = "nghaiz@192.168.94.130",
    [string] $VmIp     = "192.168.94.130",
    [int[]]  $Ports    = @(27015, 27016),
    # A port a game server LISTENS on in the VM. A datagram to a closed port counts as NoPorts,
    # not InDatagrams, so the control must hit a bound socket whatever -Ports names.
    [int]    $ControlPort = 27015,
    [int]    $Probes   = 1000,
    [switch] $Restart
)

$ErrorActionPreference = "Stop"

# A probe counts as delivered at this fraction of what was sent. Loopback-through-NAT loses a
# few datagrams even when healthy (196 of 200 on 2026-09-17), and a stall delivers exactly 0.
$deliveredFraction = 0.5

function Get-VmUdpInDatagrams {
    $line = & ssh -o BatchMode=yes -o ConnectTimeout=10 $VmHost "awk '/^Udp: [0-9]/{print `$2}' /proc/net/snmp"
    if ($LASTEXITCODE -ne 0 -or -not $line) { throw "could not read /proc/net/snmp on $VmHost over ssh." }
    return [long]$line.Trim()
}

function Measure-Delivery([string] $Address, [int] $Port) {
    # The counter is the VM's WHOLE UDP intake, so a match in progress adds its own datagrams.
    # The first version of this script read that traffic as delivery: probing 27099, a port vmnat
    # does not forward at all, reported 194 of 200 "delivered" while two clients were playing.
    # So measure the background over an equal quiet window first and subtract it, and send
    # enough probes (-Probes, default 1000) that they dwarf what a live match adds in two seconds.
    $quietStart = Get-VmUdpInDatagrams
    Start-Sleep -Seconds 2
    $probeStart = Get-VmUdpInDatagrams
    $background = $probeStart - $quietStart

    $udp = [System.Net.Sockets.UdpClient]::new()
    try {
        $payload = [System.Text.Encoding]::ASCII.GetBytes("vmnat-probe")
        # Paced, not one burst: a thousand back-to-back datagrams overflow vmnat's own queue and
        # read as a partial stall on a healthy port (27016 measured -54 of 1000 that way).
        for ($i = 0; $i -lt $Probes; $i++) {
            [void]$udp.Send($payload, $payload.Length, $Address, $Port)
            if ($i % 50 -eq 49) { Start-Sleep -Milliseconds 20 }
        }
    }
    finally { $udp.Dispose() }
    Start-Sleep -Seconds 2

    return ((Get-VmUdpInDatagrams) - $probeStart) - $background
}

function Test-Forwarding {
    $rows = foreach ($port in $Ports) {
        $delivered = Measure-Delivery $PublicIp $port
        [pscustomobject]@{ port = $port; delivered = $delivered; ok = ($delivered -ge $Probes * $deliveredFraction) }
    }
    return @($rows)
}

$control = Measure-Delivery $VmIp $ControlPort
if ($control -lt $Probes * $deliveredFraction) {
    [pscustomobject]@{ verdict = "vm-unreachable"; control = $control; ports = @() } | ConvertTo-Json -Compress
    Write-Host "The VM itself received $control of $Probes direct datagrams -- this is not a NAT stall." -ForegroundColor Red
    exit 2
}

$before = Test-Forwarding
$stalled = @($before | Where-Object { -not $_.ok }).Count -gt 0
$result = [ordered]@{ verdict = $(if ($stalled) { "stalled" } else { "forwarding" }); control = $control; ports = $before }

if ($stalled -and $Restart) {
    $cmd = "Restart-Service -Name 'VMware NAT Service' -Force"
    $proc = Start-Process pwsh -Verb RunAs -ArgumentList @("-NoProfile", "-Command", $cmd) -PassThru -WindowStyle Hidden
    $proc.WaitForExit(120000) | Out-Null
    Start-Sleep -Seconds 5
    $after = Test-Forwarding
    $result.restarted = $true
    $result.portsAfterRestart = $after
    $result.verdict = $(if (@($after | Where-Object { -not $_.ok }).Count -eq 0) { "fixed" } else { "still-stalled" })
}

$result | ConvertTo-Json -Compress -Depth 4
switch ($result.verdict) {
    "forwarding" { exit 0 }
    "fixed"      { exit 0 }
    "stalled"    { Write-Host "vmnat is not forwarding UDP. Re-run with -Restart (UAC prompt)." -ForegroundColor Yellow; exit 1 }
    default      { exit 1 }
}
