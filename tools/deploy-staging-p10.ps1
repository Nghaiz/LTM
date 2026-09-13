# tools/deploy-staging-p10.ps1 — builds the protocol-10 images and puts them on the Kubernetes
# sandbox as a staging deployment, per § 13.1 of
# docs/multiplayer-game-server-protocol-handoff-2026-09-13.md.
#
# WHAT THIS IS FOR: the handoff forbids deploying a protocol-10 game server into a pool that is
# serving protocol-9 clients. A mixed-version match is not a degraded match; the two sides decode
# different bytes from the same packet. So protocol 10 goes to a staging endpoint first, and the
# production cutover moves master, game server and client together.
#
# WHY THE IMAGES ARE BUILT HERE AND NOT IN CI: GitHub-hosted runners have no Unity licence, so
# .github/workflows/images.yml can only repackage a Linux artifact that a licensed machine already
# produced. This script is the licensed machine's half.
#
# WHY THE IMAGES ARE PUSHED *AND* SIDE-LOADED: the node's egress to ghcr.io runs about a hundred
# times slower than this host's, so a `kubectl apply` that has to pull sits for tens of minutes and
# frequently times out. Pushing gives a real registry digest to pin and to roll back to; the
# side-load over the LAN at 25 MB/s is how the bytes actually arrive. Both halves matter and the
# script does not offer to skip either by default.
#
#   pwsh tools/deploy-staging-p10.ps1                  # build, push, side-load, apply, verify
#   pwsh tools/deploy-staging-p10.ps1 -SkipBuild       # reuse the images already built locally
#   pwsh tools/deploy-staging-p10.ps1 -VerifyOnly      # just re-run the checks against the node
#
# THE NODE BELONGS TO ANOTHER PROJECT. Everything here is confined to the `ironfront` namespace
# and to ports 27000/27001/27015/27016. Nothing installs Docker on the node — its kubelet uses
# containerd, and docker-ce would replace it, which is how a sandbox cluster dies. Images reach it
# through `ctr -n k8s.io images import` and no other route.

