#!/usr/bin/env pwsh
# tools/backup-live-stack.ps1 -- copies what the live stack holds onto this PC, so either host can be
# stopped, lost or replaced: the fly master's database, data files, machine config and secrets, the
# Azure VM's compose files, env, logs and inventory, and both server images.
#
# WHAT GIT CANNOT GIVE BACK. Everything else is rebuilt from the repo at the deployed revision:
#   - the accounts, careers, achievements and match history: the master's SQLite database on the fly
#     volume is the only copy;
#   - the fly secret IRONFRONT_SHARED_SECRET. `fly secrets` is write-only, so it is read inside the
#     machine here and compared with the repo .env and the VM .env, because deploy-gameservers-azure.ps1
#     takes the game servers' copy from the repo .env;
#   - the game-server image. Deploys side-load it from this PC's Docker and the VM keeps only the one
#     that runs, so no registry holds it, and rebuilding it takes Unity;
#   - the logs: /data/master.log on the volume, and `docker logs` of each map's container, which a
#     redeploy or `compose down` deletes.
#
# HOW IT READS THE MASTER. No flyctl: the Machines API with the repo .env's FLY_API_TOKEN as the whole
# Authorization value, never printed, and nothing read from the machine is printed either. Files come
# back through `exec` as 1 MiB base64 chunks (1.5 MiB was measured to arrive whole), and each file is
# checked against its sha256 in the machine. The database is copied with the master's own `--backup`
# (SQLite's online backup API): copying ironfront.db while the master writes leaves out what is still
# in ironfront.db-wal, and fails silently.
#
# IMAGES are `docker save`d without --platform. With --platform the tar keeps only the amd64 manifest,
# so `docker load` gives back a different image id than the one that runs; without it the tar holds
# the image's own index, which every blob name in the tar is checked against.
#
# WHERE IT WRITES. ../LTM-backups/live-stack-<yyyyMMdd-HHmm>/ beside the repo (the folder
# deploy-master-fly.ps1 writes its config backups to), never inside it: secrets/ holds the shared
# secret and the fly token, and only this Windows user may read it. Work happens in <dir>.partial,
# renamed when every check has passed, so a folder without that suffix is a complete backup.
#
# Restoring: docs/live-stack-backup.md, copied into the backup as RESTORE.md.
#
# Usage:
#   pwsh tools/backup-live-stack.ps1
#   pwsh tools/backup-live-stack.ps1 -SkipImages                    # data and config only, ~10 MB
#   pwsh tools/backup-live-stack.ps1 -SkipGameServers               # the master alone (the VM is off)
#   pwsh tools/backup-live-stack.ps1 -EnvFile D:\Coding\LTM\.env    # from a git worktree (no .env)

