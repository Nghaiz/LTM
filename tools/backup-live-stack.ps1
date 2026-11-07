#!/usr/bin/env pwsh
# tools/backup-live-stack.ps1 -- copies what the live stack holds onto this PC, so either host can be
# stopped, lost or replaced: the EC2 master's database, data files, config and secret, the Azure
# VM's compose files, env, logs and inventory, and both server images.
#
# WHAT GIT CANNOT GIVE BACK. Everything else is rebuilt from the repo at the deployed revision:
#   - the accounts, careers, achievements and match history: the master's SQLite database. The EC2
#     instance also copies it to S3 every day (infra/terraform/aws/master), this is the off-AWS copy;
#   - IRONFRONT_SHARED_SECRET, held in SSM Parameter Store (/ironfront/shared-secret). It is read
#     here and compared with the repo .env and the VM .env, because deploy-gameservers-azure.ps1
#     takes the game servers' copy from the repo .env;
#   - the game-server image. Deploys side-load it from this PC's Docker and the VM keeps only the one
#     that runs, so no registry holds it, and rebuilding it takes Unity;
#   - the logs: /opt/ironfront/data/master.log on the instance, and `docker logs` of each map's
#     container, which a redeploy or `compose down` deletes.
#
# HOW IT READS THE MASTER. The aws CLI, logged in to the account that owns the instance: SSM Run
# Command runs the instance's own /opt/ironfront/bin/ironfront-backup.sh (the master's `--backup`,
# SQLite's online backup API -- copying ironfront.db while the master writes leaves out what is
# still in ironfront.db-wal, and fails silently), which puts the copy in S3, and it is downloaded
# from there. The data files take the same route under data-files/. Nothing secret is printed.
#
# IMAGES are `docker save`d without --platform. With --platform the tar keeps only the amd64 manifest,
# so `docker load` gives back a different image id than the one that runs; without it the tar holds
# the image's own index, which every blob name in the tar is checked against.
#
# WHERE IT WRITES. ../LTM-backups/live-stack-<yyyyMMdd-HHmm>/ beside the repo (the folder
# earlier tooling used), never inside it: secrets/ holds the shared secret, and only this Windows
# user may read it. Work happens in <dir>.partial,
# renamed when every check has passed, so a folder without that suffix is a complete backup.
#
# Restoring: docs/live-stack-backup.md, copied into the backup as RESTORE.md.
#
# Usage:
#   pwsh tools/backup-live-stack.ps1
#   pwsh tools/backup-live-stack.ps1 -SkipImages                    # data and config only, ~10 MB
#   pwsh tools/backup-live-stack.ps1 -SkipGameServers               # the master alone (the VM is off)
#   (needs the aws CLI logged in: `aws login`, or AWS_PROFILE set)
#   pwsh tools/backup-live-stack.ps1 -EnvFile D:\Coding\LTM\.env    # from a git worktree (no .env)

