# Inputs for the Ironfront master on EC2. Nothing here is a secret.

variable "region" {
  description = <<-EOT
    AWS region. Sydney because the account's organization SCP denies every other region (checked
    2026-11-07: ap-southeast-1, ap-northeast-1, us-east-1 and us-west-2 all explicit-deny).
    Singapore would be closer to players and the Azure game servers; the master only carries the
    lobby/login TCP link, so ~130 ms from Vietnam is tolerable. Move if the SCP allows it.
  EOT
  type        = string
  default     = "ap-southeast-2"
}

variable "name" {
  description = "Name prefix for every resource."
  type        = string
  default     = "ironfront-master"
}

variable "instance_type" {
  description = <<-EOT
    2 vCPU / 8 GiB. x86_64 on purpose: .github/workflows/images.yml builds the master image for
    linux/amd64 only, so a Graviton (t4g/m7g) instance cannot run it. m7i-flex.large because this
    account is on the Free Tier plan, which refuses t3.large ("not eligible for Free Tier");
    m7i-flex.large is the one free-tier-eligible type with 8 GiB (checked 2026-11-07 with
    `aws ec2 describe-instance-types --filters Name=free-tier-eligible,Values=true`).
  EOT
  type        = string
  default     = "m7i-flex.large"
}

variable "root_volume_gb" {
  description = "Root gp3 volume: OS, Docker images, the SQLite database and logs."
  type        = number
  default     = 30
}

variable "master_image" {
  description = <<-EOT
    The master image, pinned by digest (images.yml never pushes `latest`). Only used on the
    FIRST boot; later deploys go through infra/terraform/aws/master/deploy.sh, not Terraform.
  EOT
  type        = string

  validation {
    condition     = can(regex("@sha256:[0-9a-f]{64}$", var.master_image))
    error_message = "master_image must be pinned by digest: ghcr.io/nghaiz/ironfront-master@sha256:<64 hex>."
  }
}

variable "master_domain" {
  description = <<-EOT
    Hostname clients dial and the Let's Encrypt certificate names. Empty (default) uses
    <elastic-ip-with-dashes>.sslip.io, which resolves to the Elastic IP with no DNS setup at all.
    Set your own domain only after pointing its A record at the `public_ip` output.
  EOT
  type        = string
  default     = ""
}

variable "acme_email" {
  description = "Optional Let's Encrypt account email (expiry notices). Empty registers without one."
  type        = string
  default     = ""
}

variable "shared_secret_parameter" {
  description = <<-EOT
    SSM Parameter Store name (SecureString) holding IRONFRONT_SHARED_SECRET. Terraform does NOT
    create it, so the secret never enters state; the instance waits at boot until it exists.
  EOT
  type        = string
  default     = "/ironfront/shared-secret"
}

variable "cloudflared_token_parameter" {
  description = <<-EOT
    SSM Parameter Store name (SecureString) holding the Cloudflare Tunnel token for the database
    viewer (db-tunnel in files/compose.yaml). Optional: Terraform does NOT create it, and without
    it the viewer is reachable only through the SSM port forward. See db-viewer.sh.
  EOT
  type        = string
  default     = "/ironfront/cloudflared-token"
}

variable "ssh_public_key" {
  description = "Optional OpenSSH public key. Empty = no key pair and no port 22; use SSM Session Manager."
  type        = string
  default     = ""
}

variable "ssh_cidrs" {
  description = "CIDRs allowed to SSH, only used with ssh_public_key. Never 0.0.0.0/0."
  type        = list(string)
  default     = []

  validation {
    condition     = !contains(var.ssh_cidrs, "0.0.0.0/0")
    error_message = "Do not open SSH to 0.0.0.0/0; use your own /32 or SSM Session Manager."
  }
}

variable "backup_retention_days" {
  description = "Days the daily database backups are kept in S3."
  type        = number
  default     = 30
}