[CmdletBinding()]
param(
    [string] $OutDir = '',
    [string] $EnvFile = '',
    [string] $App = 'kien-master-2026',
    [string] $VmHost = '20.205.152.66',
    [string] $User = 'irondev',
    [string] $KeyPath = (Join-Path $HOME '.ssh/nghaiz_ed25519_ctf'),
    [switch] $SkipImages,
    [switch] $SkipGameServers
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Formats.Tar
$repo = Split-Path -Parent $PSScriptRoot
if (-not $EnvFile) { $EnvFile = Join-Path $repo '.env' }
if (-not $OutDir) {
    $OutDir = Join-Path (Split-Path -Parent $repo) ('LTM-backups/live-stack-' + (Get-Date -Format 'yyyyMMdd-HHmm'))
}
$OutDir = [IO.Path]::GetFullPath($OutDir)
$work = "$OutDir.partial"
if ((Test-Path $OutDir) -or (Test-Path $work)) { throw "$OutDir (or its .partial) already exists; pass another -OutDir." }
$chunkBytes = 1MB
$facts = [ordered]@{}

function Read-EnvValue([string]$name) {
    $line = Get-Content $EnvFile | Where-Object { $_ -match "^$name=" } | Select-Object -First 1
    if (-not $line) { throw "$name is not in $EnvFile." }
    return ($line -replace "^$name=", '').Trim()
}

function Get-FileSha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

# Every call here is a read or is safe to repeat, so a call that got NO response (a dropped
# connection, seen against this app on 2026-10-18) is retried. An HTTP error is not.
function Invoke-FlyRequest([hashtable]$request, [string]$what) {
    for ($attempt = 1; ; $attempt++) {
        try { return Invoke-RestMethod @request }
        catch {
            $status = $_.Exception.Response.StatusCode.value__
            if ($status -or $attempt -ge 3) { throw "$what failed with HTTP $(if ($status) { $status } else { 'none (no response)' })." }
            Write-Warning "$what got no response (attempt $attempt of 3); retrying."
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
}

function Invoke-Fly([string]$Method, [string]$Path) {
    $request = @{ Method = $Method; Uri = "https://api.machines.dev/v1/apps/$App$Path"
                  Headers = @{ Authorization = $script:token }; SkipHeaderValidation = $true; TimeoutSec = 120 }
    return Invoke-FlyRequest $request "Machines API $Method $Path"
}

# Runs argv in the master machine and returns its stdout. -Secret keeps the output out of any error.
function Invoke-FlyExec([string[]]$Argv, [int]$TimeoutSec = 60, [switch]$Secret) {
    $request = @{ Method = 'POST'; Uri = "https://api.machines.dev/v1/apps/$App/machines/$($script:machineId)/exec"
                  Headers = @{ Authorization = $script:token }; SkipHeaderValidation = $true; TimeoutSec = $TimeoutSec + 60
                  Body = (@{ command = $Argv; timeout = $TimeoutSec } | ConvertTo-Json -Depth 5); ContentType = 'application/json' }
    $result = Invoke-FlyRequest $request "exec in the master ($($Argv -join ' '))"
    if ($result.exit_code -ne 0) {
        $detail = if ($Secret) { '' } else { ' -- ' + ("$($result.stdout) $($result.stderr)".Trim() -replace '\s+', ' ') }
        throw "exec in the master exited $($result.exit_code): $($Argv -join ' ')$detail"
    }
    return [string]$result.stdout
}

function Receive-FlyFile([string]$remote, [string]$local) {
    $lines = @((Invoke-FlyExec @('sh', '-c', "stat -c %s '$remote' && sha256sum '$remote'")) -split "`n")
    $size = [long]$lines[0].Trim()
    $want = ($lines[1].Trim() -split '\s+')[0]
    $stream = [IO.File]::Create($local)
    try {
        for ($i = 0L; $i * $chunkBytes -lt $size; $i++) {
            $b64 = Invoke-FlyExec @('sh', '-c', "dd if='$remote' bs=$chunkBytes skip=$i count=1 2>/dev/null | base64 -w0")
            $bytes = [Convert]::FromBase64String($b64.Trim())
            $expected = [Math]::Min([long]$chunkBytes, $size - $i * $chunkBytes)
            if ($bytes.Length -ne $expected) { throw "$remote chunk $i came back with $($bytes.Length) bytes, not $expected." }
            $stream.Write($bytes, 0, $bytes.Length)
        }
    } finally { $stream.Dispose() }
    $got = Get-FileSha256 $local
    if ($got -ne $want) { throw "$remote arrived with sha256 $got; the machine's copy is $want." }
}

function Expand-GzipFile([string]$source, [string]$destination) {
    $in = [IO.File]::OpenRead($source)
    try {
        $gzip = [IO.Compression.GZipStream]::new($in, [IO.Compression.CompressionMode]::Decompress)
        $out = [IO.File]::Create($destination)
        try { $gzip.CopyTo($out) } finally { $out.Dispose(); $gzip.Dispose() }
    } finally { $in.Dispose() }
}

$sshArgs = @('-o', 'BatchMode=yes', '-o', 'IdentitiesOnly=yes', '-o', 'ConnectTimeout=20', '-i', $KeyPath)
$target = "$User@$VmHost"
function Invoke-Vm([string]$command) {
    $output = & ssh @sshArgs $target $command
    if ($LASTEXITCODE -ne 0) { throw "ssh $target failed ($LASTEXITCODE): $command" }
    return $output
}
function Copy-FromVm([string]$remote, [string]$local) {
    & scp @sshArgs -q "${target}:$remote" $local
    if ($LASTEXITCODE -ne 0) { throw "scp ${target}:$remote failed ($LASTEXITCODE)." }
}

# A docker-save tar is content-addressed: every blob is named by its sha256. Checking each name against
# its bytes proves the tar intact, and the image's own id among the blobs proves it is the image that
# runs, not a re-resolved platform copy.
function Assert-SavedImage([string]$file, [string]$imageId, [string]$tag) {
    $wantBlob = 'blobs/sha256/' + ($imageId -replace '^sha256:', '')
    $hasId = $false; $tags = @(); $blobs = 0
    $fs = [IO.File]::OpenRead($file)
    try {
        $reader = [Formats.Tar.TarReader]::new($fs)
        while ($null -ne ($entry = $reader.GetNextEntry())) {
            if ($null -eq $entry.DataStream) { continue }
            if ($entry.Name -eq 'manifest.json') {
                $tags = @((([IO.StreamReader]::new($entry.DataStream)).ReadToEnd() | ConvertFrom-Json).RepoTags)
            } elseif ($entry.Name -like 'blobs/sha256/*') {
                $hex = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entry.DataStream)).ToLowerInvariant()
                if ("blobs/sha256/$hex" -ne $entry.Name) { throw "$file is damaged: $($entry.Name) hashes to $hex." }
                if ($entry.Name -eq $wantBlob) { $hasId = $true }
                $blobs++
            }
        }
    } finally { $fs.Dispose() }
    if (-not $hasId) { throw "$file does not hold $wantBlob, so it is not the image that runs." }
    if ($tags -notcontains $tag) { throw "$file is not tagged $tag (it carries: $($tags -join ', '))." }
    return $blobs
}

function Save-Image([string]$image, [string]$file) {
    & docker save -o $file $image
    if ($LASTEXITCODE -ne 0) { throw "docker save $image failed ($LASTEXITCODE)." }
}

$script:token = Read-EnvValue 'FLY_API_TOKEN'
$localSecret = Read-EnvValue 'IRONFRONT_SHARED_SECRET'

# Asked before anything is written, so a VM that is off leaves no half backup behind.
if (-not $SkipGameServers) {
    & ssh @sshArgs $target 'true' 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "cannot reach the game-server VM $target over ssh ($LASTEXITCODE). If it is off on purpose, pass -SkipGameServers to back up the master alone."
    }
}

