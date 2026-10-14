# tools/deploy-master-fly.ps1 -- moves the live master (fly.io app kien-master-2026) onto a master
# image from GHCR, through the Machines API: no flyctl needed.
#
#   pwsh tools/deploy-master-fly.ps1                         # the newest image tagged 'develop'
#   pwsh tools/deploy-master-fly.ps1 -Tag main
#   pwsh tools/deploy-master-fly.ps1 -Image ghcr.io/nghaiz/ironfront-master@sha256:...   # pin, or roll back
#   pwsh tools/deploy-master-fly.ps1 -DryRun                 # resolve and back up, change nothing
#   pwsh tools/deploy-master-fly.ps1 -Force                  # deploy even with rooms open
#
# What it does, in order: resolves the image to a digest (a tag moves; a digest is what was
# tested), saves the machine's current config as the rollback, reads what the master is hosting
# right now, swaps config.image AND applies infra/fly/master.toml's [env], posts the update, waits
# for the machine to be started, and reads back the image the machine actually runs with its
# revision label. Rolling back is this script with -Image set to the digest the backup names.
#
# THE ENV COMES FROM master.toml. This script used to swap the image only, so the machine kept
# whatever env it was last given by `fly deploy` and master.toml's limits never reached it: on
# 2026-10-14 raising the per-IP cap meant a separate flyctl deploy, mid-match. Every IRONFRONT_*
# variable is now set from the toml (and one the toml no longer has is removed); fly's own keys
# are left alone. The differences are printed, and a deploy whose image is current still runs when
# the env differs. Secrets stay in `fly secrets`, never in the toml.
#
# IT REFUSES WHILE ANY ROOM IS OPEN. A restart forgets every room and drops every player to the
# login screen, while the game servers play on. On 2026-10-14 the master was redeployed at 14:22
# (+07) with Island and Forest Lake both mid-round, and Island's round was lost to the careers of
# the six players who finished it. The check reads the newest row of /data/durability.csv (one a
# minute, so up to a minute old): any room open, waiting or in a match, refuses; players connected
# with no room only warn. -Force deploys anyway, for a fix that cannot wait.
#
# FLY_API_TOKEN comes from the repo's gitignored .env. It is a 'FlyV1 fm2_...' macaroon sent as
# the WHOLE Authorization value, never 'Bearer ...', and it is never printed: every HTTP error is
# reported by status code alone, because PowerShell's header-format errors echo the header.
[CmdletBinding()]
param(
    [string]$Image,
    [string]$Tag = 'develop',
    [string]$App = 'kien-master-2026',
    [string]$Repository = 'ghcr.io/nghaiz/ironfront-master',
    [string]$BackupDir = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'LTM-backups'),
    [switch]$DryRun,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Read-FlyToken {
    $line = Get-Content (Join-Path $repoRoot '.env') | Where-Object { $_ -match '^FLY_API_TOKEN=' } | Select-Object -First 1
    if (-not $line) { throw 'FLY_API_TOKEN is not in .env.' }
    return ($line -replace '^FLY_API_TOKEN=', '').Trim()
}

function Invoke-Fly([string]$Method, [string]$Path, $Body = $null) {
    $uri = "https://api.machines.dev/v1/apps/$App$Path"
    try {
        $request = @{ Method = $Method; Uri = $uri; Headers = @{ Authorization = $script:token }; SkipHeaderValidation = $true }
        if ($null -ne $Body) { $request.Body = ($Body | ConvertTo-Json -Depth 40); $request.ContentType = 'application/json' }
        return Invoke-RestMethod @request
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        throw "Machines API $Method $Path failed with HTTP $status."
    }
}

# The [env] table of infra/fly/master.toml: KEY = "value" lines up to the next table. Values are
# plain strings there by convention; anything else is refused rather than guessed at.
function Read-TomlEnv([string]$path) {
    $envTable = [ordered]@{}
    $inEnv = $false
    foreach ($raw in Get-Content $path) {
        $line = $raw.Trim()
        if ($line -match '^\[') { $inEnv = ($line -eq '[env]'); continue }
        if (-not $inEnv -or $line -eq '' -or $line.StartsWith('#')) { continue }
        if ($line -notmatch '^([A-Z0-9_]+)\s*=\s*"([^"]*)"\s*(#.*)?$') { throw "master.toml [env]: cannot read '$line'." }
        $envTable[$Matches[1]] = $Matches[2]
    }
    if ($envTable.Count -eq 0) { throw "master.toml has no [env] table." }
    return $envTable
}

function Resolve-Digest([string]$tag) {
    $owner = ($Repository -split '/')[1]
    $package = ($Repository -split '/')[2]
    $rows = gh api "users/$owner/packages/container/$package/versions?per_page=50" --jq '.[] | {name, created_at, tags: .metadata.container.tags}' |
        ForEach-Object { $_ | ConvertFrom-Json }
    $match = $rows | Where-Object { $_.tags -contains $tag } | Sort-Object created_at -Descending | Select-Object -First 1
    if (-not $match) { throw "No $Repository version carries the tag '$tag'." }
    Write-Host "tag '$tag' -> $($match.name) (pushed $($match.created_at), tags: $($match.tags -join ', '))"
    return "$Repository@$($match.name)"
}