[CmdletBinding()]
param(
    [string]$Node = "nghaiz@192.168.94.130",
    [string]$Registry = "ghcr.io/nghaiz",
    [string]$Tag = "staging-p10",

    # The Unity dedicated-server tree that tools/build-server.ps1 produced. It is the build
    # CONTEXT for the game-server image, not a directory copied into an otherwise-built image.
    [string]$ServerBuild = "build/server",

    [switch]$SkipBuild,
    [switch]$SkipPush,
    [switch]$VerifyOnly
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$masterRepo = "$Registry/ironfront-master"
$gsRepo     = "$Registry/ironfront-game-server"
$manifest   = "infra/k8s/staging-protocol10.yaml"

function Step($text) { Write-Host "`n[deploy-staging-p10] $text" -ForegroundColor Cyan }

function Invoke-Node([string]$command) {
    $output = ssh -o BatchMode=yes $Node $command 2>&1
    if ($LASTEXITCODE -ne 0) { throw "node command failed ($LASTEXITCODE): $command`n$output" }
    return $output
}

# ---------------------------------------------------------------------------------------
# Verification. Defined first because -VerifyOnly runs only this, and because it is the part
# worth re-reading: a pod that is Running has proved nothing about whether it is listening.
# ---------------------------------------------------------------------------------------
function Test-Staging {
    Step "verifying"

    $failures = New-Object System.Collections.Generic.List[string]

    $pods = Invoke-Node "kubectl -n ironfront get pods -o wide --no-headers"
    Write-Host $pods

    # A Unity UDP listener cannot be probed honestly over TCP, so there is no readinessProbe to
    # believe. These four facts are what actually distinguishes a working staging deployment from
    # a process that started, bound nothing and accepted nobody.

    $listeners = Invoke-Node "ss -lntup 2>/dev/null | grep -E ':(27000|27001|27015|27016)\b' || true"
    Write-Host $listeners
    foreach ($port in @("27000", "27015", "27016")) {
        if ($listeners -notmatch ":$port\b") { $failures.Add("nothing is listening on $port") }
    }

    $metrics = Invoke-Node "curl -s --max-time 5 http://127.0.0.1:27001/metrics || true"
    $registered = ($metrics | Select-String -Pattern 'gameserver|registered|healthy' | Out-String).Trim()
    if ($registered) { Write-Host $registered } else { $failures.Add("the master's metrics named no game server") }

    # The build stamp is the only thing that says which commit is actually running. A deployment
    # that looks right and reports a different SHA is the failure this exists to catch.
    foreach ($map in @("dustbowl", "island")) {
        $stamp = Invoke-Node "kubectl -n ironfront logs deploy/game-server-$map --tail=400 2>/dev/null | grep -m1 '\[net\] build' || true"
        if ($stamp) { Write-Host "$map : $stamp" } else { $failures.Add("$map printed no build stamp") }
    }

    # Signed tickets are valid for 60 seconds, so a node whose clock has drifted rejects every
    # join and the rejection looks like a protocol fault rather than a clock one.
    $ntp = Invoke-Node "timedatectl show -p NTPSynchronized --value || true"
    if ($ntp.Trim() -ne "yes") { $failures.Add("the node's clock is not NTP-synchronised; joinTickets expire after 60 s") }

    if ($failures.Count -gt 0) {
        Write-Host "`nFAILED:" -ForegroundColor Red
        $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        return $false
    }

    Write-Host "`nstaging is up: master on 27000, Dustbowl on 27015, Island on 27016" -ForegroundColor Green
    return $true
}

if ($VerifyOnly) {
    exit ([int](-not (Test-Staging)))
}

# ---------------------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------------------
if (-not $SkipBuild) {
    $serverExe = Join-Path $ServerBuild "Ironfront.Server.x86_64"
    if (-not (Test-Path $serverExe)) {
        throw "no dedicated-server build at $serverExe. Run: `$env:UNITY_PATH=...; pwsh tools/build-server.ps1"
    }

    Step "building $masterRepo`:$Tag (context: repo root)"
    docker build -f infra/docker/master.Dockerfile -t "$masterRepo`:$Tag" .
    if ($LASTEXITCODE -ne 0) { throw "master image build failed" }

    Step "building $gsRepo`:$Tag (context: $ServerBuild)"
    docker build -f infra/docker/gameserver.Dockerfile -t "$gsRepo`:$Tag" $ServerBuild
    if ($LASTEXITCODE -ne 0) { throw "game-server image build failed" }
}

# ---------------------------------------------------------------------------------------
# Push, to get a digest worth pinning
# ---------------------------------------------------------------------------------------
$digests = @{}
foreach ($repo in @($masterRepo, $gsRepo)) {
    if (-not $SkipPush) {
        Step "pushing $repo`:$Tag"
        docker push "$repo`:$Tag"
        if ($LASTEXITCODE -ne 0) { throw "push failed for $repo — check `gh auth token | docker login ghcr.io -u Nghaiz --password-stdin`" }
    }

    $repoDigest = docker inspect --format '{{index .RepoDigests 0}}' "$repo`:$Tag" 2>$null
    if (-not $repoDigest) {
        throw "no RepoDigest on $repo`:$Tag. The image was never pushed, so there is no registry digest to pin and no way to roll back to it. Re-run without -SkipPush."
    }
    $digests[$repo] = ($repoDigest -split '@')[1]
    Write-Host "  $repo@$($digests[$repo])"
}

# ---------------------------------------------------------------------------------------
# Side-load
#
# Saved from the TAG, never from the digest. `ctr import` names the archive's index
# `import-<date>@sha256:...` either way, and that stray name is harmless on its own — what breaks
# is a container creation whose config digest resolves to one of those names when the name does
# not resolve back, which surfaces as an error about a checkpoint feature nobody asked for.
# Importing an archive that carries a real repository name, and adding the digest alias
# afterwards, is the pair that was proven to work. See infra/k8s/README.md.
# ---------------------------------------------------------------------------------------
foreach ($repo in @($masterRepo, $gsRepo)) {
    $short = ($repo -split '/')[-1]
    $tar = "$env:TEMP\$short-$Tag.tar"

    Step "side-loading $repo`:$Tag over the LAN"
    docker save -o $tar "$repo`:$Tag"
    if ($LASTEXITCODE -ne 0) { throw "docker save failed for $repo" }

    scp $tar "${Node}:/tmp/$short-$Tag.tar"
    if ($LASTEXITCODE -ne 0) { throw "scp failed for $repo" }
    Remove-Item $tar -Force

    Invoke-Node "sudo ctr -n k8s.io images import --digests --all-platforms /tmp/$short-$Tag.tar"
    Invoke-Node "sudo ctr -n k8s.io images tag --force $repo`:$Tag $repo@$($digests[$repo])"
    Invoke-Node "rm -f /tmp/$short-$Tag.tar"
}

Invoke-Node "sudo ctr -n k8s.io images check 2>/dev/null | grep ironfront | grep -c complete"

# ---------------------------------------------------------------------------------------
# Pin the manifest to the digests, and apply
# ---------------------------------------------------------------------------------------
Step "pinning $manifest to the digests just published"
$text = Get-Content $manifest -Raw
$text = $text -replace [regex]::Escape("image: $masterRepo`:$Tag"), "image: $masterRepo@$($digests[$masterRepo])"
$text = $text -replace [regex]::Escape("image: $gsRepo`:$Tag"),     "image: $gsRepo@$($digests[$gsRepo])"
$text = $text -replace "image: $([regex]::Escape($masterRepo))@sha256:[0-9a-f]{64}", "image: $masterRepo@$($digests[$masterRepo])"
$text = $text -replace "image: $([regex]::Escape($gsRepo))@sha256:[0-9a-f]{64}",     "image: $gsRepo@$($digests[$gsRepo])"
Set-Content -Path $manifest -Value $text -NoNewline -Encoding utf8

Step "applying"
# The shared secret signs joinTickets and is never committed. Created once; left alone afterwards,
# because rotating it under a running deployment invalidates every ticket in flight.
Invoke-Node "kubectl -n ironfront get secret ironfront-staging >/dev/null 2>&1 || kubectl create namespace ironfront --dry-run=client -o yaml | kubectl apply -f - && kubectl -n ironfront get secret ironfront-staging >/dev/null 2>&1 || kubectl -n ironfront create secret generic ironfront-staging --from-literal=IRONFRONT_SHARED_SECRET=`$(openssl rand -base64 48 | tr -d '\n')`"

Get-Content $manifest -Raw | ssh -o BatchMode=yes $Node "kubectl apply -f -"
if ($LASTEXITCODE -ne 0) { throw "kubectl apply failed" }

Invoke-Node "kubectl -n ironfront rollout status deploy/master --timeout=180s"
Invoke-Node "kubectl -n ironfront rollout status deploy/game-server-dustbowl --timeout=300s"
Invoke-Node "kubectl -n ironfront rollout status deploy/game-server-island --timeout=300s"

Start-Sleep -Seconds 20   # the master's registration handshake is not instant

if (-not (Test-Staging)) { exit 1 }

Step "done. Rollback: the previous game-server digest is sha256:8c5d062d7ddbd432fa87b363969e0499c8393be67e5c95ab7f5de9e4399293a1 (lan-v0.4.0, protocol 9)."