Write-Host "[backup] writing to $work"
foreach ($d in 'master', 'secrets') {
    New-Item -ItemType Directory -Force -Path (Join-Path $work $d) | Out-Null
}
$secretsDir = Join-Path $work 'secrets'
& icacls $secretsDir /inheritance:r /grant:r "${env:USERDOMAIN}\${env:USERNAME}:(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls could not restrict $secretsDir ($LASTEXITCODE)." }

# ---- the master (fly) ----
$machines = @(Invoke-Fly GET '/machines')
if ($machines.Count -ne 1) { throw "expected one machine in $App, found $($machines.Count)." }
$machine = $machines[0]
$script:machineId = $machine.id
if ($machine.state -ne 'started') { throw "machine $($machine.id) is $($machine.state): start it first, exec needs it running." }
$revision = [string]$machine.image_ref.labels.'org.opencontainers.image.revision'
ConvertTo-Json -InputObject $machine -Depth 40 | Set-Content -Encoding utf8 (Join-Path $work 'master/machine.json')
$volumes = @(Invoke-Fly GET '/volumes')
ConvertTo-Json -InputObject $volumes -Depth 20 | Set-Content -Encoding utf8 (Join-Path $work 'master/volumes.json')
foreach ($v in $volumes) {
    try {
        $snapshots = @(Invoke-Fly GET "/volumes/$($v.id)/snapshots")
        ConvertTo-Json -InputObject $snapshots -Depth 10 | Set-Content -Encoding utf8 (Join-Path $work "master/snapshots-$($v.id).json")
        $facts["fly snapshots of $($v.name)"] = "$($snapshots.Count) kept by fly (retention $($v.snapshot_retention) days); they die with the volume"
    } catch { Write-Warning "could not list the snapshots of $($v.id): $($_.Exception.Message)" }
}
$facts['master machine'] = "$($machine.id) $($machine.state) in $($machine.region), $($machine.config.guest.cpu_kind) $($machine.config.guest.cpus) cpu / $($machine.config.guest.memory_mb) MB"
$facts['master image'] = "$($machine.config.image) (revision $revision)"

