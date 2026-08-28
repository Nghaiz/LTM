# tools/play-lan.ps1 -- launch one human-playable client against the LAN game server.
#
# WHAT THIS IS FOR. run-lane-b.ps1 launches SCRIPTED clients: it sets IRONFRONT_LANEB_ROLE and
# lets LaneBHarness load the map and play a recorded programme. That harness is diagnostics-only
# scaffolding. This script is the other thing -- a client a person drives with a keyboard.
#
# THE ROLE VARIABLE IS NOT OPTIONAL. Every map scene carries an active NetServer AND an active
# NetClient, so a rendered process that declares no role becomes whichever of the two Awakes
# first. The build says so itself on startup, and when the server wins,
# NetClientPresenterGuard.IsPresentable is false for the whole session: no killfeed, no name
# table, no local combat driver. IRONFRONT_ROLE=client settles it (ledger X-10).
#
# EACH PLAYER NEEDS THEIR OWN ID. The server enforces one session per player id, so a second
# client reusing the first's id is rejected with a bare InvalidTicket -- which reads as a full
# server and is not one. -PlayerId defaults to something distinct per launch for that reason.
#
# Usage:
#   pwsh tools/play-lan.ps1                          # player 1
#   pwsh tools/play-lan.ps1 -PlayerId 2 -Name P2     # player 2, on this or another machine
#   pwsh tools/play-lan.ps1 -Host 10.0.0.5           # a different server

[CmdletBinding()]
param(
    # The game server. Defaults to the sandbox k8s node (infra/k8s/gameserver-lan.yaml).
    [string] $ServerHost = "192.168.94.130",

    [int] $Port = 27015,

    # Distinct per client. Two clients sharing this are not both allowed in.
    [int] $PlayerId = 1,

    [string] $Name = "",

    # The player built by run-lane-b.ps1 -Build. There is no separate shipping client target
    # yet; this is the same binary, launched without the lane-B role so the harness stays inert.
    [string] $PlayerPath = "build/windows/Ironfront.exe",

    # Where the client writes its log. Read this first when a join does not happen.
    [string] $LogFile = ""
)

$ErrorActionPreference = "Stop"

$exe = Resolve-Path -LiteralPath $PlayerPath -ErrorAction SilentlyContinue
if (-not $exe) {
    Write-Error ("no player at '$PlayerPath'. Build one first:`n" +
                 "  `$env:UNITY_PATH = '<Unity.exe>'`n" +
                 "  pwsh tools/run-lane-b.ps1 -Build -Smoke")
    exit 1
}

if (-not $Name) { $Name = "PLAYER-$PlayerId" }
if (-not $LogFile) { $LogFile = Join-Path $PWD "tmp/client-$PlayerId.log" }

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogFile) | Out-Null

# Cleared rather than assumed absent: a shell that has run run-lane-b.ps1 still carries these,
# and a stale IRONFRONT_LANEB_ROLE would hand this process to the harness instead of the player.
foreach ($stale in @("IRONFRONT_LANEB_ROLE", "IRONFRONT_LANEB_LABEL", "IRONFRONT_LANEB_SCENE",
                     "IRONFRONT_LANEB_PROGRAMME", "IRONFRONT_LANEB_OUTPUT")) {
    Remove-Item ("Env:" + $stale) -ErrorAction SilentlyContinue
}

$env:IRONFRONT_ROLE                = "client"
$env:IRONFRONT_CLIENT_HOST         = $ServerHost
$env:IRONFRONT_CLIENT_PORT         = "$Port"
$env:IRONFRONT_CLIENT_PLAYER_ID    = "$PlayerId"
$env:IRONFRONT_CLIENT_DISPLAY_NAME = $Name

Write-Host "[play] $Name (id $PlayerId) -> ${ServerHost}:${Port}"
Write-Host "[play] log: $LogFile"
Write-Host "[play] in the menu, pick Dustbowl -- the scene's NetClient has connectOnStart set,"
Write-Host "[play] so it dials as the map loads. Grep the log for '[net] connected as'."

Start-Process -FilePath $exe -ArgumentList @("-logFile", $LogFile) | Out-Null
