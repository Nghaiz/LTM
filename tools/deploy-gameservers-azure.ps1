#!/usr/bin/env pwsh
# tools/deploy-gameservers-azure.ps1 -- put a game-server image on the Azure VM and run both maps
# there with infra/docker/gameservers.compose.yml, registered with the fly master.
#
# THE HOST (since 2026-09-28). Azure VM "ironfront-game", Standard_B2as_v2 in Southeast Asia
# (Singapore, the same city as the fly master: TCP connect 2-3 ms). Its network security group
# admits UDP 27015/27016 only -- 27115/27116, the Docker Desktop defaults, are dropped at the Azure
# edge -- so this runs the compose file with IRONFRONT_GS_*_PORT set to 27015/27016 and advertises
# the VM's public address. Log in as irondev with the key below; irondev has passwordless sudo.
#
# WHAT IT DOES, in order:
#   1. Ships the image only if the VM lacks that exact image id. `docker save` is piped straight
#      into `docker load` over ssh -C through cmd.exe, whose pipes carry bytes unchanged
#      (~135 MB on the wire, about 20 s from the owner's line).
#   2. Copies the compose file and writes ~/ironfront/.env (mode 600) with the shared secret from
#      this repo's .env -- the secret the fly master signs tickets with -- plus the image, ports
#      and advertised address. The secret is never echoed.
#   3. `docker compose up -d`, then waits until BOTH containers log
#      "[net] master link: registered as server N". A server that binds its port and never
#      registers answers every join NoGameServerAvailable, so a port check would be a false green.
#
# Only one host may serve the fly master at a time: stop the Docker Desktop copies first
# (docker compose --env-file .env -f infra/docker/gameservers.compose.yml down), or the master
# holds two servers per map and a room can land on either.
#
# Usage:
#   pwsh tools/deploy-gameservers-azure.ps1 -Image ironfront-game-server:<rev>
#   pwsh tools/deploy-gameservers-azure.ps1 -Status
#   pwsh tools/deploy-gameservers-azure.ps1 -Stop

[CmdletBinding()]
param(
    [string] $Image = "",
    [switch] $Status,
    [switch] $Stop,
    [string] $VmHost = "20.205.152.66",
    [string] $User = "irondev",
    [string] $KeyPath = (Join-Path $HOME ".ssh/nghaiz_ed25519_ctf"),
    [int] $DustbowlPort = 27015,
    [int] $IslandPort = 27016,
    [int] $RegisterTimeoutSec = 180
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$compose = Join-Path $repo "infra/docker/gameservers.compose.yml"
$sshArgs = @("-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-i", $KeyPath)
$target = "$User@$VmHost"
$remoteCompose = "cd ~/ironfront && sudo docker compose --env-file .env -f gameservers.compose.yml"
$containers = @("ironfront-gs-dustbowl", "ironfront-gs-island")

function Invoke-Remote([string] $command) {
    $output = & ssh @sshArgs $target $command
    if ($LASTEXITCODE -ne 0) { throw "ssh $target failed ($LASTEXITCODE): $command" }
    return $output
}

if ($Status) {
    Invoke-Remote "$remoteCompose ps --format '{{.Name}} {{.Image}} {{.Status}}'; for c in $containers; do echo `$c; sudo docker logs `$c 2>&1 | grep 'master link: registered' | tail -1; sudo docker logs `$c 2>&1 | grep '^\[frames\]' | tail -1; done"
    exit 0
}

if ($Stop) {
    Invoke-Remote "$remoteCompose down"
    exit 0
}

if (-not $Image) {
    Write-Error "pass -Image <repo:tag>, e.g. ironfront-game-server:8d33ed0 (docker images | findstr ironfront)"
    exit 1
}

$localId = (& docker image inspect --format "{{.Id}}" $Image 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $localId) {
    Write-Error ("no local image '$Image'. Build it first:`n" +
                 "  docker build -f infra/docker/gameserver.Dockerfile -t $Image build/server")
    exit 1
}

$secretLine = Get-Content (Join-Path $repo ".env") | Where-Object { $_ -match '^IRONFRONT_SHARED_SECRET=.+' }
if (-not $secretLine) { Write-Error "IRONFRONT_SHARED_SECRET is missing from the repo .env"; exit 1 }

# 1. The image, only when the VM does not already hold this exact id.
$remoteId = (& ssh @sshArgs $target "sudo docker image inspect --format '{{.Id}}' $Image 2>/dev/null")
if ($remoteId -eq $localId) {
    Write-Host "[azure] $Image already on the VM ($localId)"
} else {
    Write-Host "[azure] shipping $Image ($localId) ..."
    $keyForCmd = $KeyPath -replace '/', '\'
    & cmd /c "docker save $Image | ssh -C -o BatchMode=yes -o IdentitiesOnly=yes -i `"$keyForCmd`" $target `"sudo docker load`""
    if ($LASTEXITCODE -ne 0) { throw "image transfer failed ($LASTEXITCODE)" }
    $remoteId = Invoke-Remote "sudo docker image inspect --format '{{.Id}}' $Image"
    if ($remoteId -ne $localId) { throw "the VM loaded $remoteId, expected $localId" }
}

# 2. The compose file and the env file beside it.
Invoke-Remote "mkdir -p ~/ironfront && chmod 700 ~/ironfront" | Out-Null
& scp @sshArgs -q $compose "${target}:ironfront/gameservers.compose.yml"
if ($LASTEXITCODE -ne 0) { throw "scp of the compose file failed ($LASTEXITCODE)" }

$envBody = @(
    $secretLine.Trim(),
    "IRONFRONT_GAMESERVER_IMAGE=$Image",
    "IRONFRONT_GAMESERVER_PUBLIC_IP_ADVERTISED=$VmHost",
    "IRONFRONT_GS_DUSTBOWL_PORT=$DustbowlPort",
    "IRONFRONT_GS_ISLAND_PORT=$IslandPort"
) -join "`n"
$envBody + "`n" | & ssh @sshArgs $target "umask 077; cat > ~/ironfront/.env"
if ($LASTEXITCODE -ne 0) { throw "writing the VM .env failed ($LASTEXITCODE)" }

# 3. Up, then wait for the master's view rather than for the ports. Each container's log is read
#    from the moment its CURRENT run started: an unchanged container that compose leaves running
#    still shows its registration, and a line from before a crash-restart cannot pass for one.
Invoke-Remote "$remoteCompose up -d 2>&1"

$deadline = (Get-Date).AddSeconds($RegisterTimeoutSec)
$registered = @{}
while ((Get-Date) -lt $deadline -and $registered.Count -lt $containers.Count) {
    Start-Sleep -Seconds 5
    foreach ($c in $containers) {
        if ($registered.ContainsKey($c)) { continue }
        $line = Invoke-Remote ("sudo docker logs --since `$(sudo docker inspect -f '{{.State.StartedAt}}' $c) $c 2>&1 " +
                               "| grep 'master link: registered as server' | tail -1 || true")
        if ($line) { $registered[$c] = $line; Write-Host "[azure] $c $line" }
    }
}

if ($registered.Count -lt $containers.Count) {
    $missing = $containers | Where-Object { -not $registered.ContainsKey($_) }
    Write-Error ("not registered with the master after ${RegisterTimeoutSec}s: $($missing -join ', '). " +
                 "Read: ssh $target sudo docker logs <container>")
    exit 1
}

Write-Host "[azure] both maps registered; players dial ${VmHost}:$DustbowlPort (Dustbowl) and ${VmHost}:$IslandPort (Island)."
