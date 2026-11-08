# Ironfront Ansible Deployment Automation

Automated CD (Continuous Deployment) configuration for Ironfront multiplayer infrastructure using Ansible.

## Architecture & Inventory

- **Master Server** (`[master]`): AWS EC2 (`m7i-flex.large`, Sydney) running `ironfront-master` (ASP.NET / MSP + TLS).
- **Game Servers** (`[gameservers]`): Azure VM (`Standard_B2as_v2`, Singapore) running 3 game server containers (Dustbowl, Island, Forest Lake) on UDP 27015-27017.
- **Standalone** (`[standalone]`): Single-VM stack (`infra/compose/compose.yaml`) for local/self-hosted full deployments.

```
infra/ansible/
├── ansible.cfg                # Default Ansible config (SSH, roles_path, inventory)
├── inventory/
│   ├── production.ini         # Production hosts (EC2 Master + Azure Game Servers)
│   ├── staging.ini            # Staging hosts template
│   └── group_vars/
│       ├── all.yml            # Common paths and secrets
│       ├── master.yml         # Master ports, options, image repo, dynamic host overrides
│       ├── gameservers.yml    # Map catalog, ports, master host, dynamic host overrides
│       └── standalone.yml     # Single-VM compose settings
├── playbooks/
│   ├── site.yml               # Deploys both Master and Game Servers
│   ├── deploy-master.yml      # Deploys Master Server only
│   ├── deploy-gameservers.yml # Deploys Game Servers only
│   └── deploy-standalone.yml  # Deploys Single-VM Compose stack
└── roles/
    ├── common/                # Verifies Docker & Compose, directory permissions, GHCR login
    ├── master/                # Durability check, compose deploy, service restart, healthcheck
    ├── gameserver/            # Compose deploy, registration verification with master
    └── standalone/            # Single-VM full stack deployment
```

## Prerequisites

- Ansible 2.15+ (with `pipelining = True` and python3 on targets).
- SSH access to target machines:
  - AWS EC2: user `ubuntu` with authorized SSH key (`MASTER_SERVER_KEY`).
  - Azure VM: user `irondev` with authorized SSH key.

## Production Safety Policy & GitHub Actions CD

All deployments to the production server are **100% MANUAL** via GitHub Actions:
- Pushing to any branch (`main`, `release`), pushing a git tag (`v*.*.*`), or publishing a GitHub Release will **NEVER** automatically deploy to the server.
- To deploy, navigate to **GitHub Actions → CD → Click "Run workflow"**:
  1. **Use workflow from**: Choose the branch or tag to deploy (`main`, `release`, `v4.6.5`, etc.).
  2. **Deployment target**: `master`, `gameservers`, `all`, or `standalone`.
  3. **Target environment**: `production` or `staging`.
  4. **image_tag**: Optional specific image digest or release tag (leave blank to build selected ref).
  5. **force**: Set true to deploy even if active rooms are in progress.
- If configured with GitHub Environment Protection Rules ("Required reviewers"), deployment will pause and require an explicit "Approve and deploy" before Ansible starts.

## Manual CLI Usage

### 1. Deploy Master Server only

Deploy the newest build of master:
```bash
ansible-playbook -i inventory/production.ini playbooks/deploy-master.yml \
  -e "master_image=ghcr.io/nghaiz/ironfront-master@sha256:<digest>"
```

Deploy even if rooms/matches are open (`force_deploy`):
```bash
ansible-playbook -i inventory/production.ini playbooks/deploy-master.yml \
  -e "master_image=ghcr.io/nghaiz/ironfront-master@sha256:<digest>" \
  -e "force_deploy=true"
```

### 2. Deploy Game Servers only

```bash
ansible-playbook -i inventory/production.ini playbooks/deploy-gameservers.yml \
  -e "gameserver_image=ghcr.io/nghaiz/ironfront-gameserver:v4.6.5"
```

### 3. Full Deployment (Master + Game Servers)

```bash
ansible-playbook -i inventory/production.ini playbooks/site.yml \
  -e "master_image=ghcr.io/nghaiz/ironfront-master@sha256:<digest>" \
  -e "gameserver_image=ghcr.io/nghaiz/ironfront-gameserver:v4.6.5"
```

### 4. Single-VM Standalone Stack

```bash
ansible-playbook -i inventory/production.ini playbooks/deploy-standalone.yml \
  -e "standalone_master_image=ghcr.io/nghaiz/ironfront-master@sha256:<digest>" \
  -e "standalone_gameserver_image=ironfront-game-server:v4.6.5"
```

## Safety Mechanisms & Healthchecks

1. **Active Match Protection**: `roles/master` inspects `/opt/ironfront/data/durability.csv`. If `roomsActive > 0`, the deployment halts immediately to avoid disconnecting active players (unless `-e force_deploy=true` is passed).
2. **Digest Enforcement**: Refuses `:latest` images. Every deployment requires an immutable digest or version tag.
3. **Master Healthcheck**: Waits for `docker inspect` to report `healthy` before finishing.
4. **Game Server Registration Check**: Verifies that every deployed container reports `master link: registered as server` in its logs within 180s.
