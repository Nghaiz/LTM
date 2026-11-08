# Ansible: game-server deploys

One playbook, `playbooks/deploy-gameservers.yml`: it switches the three game servers on the Azure
VM (`irondev@20.205.152.66`, Dustbowl/Island/Forest Lake on UDP 27015-27017) to an image that is
already on the VM. It is the update half of `tools/deploy-gameservers-azure.ps1`, which still does
the first-time setup and ships images (CI cannot build the Unity server).

The EC2 master is not deployed from here: it has no SSH. `infra/terraform/aws/master/deploy.sh`
deploys it over SSM, by hand or from the CD workflow.

## What a run does

1. **Preflight, read-only.** Refuses when:
   - the image is not `ironfront-game-server:<rev>`, or the VM does not hold it;
   - `~/ironfront/.env` or `gameservers.compose.yml` is missing, `.env` has no shared secret, or its
     current image is unknown (there would be nothing to roll back to);
   - a container's latest room event is `master assigned room` (a live room) and `force` is off;
   - the current servers are not running the `.env` image and registered (`force` skips this).
2. **Backup** to `~/ironfront/backups/<UTC time>/`: `.env`, the compose file and every
   container's `docker logs -t` (the only record of a match). The five newest backups are kept.
3. **Change**: installs `infra/docker/gameservers.compose.yml`, rewrites only the
   `IRONFRONT_GAMESERVER_IMAGE=` line of `.env` (the shared secret is never read out or rewritten),
   then `docker compose up -d` in `~/ironfront`. The directory is the compose project name
   (`ironfront`); never run it from anywhere else.
4. **Verify**: every container runs the new image and has logged
   `master link: registered as server` since its current start, within 180 s.
5. **Rollback** if 3 or 4 fails: restores the backup, brings the previous image up, verifies it the
   same way, and fails the run naming both images.

Nothing changes when the image and compose file are already in place; the run only verifies.

## From GitHub Actions

Actions -> **CD** -> Run workflow, from `main` only: target `gameservers`, image
`ironfront-game-server:<rev>`, `force` off unless a live room may be dropped.

Needs the `production` environment's secret `GAMESERVER_SSH_KEY` (the CD deploy key, authorized
for irondev on the VM); without it the run stops before connecting. The host and its host key are
pinned in `inventory/production.ini` and `pinned-hostkeys.txt`, so a changed host key stops the run.
The environment admits only `main` and waits for the owner's approval on every run.

Target `master` runs `deploy.sh` over SSM with the environment variables `AWS_DEPLOY_ROLE_ARN` and
`MASTER_INSTANCE_ID`, both written by `infra/terraform/aws/master/github-deploy-role.sh --apply`
(an IAM role trusted through GitHub OIDC for `repo:Nghaiz/LTM:environment:production`, allowed
`ssm:SendCommand` on that one instance). After a successful deploy it tags the digest `live` on
GHCR, which `tools/prune_ghcr.py` never prunes (it keeps every name-tagged image).

## By hand

```bash
cd infra/ansible
ssh-keygen -F 20.205.152.66 || cat pinned-hostkeys.txt >> ~/.ssh/known_hosts   # the pinned host key
ansible-playbook playbooks/deploy-gameservers.yml \
  --private-key ~/.ssh/nghaiz_ed25519_ctf \
  -e gameserver_image=ironfront-game-server:<rev>
```

Add `-e gameserver_force=true` to deploy over a live room. Ansible needs a Linux or macOS control
machine (WSL on Windows); `ansible-core` 2.17 is what CI pins.
