# tools/play-lan.ps1 -- launch ONE human-playable client against a master + game server that are
# already running somewhere else.
#
# WHICH SCRIPT DO I WANT?
#
#   everything on this machine     -> tools/playtest-local.ps1   (starts the master, the game
#                                     server AND N clients; this is the usual answer)
#   server elsewhere, client here  -> this script
#   scripted clients, no human     -> tools/run-lane-b.ps1
#
# WHAT CHANGED IN P15, AND WHY THIS FILE WAS WRONG UNTIL 2026-09-03. This script used to say
# "in the menu, pick Dustbowl -- the scene's NetClient has connectOnStart set, so it dials as the
# map loads", and it set IRONFRONT_CLIENT_HOST to steer that dial. Both belong to the menu that
# no longer exists. Since P15/P16 the player logs in, browses rooms, picks a side and readies up;
# the master then hands the client a signed ticket, ClientFlowBootstrap dials the game server
# with it and OFFERS THE OPEN SOCKET FORWARD, and NetClientBootstrap.Connect adopts that socket
# via MatchTransportHandoff.TryTake rather than dialling anything of its own. So the address that
# matters is now the MASTER's, and IRONFRONT_CLIENT_HOST only takes effect on a path a human can
# no longer reach -- it is still set below, as a documented fallback, and it is no longer the
# thing that gets you into a match.
#
# THE ROLE VARIABLE IS NOT OPTIONAL, and it buys two different things. Every map scene carries an
# active NetServer AND an active NetClient, so a rendered process that declares no role becomes
# whichever of the two Awakes first. When the server wins, NetClientPresenterGuard.IsPresentable
# is false for the whole session: no killfeed, no name table, no local combat driver (ledger
# X-10). It also stops this process HOSTING: until X-52 the declaration only made
# NetServerBootstrap decline to CLAIM the role, never to start, so the first run of this script
# produced two clients that each logged `[net] role = Client` and then ran a sixteen-slot
# authority anyway. The log line to look for is
# `[net] declared client: no local server will be started (AD-1).`
#
# EACH PLAYER NEEDS THEIR OWN ID AND THEIR OWN ACCOUNT. The server enforces one session per
# player id, so a second client reusing the first's is rejected with a bare InvalidTicket --
# which reads as a full server and is not one.
#
# Usage:
#   pwsh tools/play-lan.ps1                              # player 1, master on the sandbox node
#   pwsh tools/play-lan.ps1 -PlayerId 2 -Name P2         # player 2, on this or another machine
#   pwsh tools/play-lan.ps1 -MasterHost 10.0.0.5         # a master somewhere else

[CmdletBinding()]
param(
    # The MASTER server -- accounts, room browser, tickets. This is the address the menu talks
    # to, and the one that decides whether you can get into a match at all.
    #
    # Defaults to the fly.io master, which is the one that is actually up and is reachable from
    # anywhere. It used to default to the sandbox k8s node, an address on a VMware NAT subnet
    # that only the machine hosting that VM can route to -- so the default was useful to exactly
    # one person and silently useless to everyone else.
    [string] $MasterHost = "kien-master-2026.fly.dev",

    # 443, not 27000, and NOT because this is HTTP. A fly app is IPv6-only by default; the free
    # shared IPv4 that makes it reachable from an IPv4-only machine routes ports 80 and 443
    # only, picking the app by TLS SNI. infra/fly/master.toml therefore exposes the same MSP
    # listener on both, and 443 is the one an IPv4 client can use. 27000 still works over IPv6.
    [int] $MasterPort = 443,

    # TLS is ON by default because the default master terminates TLS at fly's edge and a
    # plaintext dial fails the handshake before a login screen can appear. Pass this when
    # pointing at a plaintext master -- a local one, or a LAN box.
    [switch] $NoMasterTls,

    # The GAME server, for the direct-dial fallback only: NetClientBootstrap uses these when no
    # socket was handed to it, which today means a map scene entered outside the shipped flow.
    # In a normal session the room join supplies the address and these are ignored. Empty means
    # "same host as the master".
    [string] $ServerHost = "",

    [int] $Port = 27015,

    # Distinct per client. Two clients sharing this are not both allowed in.
    [int] $PlayerId = 1,

    [string] $Name = "",

    # Built by tools/build-player.ps1. There is no separate shipping client target yet; this is
    # the same binary as the game server, launched without a harness role so LaneBHarness stays
    # inert.
    [string] $PlayerPath = "build/windows/Ironfront.exe",

    # Where the client writes its log. Read this first when a join does not happen.
    [string] $LogFile = ""
)

$ErrorActionPreference = "Stop"

$exe = Resolve-Path -LiteralPath $PlayerPath -ErrorAction SilentlyContinue
if (-not $exe) {
    Write-Error ("no player at '$PlayerPath'. Build one first:`n" +
                 "  `$env:UNITY_PATH = '<Unity.exe>'`n" +
                 "  pwsh tools/build-player.ps1")
    exit 1
}

if (-not $Name)       { $Name = "PLAYER-$PlayerId" }
if (-not $ServerHost) { $ServerHost = $MasterHost }
if (-not $LogFile)    { $LogFile = Join-Path $PWD "tmp/client-$PlayerId.log" }

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogFile) | Out-Null

# Cleared rather than assumed absent: a shell that has run run-lane-b.ps1 still carries these,
# and a stale IRONFRONT_LANEB_ROLE would hand this process to the harness instead of the player.
foreach ($stale in @("IRONFRONT_LANEB_ROLE", "IRONFRONT_LANEB_LABEL", "IRONFRONT_LANEB_SCENE",
                     "IRONFRONT_LANEB_PROGRAMME", "IRONFRONT_LANEB_OUTPUT")) {
    Remove-Item ("Env:" + $stale) -ErrorAction SilentlyContinue
}

$env:IRONFRONT_ROLE                = "client"
$env:IRONFRONT_CLIENT_MASTER_HOST  = $MasterHost
$env:IRONFRONT_CLIENT_MASTER_PORT  = "$MasterPort"
$masterTlsFlag = if ($NoMasterTls) { "0" } else { "1" }
$env:IRONFRONT_CLIENT_MASTER_TLS   = $masterTlsFlag

# The SNI, and on fly's shared IPv4 it is what PICKS THE APP rather than a nicety -- the edge
# has no other way to tell which app a raw TLS connection on 443 belongs to. Empty would default
# to the host, which is already this value; it is set explicitly so the two cannot drift apart.
$env:IRONFRONT_CLIENT_MASTER_TLS_TARGET_HOST = $MasterHost
$env:IRONFRONT_CLIENT_HOST         = $ServerHost
$env:IRONFRONT_CLIENT_PORT         = "$Port"
$env:IRONFRONT_CLIENT_PLAYER_ID    = "$PlayerId"
$env:IRONFRONT_CLIENT_DISPLAY_NAME = $Name

$masterScheme = if ($NoMasterTls) { "plaintext" } else { "TLS" }
Write-Host "[play] $Name (id $PlayerId) -> master ${MasterHost}:${MasterPort} ($masterScheme)"
Write-Host "[play] log: $LogFile"
Write-Host "[play] in the client: register or log in, open the room browser, pick a side, ready up."
Write-Host "[play] the match starts when everyone in the room is ready; the map loads itself."
Write-Host "[play] grep the log for '[net] connected as' once you are in."

Start-Process -FilePath $exe -ArgumentList @("-logFile", $LogFile) | Out-Null
