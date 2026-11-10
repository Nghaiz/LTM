# AWS EC2 — game servers

Runs the Ironfront dedicated game servers (Dustbowl, Island, Forest Lake) on one EC2 instance
in the same AWS region and VPC as the master server (`infra/terraform/aws/master/`).

**Status: STANDBY.** Players are served by the Azure VM (`infra/docker/gameservers.compose.yml`,
CD target `gameservers`). The owner moves them here once the Azure credit runs out (2026-11-10).
While both hosts register with the one master, the matchmaker may send a room to either, so
**keep this instance stopped** unless you are testing or moving over. Cost: `m7i-flex.large` is
$0.1197/h in Sydney (~$90/month with the Elastic IP and disk), paid from the account's Free-plan
credit, which also pays for the master.

Deploy: CD target `gameservers-aws` with `ghcr.io/nghaiz/ironfront-game-server:<rev>`, or
`INSTANCE_ID=<id> REGION=ap-southeast-2 ./deploy.sh ghcr.io/nghaiz/ironfront-game-server:<rev>`.
The on-box half (`files/ironfront-gameserver-deploy.sh`) refuses while a room is live (`FORCE=1`
overrides), backs up the match logs, verifies every server registered since its current start and
rolls back to the previous image on failure, the same contract as the Azure playbook. Cloud-init
writes `files/` only when the instance is created (`user_data` is ignored afterwards), so a changed
script reaches a running instance only by recreating it or copying the file over SSM.

| | |
|---|---|
| Region | `ap-southeast-2` (Sydney), matching master |
| Instance | `m7i-flex.large`: 2 vCPU / 8 GiB, x86_64 amd64 (Free Tier eligible on this account) |
| OS | Ubuntu 24.04, gp3 30 GB encrypted, IMDSv2 only |
| Address | Elastic IP (advertised to master and connecting players) |
| Ports | `27015/udp` (Dustbowl), `27016/udp` (Island), `27017/udp` (Forest Lake). SSH `22/tcp` admin |
| Secret | `IRONFRONT_SHARED_SECRET` in SSM Parameter Store `/ironfront/shared-secret` (read at boot) |
| Master connection | Connects to `master_host:443` over TLS |
| Shell access | SSM Session Manager (`aws ssm start-session --target <instance_id>`) or SSH if key is configured |

## Files

| Path | What |
|---|---|
| `*.tf` | Security group, Elastic IP, IAM role (SSM core + SSM parameter access), instance |
| `files/` | Written onto the instance by cloud-init: `gameservers.compose.yml`, `ironfront-gameserver.service`, `ironfront-gameserver-bootstrap.sh` |
| `terraform.tfvars` | Instance configurations, SSH key, and image repo |

## Setup & Apply

Prerequisites: The master server stack in `infra/terraform/aws/master` must be applied first so that the VPC and public subnet exist.

```bash
cd infra/terraform/aws/game
cp terraform.tfvars.example terraform.tfvars   # if not already created
terraform init
terraform apply
```

To watch the gameserver bootstrap logs:
```bash
aws ssm start-session --region ap-southeast-2 --target "$(terraform output -raw instance_id)"
# Then on the instance:
sudo journalctl -u ironfront-gameserver -f
sudo docker compose -f /opt/ironfront/gameservers.compose.yml ps
sudo docker logs -f ironfront-gs-dustbowl
```