[CmdletBinding()]
param(
    [string] $OutDir = '',
    [string] $EnvFile = '',
    # The EC2 master (infra/terraform/aws/master: `terraform output`).
    [string] $InstanceId = 'i-0d82a32b23a370c59',
    [string] $Region = 'ap-southeast-2',
    [string] $Bucket = 'ironfront-master-backups-b647cb91eaa1e49f82570f2dfe',
    [string] $SecretParameter = '/ironfront/shared-secret',
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

# The aws CLI, with --output json parsed. A failure is reported by exit code and the CLI's own
# message, which never contains a secret value (get-parameter output is not echoed on failure).
function Invoke-Aws([string[]]$Argv) {
    $out = & aws @Argv --region $Region --output json
    if ($LASTEXITCODE -ne 0) { throw "aws $($Argv[0]) $($Argv[1]) failed ($LASTEXITCODE)." }
    if ($out) { return (($out -join "`n") | ConvertFrom-Json) }
}

# Runs a shell command as root on the instance through SSM Run Command and returns its stdout
# (SSM keeps the first 24,000 characters: anything big goes through S3 instead).
function Invoke-Master([string]$command, [int]$TimeoutSec = 600) {
    $paramFile = Join-Path ([IO.Path]::GetTempPath()) "ltm-ssm-$PID.json"
    @{ commands = @($command); executionTimeout = @("$TimeoutSec") } | ConvertTo-Json -Compress | Set-Content -Encoding ascii $paramFile
    try {
        $sent = Invoke-Aws @('ssm', 'send-command', '--instance-ids', $InstanceId, '--document-name', 'AWS-RunShellScript',
                             '--comment', 'ironfront: backup-live-stack.ps1', '--parameters', "file://$paramFile")
    } finally { Remove-Item $paramFile -ErrorAction SilentlyContinue }
    $id = $sent.Command.CommandId
    do {
        Start-Sleep -Seconds 3
        $inv = & aws ssm get-command-invocation --region $Region --command-id $id --instance-id $InstanceId --output json 2>$null | ConvertFrom-Json
        $status = if ($inv) { $inv.Status } else { 'Pending' }
    } while ($status -in 'Pending', 'InProgress', 'Delayed')
    if ($status -ne 'Success') { throw "on the master, '$command' ended $status`: $($inv.StandardErrorContent)" }
    return [string]$inv.StandardOutputContent
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

# ---- the master (AWS EC2) ----
$instance = (Invoke-Aws @('ec2', 'describe-instances', '--instance-ids', $InstanceId)).Reservations[0].Instances[0]
if ($instance.State.Name -ne 'running') { throw "instance $InstanceId is $($instance.State.Name): start it first." }
ConvertTo-Json -InputObject $instance -Depth 20 | Set-Content -Encoding utf8 (Join-Path $work 'master/instance.json')
$facts['master instance'] = "$InstanceId $($instance.State.Name) in $($instance.Placement.AvailabilityZone), $($instance.InstanceType), address $($instance.PublicIpAddress)"

$masterRef = (Invoke-Master 'cut -d= -f2- /opt/ironfront/master.env').Trim()
$revision = (Invoke-Master 'docker inspect -f ''{{index .Config.Labels "org.opencontainers.image.revision"}}'' ironfront-master').Trim()
Invoke-Master 'cat /opt/ironfront/compose.yaml /opt/ironfront/master.env /opt/ironfront/aws.env' |
    Set-Content -Encoding utf8 (Join-Path $work 'master/config.txt')
$facts['master image'] = "$masterRef (revision $revision)"

$csv = @((Invoke-Master 'head -1 /opt/ironfront/data/durability.csv; tail -1 /opt/ironfront/data/durability.csv') -split "`n" | Where-Object { $_.Trim() })
$columns = $csv[0].Split(','); $values = $csv[-1].Split(',')
$live = @{}; for ($i = 0; $i -lt $columns.Count; $i++) { $live[$columns[$i]] = $values[$i] }
$facts['master at backup'] = "$($live.tsUtc): $($live.accountsTotal) accounts, $($live.onlineNow) online, $($live.roomsActive) room(s), $($live.gsRegistered) game server(s) registered"

Write-Host '[backup] master database (the master''s --backup on the instance, through S3) ...'
$backupOut = Invoke-Master '/opt/ironfront/bin/ironfront-backup.sh manual'
$s3Uri = ([regex]::Match($backupOut, 's3://\S+\.db\.gz')).Value
if (-not $s3Uri) { throw "ironfront-backup.sh did not report where it wrote: $backupOut" }
$dbGz = Join-Path $work 'master/ironfront.db.gz'
& aws s3 cp --only-show-errors --region $Region $s3Uri $dbGz
if ($LASTEXITCODE -ne 0) { throw "aws s3 cp $s3Uri failed ($LASTEXITCODE)." }
$facts['database in S3'] = $s3Uri
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

Write-Host '[backup] master data files (logs, durability.csv, replaced databases) ...'
$dataKey = "data-files/data-files-$(Get-Date -Format 'yyyyMMdd-HHmmss').tar.gz"
$listing = Invoke-Master ('cd /opt/ironfront/data && tar czf /tmp/ltm-data.tgz --exclude=ironfront.db --exclude=ironfront.db-wal --exclude=ironfront.db-shm --exclude=".backup-*" . ' +
    "&& /snap/bin/aws s3 cp --only-show-errors --region $Region /tmp/ltm-data.tgz s3://$Bucket/$dataKey && tar tzf /tmp/ltm-data.tgz; rm -f /tmp/ltm-data.tgz")
& aws s3 cp --only-show-errors --region $Region "s3://$Bucket/$dataKey" (Join-Path $work 'master/data-files.tar.gz')
if ($LASTEXITCODE -ne 0) { throw "aws s3 cp s3://$Bucket/$dataKey failed ($LASTEXITCODE)." }
$facts['master data files'] = (@($listing -split "`n" | Where-Object { $_ -and $_ -notmatch '/$' }) -join ', ')

Write-Host '[backup] master secret ...'
$masterSecret = [string](& aws ssm get-parameter --region $Region --name $SecretParameter --with-decryption --query Parameter.Value --output text)
if ($LASTEXITCODE -ne 0 -or -not $masterSecret.Trim()) { throw "could not read $SecretParameter from SSM ($LASTEXITCODE)." }
$masterSecret = $masterSecret.Trim()
[IO.File]::WriteAllText((Join-Path $secretsDir 'master-secrets.env'), "IRONFRONT_SHARED_SECRET=$masterSecret`n")
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

$vmMatch = if ($null -eq $vmSecret) { 'not read (-SkipGameServers)' } else { $masterSecret -ceq $vmSecret }
$facts['shared secret'] = "the master's = repo .env: $($masterSecret -ceq $localSecret); = VM .env: $vmMatch"
if ($masterSecret -cne $localSecret) {
    Write-Warning 'the repo .env holds a different IRONFRONT_SHARED_SECRET from the master: deploys from it would not register. secrets/master-secrets.env has the master''s.'
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
    $facts["image $masterTag"] = "$masterId (= the digest the master runs), $blobs blobs verified"
}

# ---- restore guide, manifest, done ----
Copy-Item (Join-Path $repo 'docs/live-stack-backup.md') (Join-Path $work 'RESTORE.md')
$head = (& git -C $repo rev-parse --short HEAD 2>$null)
$manifest = @('Ironfront live-stack backup', "taken $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz') by tools/backup-live-stack.ps1 at $head", '')
foreach ($k in $facts.Keys) { $manifest += ('{0,-26} {1}' -f $k, $facts[$k]) }
$manifest += @('', 'secrets/ (this Windows user only): master-secrets.env = the master''s secret from SSM, repo.env = the repo .env',
               '  (IRONFRONT_SHARED_SECRET), vm-ironfront.env = the VM''s compose env.', '', 'files (sha256  bytes  path):')
Get-ChildItem -LiteralPath $work -Recurse -File | Sort-Object FullName | ForEach-Object {
    $manifest += ('{0}  {1,10}  {2}' -f (Get-FileSha256 $_.FullName), $_.Length, $_.FullName.Substring($work.Length + 1).Replace('\', '/'))
}
Set-Content -Encoding utf8 (Join-Path $work 'MANIFEST.txt') $manifest
Rename-Item -LiteralPath $work -NewName (Split-Path -Leaf $OutDir)

$total = (Get-ChildItem -LiteralPath $OutDir -Recurse -File | Measure-Object Length -Sum).Sum
foreach ($k in $facts.Keys) { Write-Host ('[backup] {0,-26} {1}' -f $k, $facts[$k]) }
Write-Host ("[backup] done: $OutDir ({0:N1} MB). secrets/ holds live credentials: keep a copy off this PC, never in the repo." -f ($total / 1MB))
