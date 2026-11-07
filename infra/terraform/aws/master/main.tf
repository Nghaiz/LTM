# Ironfront master server on one EC2 instance — the replacement for the fly.io app.
#
#   VPC + public subnet ─ EC2 (Ubuntu 24.04, Docker) ─ Elastic IP ─ <ip>.sslip.io
#                                │
#                                ├─ SSM Parameter Store: the shared secret (read at boot)
#                                └─ S3: daily database backups (and the fly → EC2 migration)
#
# The instance bootstraps itself and runs the master with no operator step: cloud-init installs
# Docker, then ironfront-master.service fetches the secret, issues a Let's Encrypt certificate over
# HTTP-01, restores the newest backup if there is no database yet, and starts the container.
# Everything it runs is in infra/terraform/aws/master/files/.
#
# Not high availability: one instance, one AZ. A lost instance is rebuilt by `terraform apply` and
# restores the newest S3 backup on its first boot.

data "aws_caller_identity" "current" {}

data "aws_availability_zones" "available" {
  state = "available"
}

# Canonical's own pointer to the current Ubuntu 24.04 amd64 AMI. ignore_changes below keeps a newer
# AMI from replacing a running server on a later apply.
data "aws_ssm_parameter" "ubuntu_ami" {
  name = "/aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id"
}

locals {
  domain = var.master_domain != "" ? var.master_domain : "${replace(aws_eip.master.public_ip, ".", "-")}.sslip.io"
}

# ---------------------------------------------------------------------------
# Network
# ---------------------------------------------------------------------------
resource "aws_vpc" "main" {
  cidr_block           = "10.43.0.0/24"
  enable_dns_support   = true
  enable_dns_hostnames = true
  tags                 = { Name = var.name }
}

resource "aws_internet_gateway" "main" {
  vpc_id = aws_vpc.main.id
  tags   = { Name = var.name }
}

resource "aws_subnet" "public" {
  vpc_id                  = aws_vpc.main.id
  cidr_block              = "10.43.0.0/26"
  availability_zone       = data.aws_availability_zones.available.names[0]
  map_public_ip_on_launch = true
  tags                    = { Name = "${var.name}-public" }
}

resource "aws_route_table" "public" {
  vpc_id = aws_vpc.main.id

  route {
    cidr_block = "0.0.0.0/0"
    gateway_id = aws_internet_gateway.main.id
  }

  tags = { Name = "${var.name}-public" }
}

resource "aws_route_table_association" "public" {
  subnet_id      = aws_subnet.public.id
  route_table_id = aws_route_table.public.id
}

resource "aws_security_group" "master" {
  name        = var.name
  description = "Ironfront master: MSP over TLS on 27000 and 443, ACME HTTP-01 on 80"
  vpc_id      = aws_vpc.main.id
  tags        = { Name = var.name }
}

# 27000 is the documented MSP port. 443 is the same listener: released clients and the game servers
# dial 443 today (fly's shared IPv4 only routed 80/443), and 443 is the port a school or cafe
# firewall is least likely to block.
resource "aws_vpc_security_group_ingress_rule" "msp" {
  for_each          = toset(["27000", "443"])
  security_group_id = aws_security_group.master.id
  description       = "MSP (TLS) on ${each.key}"
  ip_protocol       = "tcp"
  from_port         = tonumber(each.key)
  to_port           = tonumber(each.key)
  cidr_ipv4         = "0.0.0.0/0"
}

# Let's Encrypt HTTP-01. Only certbot --standalone listens here, for the seconds of a challenge;
# keeping it open is what lets `certbot renew` run unattended.
resource "aws_vpc_security_group_ingress_rule" "acme" {
  security_group_id = aws_security_group.master.id
  description       = "ACME HTTP-01"
  ip_protocol       = "tcp"
  from_port         = 80
  to_port           = 80
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_ingress_rule" "ssh" {
  for_each          = var.ssh_public_key != "" ? toset(var.ssh_cidrs) : toset([])
  security_group_id = aws_security_group.master.id
  description       = "SSH (admin)"
  ip_protocol       = "tcp"
  from_port         = 22
  to_port           = 22
  cidr_ipv4         = each.value
}

