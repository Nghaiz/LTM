# Terraform — Azure single-VM baseline (phase 03)

Provisions the **cloud** side of the Ironfront online backend: one Ubuntu VM on Azure with
a locked-down network and a private Blob container for off-host database backups. The VM
then runs the master + two game servers with Docker Compose (see
[`infra/compose/`](../compose/README.md)); Terraform does **not** run the containers.

> **This is not high availability.** One VM, one region. A VM, disk or region failure is a
> full outage. It buys process/capacity recovery (Compose restarts a crashed container, two
> game servers share load), nothing more. Represent it that way in the report.

## What it creates

| Resource | Purpose |
|---|---|
| Resource group | Holds everything below. |
| VNet + subnet | `10.42.0.0/24`, one subnet. |
| Network security group | Opens **only**: `22/tcp` from your admin CIDRs, `27000/tcp` public, `27015-27016/udp` public. Everything else — including metrics `27001` — is denied by Azure's default rule. |
| Static Standard public IP | Stable IP for your DNS A record. |
| NIC | Binds the VM to the subnet + public IP. |
| Ubuntu 24.04 VM | System-assigned managed identity; SSH-key-only auth; cloud-init bootstrap. |
| Storage account + container | Private, **key auth disabled** (Entra-ID only), versioned, lifecycle-expired backups. |
| Role assignments | Deployer + VM identity get `Storage Blob Data Contributor` (VM scoped to the container). |

## The security posture, briefly

- **No secret is in Terraform.** Not in variables, not in state, not in cloud-init user-data.
  The shared secret, TLS password and GHCR token are placed on the VM out of band (the VM
  writes `/opt/ironfront/BOOTSTRAP.md` with the checklist). Storage key auth is **disabled**
  (`shared_access_key_enabled = false`) precisely so no account key can be read into state —
  both Terraform and the VM reach Blob over Entra ID.
- **SSH is not open to the Internet.** `ssh_source_cidrs` is required and validation rejects
  `0.0.0.0/0`. The host `ufw` mirrors the NSG as a second line.
- **Metrics (`27001`) is never public.** No NSG rule exists for it; it binds to the host
  loopback inside compose. Reach it over SSH.

## Preflight (before every first apply)

Azure Student SKU/quota is **not** guaranteed and cannot be encoded in source. Confirm the
region + size are available to your offer, then change `terraform.tfvars` (not the code) if
they are not:

```bash
az login
az account show                      # confirm the intended subscription
az vm list-skus --location koreacentral --size Standard_B2ms --output table
az vm list-usage --location koreacentral --output table   # quota headroom
```

## Deploy

```bash
cd infra/terraform
cp terraform.tfvars.example terraform.tfvars     # edit: SSH key, admin CIDR, domain
# (optional but recommended) cp backend.tf.example backend.tf  # remote state

terraform init
terraform fmt -check
terraform validate
terraform plan -out tf.plan        # REVIEW: only the intended network/public resources,
                                   # no secret values anywhere in the diff
terraform apply tf.plan
```

## After apply

1. `terraform output public_ip_address` → create the DNS **A record** for your
   `dns_hostname`. Terraform does not manage DNS. Wait for propagation
   (`dig +short <hostname>`).
2. Issue the TLS certificate → `/opt/ironfront/tls/master.pfx` (see
   [`infra/tls/`](../tls/README.md)).
3. Deliver `/opt/ironfront/.env` and log in to GHCR, then `./deploy.sh up` (see
   [`infra/compose/`](../compose/README.md) and the VM's `BOOTSTRAP.md`).
4. Set the backup env to the outputs and enable the timers (see
   [`infra/systemd/`](../systemd/README.md)):
   - `IRONFRONT_BACKUP_BLOB_ACCOUNT` = `terraform output -raw backup_storage_account_name`
   - `IRONFRONT_BACKUP_BLOB_CONTAINER` = `terraform output -raw backup_container_name`

## State

Local state works for a one-person deploy but is easy to lose. `backend.tf.example` sets up
a locking Azure remote backend authenticated by `az login` (no key in the file). The backend
storage account is created once by hand — it can't be managed by the config that stores its
own state in it.

## Teardown

```bash
terraform destroy
```

Removes every resource in the group **including the backup storage account and its blobs**.
Download anything you need to keep first. DNS records are yours and are not touched.
