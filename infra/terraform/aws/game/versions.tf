# Ironfront game servers on AWS EC2 — provider pins.
# Auth is whatever the AWS CLI is logged in as (`aws login` / SSO / env vars); nothing here holds a credential.

terraform {
  required_version = ">= 1.5.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }

  # State is local (gitignored). It holds no secret — the shared secret lives only in SSM
  # Parameter Store and is never a Terraform input — but it is the only record of what was
  # created, so keep terraform.tfstate safe or move it to an S3 backend.
}

provider "aws" {
  region = var.region

  default_tags {
    tags = {
      project = "ironfront"
      role    = "gameserver"
      managed = "terraform"
    }
  }
}