$csv = @((Invoke-FlyExec @('sh', '-c', 'head -1 /data/durability.csv; tail -1 /data/durability.csv')) -split "`n" | Where-Object { $_.Trim() })
$columns = $csv[0].Split(','); $values = $csv[-1].Split(',')
$live = @{}; for ($i = 0; $i -lt $columns.Count; $i++) { $live[$columns[$i]] = $values[$i] }
$facts['master at backup'] = "$($live.tsUtc): $($live.accountsTotal) accounts, $($live.onlineNow) online, $($live.roomsActive) room(s), $($live.gsRegistered) game server(s) registered"

Write-Host '[backup] master database (the master''s --backup, in the machine) ...'
# IRONFRONT_LOG_FILE emptied for this one process: it inherits the machine's env, so it would open
# /data/master.log beside the live master, and .NET's FileMode.Append on Linux is not O_APPEND. Each
# process writes at its own offset, so one overwrote the other's line on both runs of 2026-10-18.
Invoke-FlyExec -TimeoutSec 120 @('sh', '-c',
    'rm -f /tmp/ltm-backup.db /tmp/ltm-backup.db.gz; cd /app && IRONFRONT_LOG_FILE= dotnet Ironfront.MasterServer.dll --backup /tmp/ltm-backup.db > /tmp/ltm-backup.log 2>&1 || { cat /tmp/ltm-backup.log; exit 1; }; gzip /tmp/ltm-backup.db') | Out-Null
$dbGz = Join-Path $work 'master/ironfront.db.gz'
Receive-FlyFile '/tmp/ltm-backup.db.gz' $dbGz
Invoke-FlyExec @('rm', '-f', '/tmp/ltm-backup.db.gz', '/tmp/ltm-backup.log') | Out-Null
$db = Join-Path $work 'master/ironfront.db'
Expand-GzipFile $dbGz $db
Remove-Item $dbGz

# immutable=1: a WAL-mode file opened normally grows -wal/-shm beside it; the backup must stay as taken.
$checkPy = Join-Path ([IO.Path]::GetTempPath()) "ltm-dbcheck-$PID.py"
Set-Content -Encoding utf8 $checkPy @'
import json, pathlib, sqlite3, sys
con = sqlite3.connect(pathlib.Path(sys.argv[1]).resolve().as_uri() + "?immutable=1", uri=True)
out = {"integrity": con.execute("PRAGMA integrity_check").fetchone()[0], "tables": {}}
for (name,) in con.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name"):
    out["tables"][name] = con.execute('SELECT COUNT(*) FROM "%s"' % name.replace('"', '""')).fetchone()[0]
