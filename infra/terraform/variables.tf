# Inputs for the Ironfront single-VM baseline (phase 03 task 4).
#
# Nothing here is a secret. The shared secret, TLS material, alert webhook and GHCR
# credential are supplied on the VM out of band (see infra/compose/.env.example) and never
# pass through Terraform variables, state or user-data.

variable "name_prefix" {
  description = "Short lowercase prefix for resource names (letters/digits only)."
  type        = string
  default     = "ironfront"

  validation {
    condition     = can(regex("^[a-z][a-z0-9]{1,14}$", var.name_prefix))
    error_message = "name_prefix must be 2-15 chars, lowercase letters/digits, starting with a letter."
  }
}

variable "location" {
  description = <<-EOT
    Azure region. Korea Central is the intended default, but Azure Student SKU/quota
    availability is NOT guaranteed and cannot be encoded in source — preflight it with
    `az vm list-skus --location <loc> --size <size>` before apply, and change THIS
    variable (not the code) if it fails.
  EOT
  type        = string
  default     = "koreacentral"
}

variable "vm_size" {
  description = <<-EOT
    VM SKU. The stack needs headroom for the master (~0.75 GB) plus two Unity servers
    (~1.5 GB each) plus the OS — so >=8 GB RAM. Standard_B2ms (2 vCPU / 8 GB) is the
    cost-conscious default; confirm it is available to your offer before apply.
  EOT
  type        = string
  default     = "Standard_B2ms"
}

variable "admin_username" {
  description = "Login user created on the VM (SSH-key only; password auth is disabled)."
  type        = string
  default     = "ironadmin"
}

variable "admin_ssh_public_key" {
  description = "OpenSSH public key for admin_username. No default — a VM you cannot log in to is useless, and a default key would be a backdoor."
  type        = string

  validation {
    condition     = can(regex("^(ssh-ed25519|ssh-rsa|ecdsa-sha2-) ", var.admin_ssh_public_key))
    error_message = "admin_ssh_public_key must be an OpenSSH public key (ssh-ed25519/ssh-rsa/ecdsa-...)."
  }
}

variable "ssh_source_cidrs" {
  description = <<-EOT
    CIDRs allowed to reach SSH (22/tcp). REQUIRED and must be your admin IP(s) — there is
    no default, because a default of 0.0.0.0/0 is how an SSH port ends up open to the
    Internet by omission. Use ["1.2.3.4/32"], never ["0.0.0.0/0"].
  EOT
  type        = list(string)

  validation {
    condition     = length(var.ssh_source_cidrs) > 0 && !contains(var.ssh_source_cidrs, "0.0.0.0/0")
    error_message = "Provide at least one specific admin CIDR and do not use 0.0.0.0/0 for SSH."
  }
}

variable "master_tcp_port" {
  description = "Public TCP port for the master (MSP + TLS)."
  type        = number
  default     = 27000
}

variable "game_udp_ports" {
  description = "Public UDP ports for the game servers. One per compose game-server instance."
  type        = list(number)
  default     = [27015, 27016]
}

variable "os_disk_size_gb" {
  description = "OS disk size. Holds the database, backups, durability CSV, TLS material and container images."
  type        = number
  default     = 64
}

variable "os_disk_type" {
  description = "OS managed disk type."
  type        = string
  default     = "StandardSSD_LRS"
}

variable "ubuntu_image" {
  description = "Ubuntu LTS marketplace image. Pin the SKU; 'latest' version is acceptable for a fresh deploy."
  type = object({
    publisher = string
    offer     = string
    sku       = string
    version   = string
  })
  default = {
    publisher = "Canonical"
    offer     = "ubuntu-24_04-lts"
    sku       = "server"
    version   = "latest"
  }
}

variable "dns_hostname" {
  description = <<-EOT
    The hostname clients will use and the master certificate will be issued for (e.g.
    master.example.com). Terraform does NOT create DNS records — it outputs the static IP
    so you point your domain's A record at it manually. Used here only for outputs and the
    cloud-init bootstrap note.
  EOT
  type        = string
  default     = ""
}

variable "backup_container_name" {
  description = "Blob container for off-host database backups."
  type        = string
  default     = "db-backups"
}

variable "backup_retention_days" {
  description = "Delete blob backups older than this (lifecycle policy). Match IRONFRONT_BACKUP_RETENTION_DAYS or exceed it."
  type        = number
  default     = 30
}

variable "repo_clone_url" {
  description = <<-EOT
    Optional https git URL of THIS repository. If set AND public, cloud-init clones it and
    installs compose.yaml, the tools and the systemd units automatically. Leave empty for a
    private repo — cloud-init then only bootstraps the machine and writes BOOTSTRAP.md with
    the manual delivery steps (no token is ever placed in user-data).
  EOT
  type        = string
  default     = ""
}

variable "tags" {
  description = "Tags applied to every resource."
  type        = map(string)
  default = {
    project = "ironfront"
    phase   = "03"
    managed = "terraform"
  }
}