# Metrics (27001) has no rule on purpose: unauthenticated, bound to the host loopback. Read it over
# SSM Session Manager: `nc 127.0.0.1 27001`.

resource "aws_vpc_security_group_egress_rule" "all" {
  security_group_id = aws_security_group.master.id
  description       = "Outbound: GHCR, Lets Encrypt, SSM, S3, apt"
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
}

# Allocated before the instance exists, so its address can be baked into the bootstrap config (the
# sslip.io hostname is derived from it) without a dependency cycle.
resource "aws_eip" "master" {
  domain = "vpc"
  tags   = { Name = var.name }
}

resource "aws_eip_association" "master" {
  instance_id   = aws_instance.master.id
  allocation_id = aws_eip.master.id
}

# ---------------------------------------------------------------------------
# Backups bucket
# ---------------------------------------------------------------------------
resource "aws_s3_bucket" "backups" {
  bucket_prefix = "${var.name}-backups-"
}

resource "aws_s3_bucket_public_access_block" "backups" {
  bucket                  = aws_s3_bucket.backups.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_ownership_controls" "backups" {
  bucket = aws_s3_bucket.backups.id
  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_s3_bucket_versioning" "backups" {
  bucket = aws_s3_bucket.backups.id
  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "backups" {
  bucket = aws_s3_bucket.backups.id
  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "backups" {
  bucket = aws_s3_bucket.backups.id

  rule {
    id     = "expire-backups"
    status = "Enabled"
    filter {
      prefix = "backups/"
    }
    expiration {
      days = var.backup_retention_days
    }
    noncurrent_version_expiration {
      noncurrent_days = 7
    }
  }
}

# ---------------------------------------------------------------------------
# Instance identity: SSM (Session Manager + Run Command), the secret, the bucket
# ---------------------------------------------------------------------------
data "aws_iam_policy_document" "ec2_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ec2.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "master" {
  name               = var.name
  assume_role_policy = data.aws_iam_policy_document.ec2_assume.json
}

resource "aws_iam_role_policy_attachment" "ssm_core" {
  role       = aws_iam_role.master.name
  policy_arn = "arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore"
}

data "aws_iam_policy_document" "master" {
  # The shared secret. A SecureString under the AWS-managed aws/ssm key, whose key policy already
  # lets account principals decrypt through SSM, so no kms statement is needed.
  statement {
    sid       = "ReadSharedSecret"
    actions   = ["ssm:GetParameter"]
    resources = ["arn:aws:ssm:${var.region}:${data.aws_caller_identity.current.account_id}:parameter${var.shared_secret_parameter}"]
  }

  statement {
    sid       = "ListBackups"
    actions   = ["s3:ListBucket"]
    resources = [aws_s3_bucket.backups.arn]
  }

  statement {
    sid       = "ReadWriteBackups"
    actions   = ["s3:GetObject", "s3:PutObject"]
    resources = ["${aws_s3_bucket.backups.arn}/backups/*"]
  }
}

resource "aws_iam_role_policy" "master" {
  name   = "${var.name}-runtime"
  role   = aws_iam_role.master.id
  policy = data.aws_iam_policy_document.master.json
}

resource "aws_iam_instance_profile" "master" {
  name = var.name
  role = aws_iam_role.master.name
}

resource "aws_key_pair" "admin" {
  count      = var.ssh_public_key != "" ? 1 : 0
  key_name   = var.name
  public_key = var.ssh_public_key
}

# ---------------------------------------------------------------------------
# The instance
# ---------------------------------------------------------------------------
locals {
  # Non-secret runtime config the on-box scripts read. Written once, at first boot.
  aws_env = <<-EOT
    AWS_REGION=${var.region}
    AWS_DEFAULT_REGION=${var.region}
    IRONFRONT_DOMAIN=${local.domain}
    IRONFRONT_EXPECTED_IP=${aws_eip.master.public_ip}
    IRONFRONT_BACKUP_BUCKET=${aws_s3_bucket.backups.id}
    IRONFRONT_SECRET_PARAM=${var.shared_secret_parameter}
    IRONFRONT_ACME_EMAIL=${var.acme_email}
  EOT

  scripts = ["ironfront-bootstrap.sh", "ironfront-deploy.sh", "ironfront-backup.sh", "ironfront-restore.sh", "ironfront-renew-hook.sh"]
  units   = ["ironfront-master.service", "ironfront-backup.service", "ironfront-backup.timer"]

  # Built with yamlencode rather than a templated YAML file, so the scripts are embedded verbatim
  # and nothing in them is ever mistaken for a Terraform interpolation.
  cloud_config = {
    package_update = true
    packages       = ["ca-certificates", "curl", "jq", "sqlite3", "certbot", "openssl", "netcat-openbsd"]
    write_files = concat(
      [
        { path = "/opt/ironfront/aws.env", permissions = "0644", content = local.aws_env },
        { path = "/opt/ironfront/master.env", permissions = "0644", content = "IRONFRONT_MASTER_IMAGE=${var.master_image}\n" },
        { path = "/opt/ironfront/compose.yaml", permissions = "0644", content = file("${path.module}/files/compose.yaml") },
        {
          path        = "/etc/docker/daemon.json"
          permissions = "0644"
          content     = jsonencode({ "log-driver" = "json-file", "log-opts" = { "max-size" = "20m", "max-file" = "5" } })
        },
      ],
      [for s in local.scripts : { path = "/opt/ironfront/bin/${s}", permissions = "0755", content = file("${path.module}/files/${s}") }],
      [for u in local.units : { path = "/etc/systemd/system/${u}", permissions = "0644", content = file("${path.module}/files/${u}") }],
    )
    runcmd = [
      "set -eux",
      "curl -fsSL https://get.docker.com -o /tmp/get-docker.sh && sh /tmp/get-docker.sh",
      "snap install aws-cli --classic",
      "systemctl enable --now docker",
      "systemctl daemon-reload",
      "systemctl enable ironfront-master.service ironfront-backup.timer",
      "systemctl start ironfront-backup.timer",
      # --no-block: the bootstrap can wait minutes (secret, Elastic IP, certificate); cloud-init
      # must not sit on it. Progress: journalctl -u ironfront-master -f
      "systemctl start --no-block ironfront-master.service",
    ]
  }
}

resource "aws_instance" "master" {
  ami                    = data.aws_ssm_parameter.ubuntu_ami.value
  instance_type          = var.instance_type
  subnet_id              = aws_subnet.public.id
  vpc_security_group_ids = [aws_security_group.master.id]
  iam_instance_profile   = aws_iam_instance_profile.master.name
  key_name               = var.ssh_public_key != "" ? aws_key_pair.admin[0].key_name : null

  user_data_base64 = base64gzip("#cloud-config\n${yamlencode(local.cloud_config)}")

  # IMDSv2 only; hop limit 1 keeps the containers (one more hop) away from the instance role.
  metadata_options {
    http_endpoint               = "enabled"
    http_tokens                 = "required"
    http_put_response_hop_limit = 1
  }

  root_block_device {
    volume_type           = "gp3"
    volume_size           = var.root_volume_gb
    encrypted             = true
    delete_on_termination = true
  }

  tags = { Name = var.name }

  lifecycle {
    # The database lives on this instance. A newer Ubuntu AMI or an edited bootstrap must not
    # silently replace it; replace deliberately with `terraform apply -replace=aws_instance.master`
    # (the new instance restores the newest S3 backup on first boot).
    ignore_changes = [ami, user_data, user_data_base64]
  }
}
