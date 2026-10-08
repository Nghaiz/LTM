# tools/deploy-master-fly.ps1 -- moves the live master (fly.io app kien-master-2026) onto a master
# image from GHCR, through the Machines API: no flyctl needed.
#
#   pwsh tools/deploy-master-fly.ps1                         # the newest image tagged 'develop'
#   pwsh tools/deploy-master-fly.ps1 -Tag main
#   pwsh tools/deploy-master-fly.ps1 -Image ghcr.io/nghaiz/ironfront-master@sha256:...   # pin, or roll back
#   pwsh tools/deploy-master-fly.ps1 -DryRun                 # resolve and back up, change nothing
#
# What it does, in order: resolves the image to a digest (a tag moves; a digest is what was
# tested), saves the machine's current config as the rollback, swaps config.image, posts the
# update, waits for the machine to be started, and reads back the image the machine actually runs
# with its revision label. Rolling back is this script with -Image set to the digest the backup
# names.
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
    [switch]$DryRun
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

if ($before -eq $Image) { Write-Host 'already on that image; nothing to do.'; exit 0 }
if ($DryRun) { Write-Host "dry run: would move to $Image"; exit 0 }

$config = $machine.config
$config.image = $Image
Invoke-Fly POST "/machines/$($machine.id)" @{ config = $config } | Out-Null
Invoke-Fly GET "/machines/$($machine.id)/wait?state=started&timeout=60" | Out-Null

$after = Invoke-Fly GET "/machines/$($machine.id)"
$revision = $after.image_ref.labels.'org.opencontainers.image.revision'
Write-Host "now running $($after.config.image) state=$($after.state) revision=$revision"
if ($after.config.image -ne $Image -or $after.state -ne 'started') { throw 'The machine is not on the new image and started.' }
