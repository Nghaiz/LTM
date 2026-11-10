# Inputs for the Ironfront game server on EC2. Nothing here is a secret.

variable "region" {
  description = "AWS region. Default ap-southeast-2 (Sydney) to match the master server."
  type        = string
  default     = "ap-southeast-2"
}

variable "name" {
  description = "Name prefix for game server resources."
  type        = string
  default     = "ironfront-game"
}

variable "master_name" {
  description = "Name of the master server stack used to discover VPC, subnet, and master EIP if not explicitly provided."
  type        = string
  default     = "ironfront-master"
}

variable "instance_type" {
  description = <<-EOT
    2 vCPU / 8 GiB. x86_64 amd64. m7i-flex.large matches master and is free-tier eligible in this account.
  EOT
  type        = string
  default     = "m7i-flex.large"
}

variable "root_volume_gb" {
  description = "Root gp3 volume: OS, Docker images, logs."
  type        = number
  default     = 30
}

variable "gameserver_image" {
  description = "The game server Docker image."
  type        = string
  default     = "ghcr.io/nghaiz/ironfront-game-server:latest"
}

variable "master_host" {
  description = <<-EOT
    Hostname of the master server. If empty, automatically derived from the master's Elastic IP
    as <ip-with-dashes>.sslip.io.
  EOT
  type        = string
  default     = ""
}

variable "master_port" {
  description = "Port the master server listens on for game server registration (TLS)."
  type        = number
  default     = 443
}

variable "shared_secret_parameter" {
  description = "SSM Parameter Store name holding IRONFRONT_SHARED_SECRET."
  type        = string
  default     = "/ironfront/shared-secret"
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

variable "game_ports" {
  description = "UDP ports opened for game servers (Dustbowl: 27015, Island: 27016, Forest Lake: 27017)."
  type        = list(number)
  default     = [27015, 27016, 27017]
}