print(json.dumps(out))
'@
try { $check = (& python -I $checkPy $db) | ConvertFrom-Json } finally { Remove-Item $checkPy -ErrorAction SilentlyContinue }
if ($LASTEXITCODE -ne 0 -or $check.integrity -ne 'ok') { throw "the database backup failed its check: $($check.integrity)" }
$accounts = [int]$check.tables.accounts
$facts['database'] = "integrity ok; " + (($check.tables.PSObject.Properties | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ', ')
if ($accounts -ne [int]$live.accountsTotal) {
    Write-Warning "the backup holds $accounts accounts, the master reported $($live.accountsTotal) a minute before (a sign-up in between?)."
}

Write-Host '[backup] master /data files (logs, durability.csv, older copies) ...'
$listing = Invoke-FlyExec -TimeoutSec 120 @('sh', '-c',
    'cd /data && rm -f /tmp/ltm-data.tgz && tar czf /tmp/ltm-data.tgz $(ls -A | grep -vx -e lost+found -e ironfront.db -e ironfront.db-wal -e ironfront.db-shm); rc=$?; [ $rc -le 1 ] && tar tzf /tmp/ltm-data.tgz')
Receive-FlyFile '/tmp/ltm-data.tgz' (Join-Path $work 'master/data-files.tar.gz')
Invoke-FlyExec @('rm', '-f', '/tmp/ltm-data.tgz') | Out-Null
$facts['master data files'] = (@($listing -split "`n" | Where-Object { $_ -and $_ -notmatch '/$' }) -join ', ')

Write-Host '[backup] master secrets ...'
$configEnv = @($machine.config.env.PSObject.Properties.Name)
$processEnv = @((Invoke-FlyExec @('sh', '-c', 'env | cut -d= -f1')) -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$secretNames = @($processEnv | Where-Object { $_ -like 'IRONFRONT_*' -and $configEnv -notcontains $_ } | Sort-Object -Unique)
if ($secretNames -notcontains 'IRONFRONT_SHARED_SECRET') { throw 'the master runs without IRONFRONT_SHARED_SECRET among its secrets.' }
$flySecrets = [ordered]@{}
foreach ($name in $secretNames) { $flySecrets[$name] = (Invoke-FlyExec -Secret @('printenv', $name)).TrimEnd("`n") }
[IO.File]::WriteAllText((Join-Path $secretsDir 'fly-secrets.env'),
    (($flySecrets.Keys | ForEach-Object { "$_=$($flySecrets[$_])" }) -join "`n") + "`n")
Copy-Item -LiteralPath $EnvFile (Join-Path $secretsDir 'repo.env')

# ---- the game servers (Azure VM) ----
$vmSecret = $null
if ($SkipGameServers) {
    $facts['game servers'] = 'skipped (-SkipGameServers): no VM files, logs, inventory or game-server image'
} else {
    Write-Host '[backup] game-server VM: files, logs, inventory ...'
    foreach ($d in 'gameservers/logs', 'gameservers/vm-ironfront') { New-Item -ItemType Directory -Force -Path (Join-Path $work $d) | Out-Null }
    foreach ($f in @(Invoke-Vm 'cd ~/ironfront && ls -A')) {
        $destination = if ($f -eq '.env') { Join-Path $secretsDir 'vm-ironfront.env' } else { Join-Path $work "gameservers/vm-ironfront/$f" }
        Copy-FromVm "ironfront/$f" $destination
    }
    $vmEnv = Join-Path $secretsDir 'vm-ironfront.env'
    $vmSecret = if (Test-Path $vmEnv) {
        ((Get-Content $vmEnv | Where-Object { $_ -match '^IRONFRONT_SHARED_SECRET=' } | Select-Object -First 1) -replace '^IRONFRONT_SHARED_SECRET=', '').Trim()
    } else { '' }

    $containers = @(Invoke-Vm "sudo docker ps -a --format '{{.Names}}' | grep '^ironfront-gs-' || true" | Where-Object { $_ })
    if ($containers.Count -gt 0) {
        Invoke-Vm ('rm -rf /tmp/ltm-backup && mkdir -m 700 /tmp/ltm-backup && for c in ' + ($containers -join ' ') +
                   '; do sudo docker logs -t $c 2>&1 | gzip -c > /tmp/ltm-backup/$c.log.gz; done') | Out-Null
        foreach ($c in $containers) { Copy-FromVm "/tmp/ltm-backup/$c.log.gz" (Join-Path $work "gameservers/logs/$c.log.gz") }
        Invoke-Vm 'rm -rf /tmp/ltm-backup' | Out-Null
    } else { Write-Warning 'no ironfront-gs-* container on the VM: there are no game-server logs to keep.' }

    $inventory = Invoke-Vm (@(
        'echo "== taken (UTC)"; date -u +%FT%TZ',
        'echo "== os"; . /etc/os-release; echo "$PRETTY_NAME, kernel $(uname -r)"; uptime',
        'echo "== disk"; df -h /',
        'echo "== docker"; sudo docker version --format "engine {{.Server.Version}}"; sudo docker compose version',
        'echo "== containers"; sudo docker ps -a --format "{{.Names}} | {{.Image}} | {{.Status}} | {{.Ports}}"',
        'echo "== images"; sudo docker images --format "{{.Repository}}:{{.Tag}} {{.ID}} {{.Size}}"',
        'echo "== enabled at boot"; systemctl is-enabled docker containerd',
        'echo "== udp listeners"; sudo ss -ulnp',
        'echo "== docker apt source"; cat /etc/apt/sources.list.d/docker* 2>/dev/null',
        'echo "== packages installed by hand"; apt-mark showmanual | tr "\n" " "; echo',
        'echo "== ssh keys allowed for this user (type and comment)"; awk "{print \$1, \$NF}" ~/.ssh/authorized_keys'
    ) -join '; ')
    Set-Content -Encoding utf8 (Join-Path $work 'gameservers/vm-inventory.txt') $inventory
    $imds = Invoke-Vm "curl -s -H Metadata:true --noproxy '*' 'http://169.254.169.254/metadata/instance?api-version=2021-02-01'"
    Set-Content -Encoding utf8 (Join-Path $work 'gameservers/azure-imds.json') $imds
    $compute = ($imds | ConvertFrom-Json).compute
    $facts['game-server VM'] = "$($compute.name) $($compute.vmSize) in $($compute.location), resource group $($compute.resourceGroupName), $($compute.storageProfile.osDisk.diskSizeGB) GB $($compute.storageProfile.osDisk.managedDisk.storageAccountType) disk, address $VmHost"
    $facts['game-server containers'] = (@(Invoke-Vm "sudo docker ps -a --filter name=^ironfront-gs- --format '{{.Names}} {{.Image}} ({{.Status}})'") -join '; ')
}

$masterSecret = $flySecrets['IRONFRONT_SHARED_SECRET']
$vmMatch = if ($null -eq $vmSecret) { 'not read (-SkipGameServers)' } else { $masterSecret -ceq $vmSecret }
$facts['shared secret'] = "the master's = repo .env: $($masterSecret -ceq $localSecret); = VM .env: $vmMatch"
if ($masterSecret -cne $localSecret) {
    Write-Warning 'the repo .env holds a different IRONFRONT_SHARED_SECRET from the master: deploys from it would not register. secrets/fly-secrets.env has the master''s.'
}

# ---- images ----
if ($SkipImages) {
    $facts['images'] = 'skipped (-SkipImages)'
} else {
    $gameServerImages = if ($SkipGameServers) { @() } else {
        @(Invoke-Vm "sudo docker ps -a --filter name=^ironfront-gs- --format '{{.Image}}' | sort -u" | Where-Object { $_ })
    }
    foreach ($image in $gameServerImages) {
        $vmId = ([string](Invoke-Vm "sudo docker image inspect --format '{{.Id}}' $image")).Trim()
        $localId = [string](& docker image inspect --format '{{.Id}}' $image 2>$null)
        $file = Join-Path $work ('gameservers/' + ($image -replace '[:/]', '_') + '.tar')
        Write-Host "[backup] image $image ..."
        if ($LASTEXITCODE -eq 0 -and $localId.Trim() -eq $vmId) {
            Save-Image $image $file
            $from = 'this PC (same id as the VM)'
        } else {
            Invoke-Vm "sudo docker save -o /tmp/ltm-image.tar $image && sudo chown `$(id -u) /tmp/ltm-image.tar" | Out-Null
            Copy-FromVm '/tmp/ltm-image.tar' $file
            Invoke-Vm 'rm -f /tmp/ltm-image.tar' | Out-Null
            $from = 'the VM'
        }
        $blobs = Assert-SavedImage $file $vmId $image
        $facts["image $image"] = "$vmId, from $from, $blobs blobs verified"
    }

    $masterRef = [string]$machine.config.image
    $masterTag = 'ironfront-master:' + $revision.Substring(0, [Math]::Min(8, $revision.Length))
    & docker image inspect $masterRef *> $null
    $pulled = $LASTEXITCODE -ne 0
    Write-Host "[backup] image $masterTag ($masterRef) ..."
    if ($pulled) { & docker pull -q $masterRef | Out-Null; if ($LASTEXITCODE -ne 0) { throw "docker pull $masterRef failed ($LASTEXITCODE)." } }
    & docker tag $masterRef $masterTag
    if ($LASTEXITCODE -ne 0) { throw "docker tag $masterRef $masterTag failed ($LASTEXITCODE)." }
    $masterId = ([string](& docker image inspect --format '{{.Id}}' $masterRef)).Trim()
    $file = Join-Path $work ('master/' + ($masterTag -replace '[:/]', '_') + '.tar')
    try { Save-Image $masterTag $file }
    finally {
        & docker rmi $masterTag | Out-Null
        if ($pulled) { & docker rmi $masterRef | Out-Null }
    }
    $blobs = Assert-SavedImage $file $masterId $masterTag
    $facts["image $masterTag"] = "$masterId (= the digest fly runs), $blobs blobs verified"
}

# ---- restore guide, manifest, done ----
Copy-Item (Join-Path $repo 'docs/live-stack-backup.md') (Join-Path $work 'RESTORE.md')
$head = (& git -C $repo rev-parse --short HEAD 2>$null)
$manifest = @('Ironfront live-stack backup', "taken $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz') by tools/backup-live-stack.ps1 at $head", '')
foreach ($k in $facts.Keys) { $manifest += ('{0,-26} {1}' -f $k, $facts[$k]) }
$manifest += @('', 'secrets/ (this Windows user only): fly-secrets.env = the master''s fly secrets, repo.env = the repo .env',
               '  (FLY_API_TOKEN, IRONFRONT_SHARED_SECRET), vm-ironfront.env = the VM''s compose env.', '', 'files (sha256  bytes  path):')
Get-ChildItem -LiteralPath $work -Recurse -File | Sort-Object FullName | ForEach-Object {
    $manifest += ('{0}  {1,10}  {2}' -f (Get-FileSha256 $_.FullName), $_.Length, $_.FullName.Substring($work.Length + 1).Replace('\', '/'))
}
Set-Content -Encoding utf8 (Join-Path $work 'MANIFEST.txt') $manifest
Rename-Item -LiteralPath $work -NewName (Split-Path -Leaf $OutDir)

$total = (Get-ChildItem -LiteralPath $OutDir -Recurse -File | Measure-Object Length -Sum).Sum
foreach ($k in $facts.Keys) { Write-Host ('[backup] {0,-26} {1}' -f $k, $facts[$k]) }
Write-Host ("[backup] done: $OutDir ({0:N1} MB). secrets/ holds live credentials: keep a copy off this PC, never in the repo." -f ($total / 1MB))
