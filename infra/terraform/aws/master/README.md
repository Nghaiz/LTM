# AWS EC2 — master server

Replaced the fly.io app `kien-master-2026` on 2026-11-07. That app has been destroyed, and its
database was carried over at the cutover. One EC2 instance
runs the master in Docker and **starts it on its own**: no SSH and no manual steps on the box.

| | |
|---|---|
| Region | `ap-southeast-2` (Sydney). The account's organization SCP denies every other region |
| Instance | `m7i-flex.large`: 2 vCPU / 8 GiB, x86_64 (the image is amd64-only). The account is on the Free Tier plan, and that refuses `t3.large` |
| OS | Ubuntu 24.04, gp3 30 GB encrypted, IMDSv2 only |
| Address | Elastic IP. The hostname is `<ip-with-dashes>.sslip.io`, so no DNS setup is needed |
| TLS | Let's Encrypt (HTTP-01 on 80/tcp). The master serves TLS itself from `/opt/ironfront/tls/master.pfx` |
| Ports | `443/tcp` and `27000/tcp`: the same MSP listener (TLS). `80/tcp` is open for ACME. Metrics `27001` is loopback only |
| Secret | `IRONFRONT_SHARED_SECRET` in SSM Parameter Store `/ironfront/shared-secret` (SecureString). **Not** in Terraform or its state |
| Backups | daily 19:30 UTC to `s3://<backup_bucket>/backups/`, kept 30 days |
| Shell access | SSM Session Manager. Port 22 is closed unless `ssh_public_key` is set |

## Files

| Path | What |
|---|---|
| `*.tf` | VPC, security group, Elastic IP, IAM role (SSM core + read the secret + the backup bucket), S3 bucket, instance |
| `files/` | Written onto the instance by cloud-init: `compose.yaml`, the systemd units, `/opt/ironfront/bin/*.sh` |
| `deploy.sh` | Deploys a new master image over SSM and records it in the `LIVE_MASTER_IMAGE` repository variable, so `ghcr-retention.yml` never prunes it |
| `lib.sh` | Shared helpers: `terraform output` plus SSM Run Command |

## How the instance boots

1. cloud-init installs Docker and the AWS CLI, writes `files/` and enables `ironfront-master.service`.
2. `ironfront-master.service` runs `ironfront-bootstrap.sh` (ExecStartPre) before each start:
   - It reads the secret from SSM and writes `/opt/ironfront/.env` (0600). It waits until the
     parameter exists.
   - It waits for the Elastic IP, issues the certificate with `certbot --standalone`, and builds
     the PFX.
   - If there is no database, it restores the newest backup from S3. A rebuilt instance gets its
     accounts back this way.
3. `docker compose up -d` runs with the image from `/opt/ironfront/master.env`.

A failed start retries every 30 s for as long as it takes.

## First-time setup

```bash
cd infra/terraform/aws/master
cp terraform.tfvars.example terraform.tfvars   # set master_image (a digest)
terraform init && terraform apply

# The secret: the SAME value the game servers use (the one fly has).
aws ssm put-parameter --region ap-southeast-2 --name /ironfront/shared-secret \
    --type SecureString --value '<secret>'
# The master starts by itself within about 30 s. To watch it:
aws ssm start-session --region ap-southeast-2 --target "$(terraform output -raw instance_id)"
#   sudo journalctl -u ironfront-master -f
```

## Day to day

```bash
./infra/terraform/aws/master/deploy.sh                    # newest 'develop' image
IRONFRONT_MASTER_IMAGE=ghcr.io/nghaiz/ironfront-master@sha256:... ./infra/terraform/aws/master/deploy.sh
FORCE=1 ./infra/terraform/aws/master/deploy.sh            # even with rooms open
```

`deploy.sh` refuses while a room is open, because a restart drops every player (the 2026-10-14
incident). It pulls the image first, restarts the master, and then reads back health, revision and
`protocol vN`. To roll back, deploy the previous digest; `deploy.sh` prints it.

On the instance (through `aws ssm start-session`):

```bash
sudo journalctl -u ironfront-master -f          # bootstrap
sudo docker logs -f ironfront-master            # master (also /opt/ironfront/data/master.log)
nc 127.0.0.1 27001                              # metrics (raw TCP JSON, not HTTP)
sudo /opt/ironfront/bin/ironfront-backup.sh manual
sudo /opt/ironfront/bin/ironfront-restore.sh backups/<file>.db.gz
```

To rotate the secret: `aws ssm put-parameter --overwrite ...`, then
`sudo systemctl restart ironfront-master`, then redeploy the game servers with the same value.

## Things to know

- **Terraform never replaces the instance on its own.** `ami` and `user_data` are in
  `ignore_changes`. After you edit `files/`, either copy the change onto the box or rebuild it
  with `terraform apply -replace=aws_instance.master`. A rebuilt box restores the newest S3
  backup, so run `ironfront-backup.sh` first.
- **Renewing the certificate restarts the master at 20:00 UTC (03:00 +07), not immediately.**
  The old certificate is still valid for about 30 days when certbot renews, so waiting is safe.
- **No PROXY protocol** (the old fly deployment needed it). Clients connect directly, so the per-IP limits see
  real addresses.
- **State is local** (`terraform.tfstate`, gitignored). It holds no secret, but it is the only
  record of what was created. Hand it over along with the repo, or move it to an S3 backend.