$script:token = Read-FlyToken
if (-not $Image) { $Image = Resolve-Digest $Tag }
if ($Image -notmatch '@sha256:[0-9a-f]{64}$') { throw "Deploy a digest, not a tag: $Image" }

$machine = @(Invoke-Fly GET '/machines')[0]
if (-not $machine) { throw "App $App has no machine." }
$before = $machine.config.image
Write-Host "machine $($machine.id) ($($machine.state)) runs $before"

New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
$backup = Join-Path $BackupDir ("master-config-before-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
$machine.config | ConvertTo-Json -Depth 40 | Set-Content -Encoding utf8 $backup
Write-Host "rollback config saved: $backup"

# The env the machine should run: its own, with every IRONFRONT_* variable taken from the toml.
$wanted = Read-TomlEnv (Join-Path $repoRoot 'infra/fly/master.toml')
$current = [ordered]@{}
foreach ($p in $machine.config.env.PSObject.Properties) { $current[$p.Name] = [string]$p.Value }
$nextEnv = [ordered]@{}
foreach ($k in $current.Keys) { if (-not $k.StartsWith('IRONFRONT_')) { $nextEnv[$k] = $current[$k] } }
foreach ($k in $wanted.Keys) { $nextEnv[$k] = $wanted[$k] }
$envChanges = @()
foreach ($k in $wanted.Keys) {
    if (-not $current.Contains($k)) { $envChanges += "  + $k=$($wanted[$k])" }
    elseif ($current[$k] -ne $wanted[$k]) { $envChanges += "  ~ $k=$($current[$k]) -> $($wanted[$k])" }
}
foreach ($k in $current.Keys) {
    if ($k.StartsWith('IRONFRONT_') -and -not $wanted.Contains($k)) { $envChanges += "  - $k (not in master.toml)" }
}
if ($envChanges.Count -gt 0) { Write-Host "env from master.toml:"; $envChanges | ForEach-Object { Write-Host $_ } }
else { Write-Host 'env already matches master.toml.' }

if ($before -eq $Image -and $envChanges.Count -eq 0) { Write-Host 'already on that image and env; nothing to do.'; exit 0 }

# What the master is hosting, from the newest durability row: the header and the last line, read
# in the machine (the image has busybox sh, head and tail; no curl for the metrics port).
$exec = Invoke-Fly POST "/machines/$($machine.id)/exec" @{ cmd = 'sh -c "head -1 /data/durability.csv; tail -1 /data/durability.csv"'; timeout = 20 }
$rows = @(($exec.stdout -split "`n") | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($rows.Count -lt 2) { throw 'Could not read /data/durability.csv on the machine. Pass -Force to deploy without knowing what it hosts.' }
$columns = $rows[0].Split(','); $values = $rows[-1].Split(',')
$now = @{}; for ($i = 0; $i -lt $columns.Count; $i++) { $now[$columns[$i]] = $values[$i] }
$roomsOpen = [int]$now.roomsActive; $roomsInMatch = [int]$now.roomsInMatch
$clients = [int]$now.connCurrent - [int]$now.gsRegistered
Write-Host ("master at $($now.tsUtc): $roomsOpen room(s) open, $roomsInMatch in a match, " +
    "$clients client connection(s), $($now.gsRegistered) game server(s)")
if ($roomsOpen -gt 0) {
    $why = "$roomsOpen room(s) are open ($roomsInMatch in a match): a restart forgets them and drops their players."
    if (-not $Force) { throw "Refusing to restart the master: $why Wait for the rooms to close, or pass -Force." }
    Write-Warning "-Force: $why"
} elseif ($clients -gt 0) {
    Write-Warning "$clients client connection(s) with no room: they go back to the login screen."
}

if ($DryRun) { Write-Host "dry run: would move to $Image"; exit 0 }

$config = $machine.config
$config.image = $Image
$config.env = $nextEnv
Invoke-Fly POST "/machines/$($machine.id)" @{ config = $config } | Out-Null
Invoke-Fly GET "/machines/$($machine.id)/wait?state=started&timeout=60" | Out-Null

$after = Invoke-Fly GET "/machines/$($machine.id)"
$revision = $after.image_ref.labels.'org.opencontainers.image.revision'
Write-Host "now running $($after.config.image) state=$($after.state) revision=$revision"
if ($after.config.image -ne $Image -or $after.state -ne 'started') { throw 'The machine is not on the new image and started.' }
foreach ($k in $wanted.Keys) {
    if ([string]$after.config.env.$k -ne $wanted[$k]) { throw "The machine's $k is '$($after.config.env.$k)', not '$($wanted[$k])'." }
}
Write-Host "env read back: $($wanted.Count) IRONFRONT_* variable(s) as master.toml says."
