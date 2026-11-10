# Ironfront game servers on one EC2 instance — registered with the master server.
#
#   Master VPC + public subnet ─ EC2 (Ubuntu 24.04, Docker) ─ Elastic IP
#                                │
#                                └─ SSM Parameter Store: reads the shared secret at boot

data "aws_caller_identity" "current" {}

# Canonical's Ubuntu 24.04 amd64 AMI pointer.
data "aws_ssm_parameter" "ubuntu_ami" {
  name = "/aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id"
}

# Discover the VPC and public subnet created by the master stack.
data "aws_vpc" "main" {
  filter {
    name   = "tag:Name"
    values = [var.master_name]
  }
}

data "aws_subnet" "public" {
  filter {
    name   = "tag:Name"
    values = ["${var.master_name}-public"]
  }
}

# Discover the master server's Elastic IP to compute the default master hostname.
data "aws_eip" "master" {
  tags = {
    Name = var.master_name
  }
}

locals {
  master_host = var.master_host != "" ? var.master_host : "${replace(data.aws_eip.master.public_ip, ".", "-")}.sslip.io"
}

# ---------------------------------------------------------------------------
# Network & Security
# ---------------------------------------------------------------------------
resource "aws_security_group" "game" {
  name        = var.name
  description = "Ironfront game server: UDP 27015-27017 for gameplay, SSH (admin)"
  vpc_id      = data.aws_vpc.main.id
  tags        = { Name = var.name }
}

resource "aws_vpc_security_group_ingress_rule" "game_udp" {
  for_each          = toset([for p in var.game_ports : tostring(p)])
  security_group_id = aws_security_group.game.id
  description       = "Game server UDP on ${each.key}"
  ip_protocol       = "udp"
  from_port         = tonumber(each.key)
  to_port           = tonumber(each.key)
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_ingress_rule" "ssh" {
  for_each          = var.ssh_public_key != "" ? toset(var.ssh_cidrs) : toset([])
  security_group_id = aws_security_group.game.id
  description       = "SSH (admin)"
  ip_protocol       = "tcp"
  from_port         = 22
  to_port           = 22
  cidr_ipv4         = each.value
}

resource "aws_vpc_security_group_egress_rule" "all" {
  security_group_id = aws_security_group.game.id
  description       = "Outbound: GHCR, master server, SSM, apt"
  ip_protocol       = "-1"
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_eip" "game" {
  domain = "vpc"
  tags   = { Name = var.name }
}

resource "aws_eip_association" "game" {
  instance_id   = aws_instance.game.id
  allocation_id = aws_eip.game.id
}

# ---------------------------------------------------------------------------
# IAM: SSM Session Manager + read shared secret
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

resource "aws_iam_role" "game" {
  name               = var.name
  assume_role_policy = data.aws_iam_policy_document.ec2_assume.json
}

resource "aws_iam_role_policy_attachment" "ssm_core" {
  role       = aws_iam_role.game.name
  policy_arn = "arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore"
}

data "aws_iam_policy_document" "game" {
  statement {
    sid       = "ReadSharedSecret"
    actions   = ["ssm:GetParameter"]
    resources = ["arn:aws:ssm:${var.region}:${data.aws_caller_identity.current.account_id}:parameter${var.shared_secret_parameter}"]
  }
}

resource "aws_iam_role_policy" "game" {
  name   = "${var.name}-runtime"
  role   = aws_iam_role.game.id
  policy = data.aws_iam_policy_document.game.json
}

resource "aws_iam_instance_profile" "game" {
  name = var.name
  role = aws_iam_role.game.name
}

resource "aws_key_pair" "admin" {
  count      = var.ssh_public_key != "" ? 1 : 0
  key_name   = var.name
  public_key = var.ssh_public_key
}

# ---------------------------------------------------------------------------
# The Instance
# ---------------------------------------------------------------------------
locals {
  aws_env = <<-EOT
    AWS_REGION=${var.region}
    AWS_DEFAULT_REGION=${var.region}
    IRONFRONT_SECRET_PARAM=${var.shared_secret_parameter}
    IRONFRONT_EXPECTED_IP=${aws_eip.game.public_ip}
    IRONFRONT_MASTER_HOST=${local.master_host}
    IRONFRONT_MASTER_PORT=${var.master_port}
  EOT

  cloud_config = {
    package_update = true
    packages       = ["ca-certificates", "curl", "jq", "netcat-openbsd"]
    write_files = [
      { path = "/opt/ironfront/aws.env", permissions = "0644", content = local.aws_env },
      { path = "/opt/ironfront/gameserver.env", permissions = "0644", content = "IRONFRONT_GAMESERVER_IMAGE=${var.gameserver_image}\n" },
      { path = "/opt/ironfront/gameservers.compose.yml", permissions = "0644", content = file("${path.module}/files/gameservers.compose.yml") },
      { path = "/opt/ironfront/bin/ironfront-gameserver-bootstrap.sh", permissions = "0755", content = file("${path.module}/files/ironfront-gameserver-bootstrap.sh") },
      { path = "/opt/ironfront/bin/ironfront-gameserver-deploy.sh", permissions = "0755", content = file("${path.module}/files/ironfront-gameserver-deploy.sh") },
      { path = "/etc/systemd/system/ironfront-gameserver.service", permissions = "0644", content = file("${path.module}/files/ironfront-gameserver.service") },
      {
        path        = "/etc/docker/daemon.json"
        permissions = "0644"
        content     = jsonencode({ "log-driver" = "json-file", "log-opts" = { "max-size" = "20m", "max-file" = "5" } })
      },
    ]
    runcmd = [
      "set -eux",
      "curl -fsSL https://get.docker.com -o /tmp/get-docker.sh && sh /tmp/get-docker.sh",
      "snap install aws-cli --classic",
      "systemctl enable --now docker",
      "systemctl daemon-reload",
      "systemctl enable ironfront-gameserver.service",
      "systemctl start --no-block ironfront-gameserver.service",
    ]
  }
}

resource "aws_instance" "game" {
  ami                    = data.aws_ssm_parameter.ubuntu_ami.value
  instance_type          = var.instance_type
  subnet_id              = data.aws_subnet.public.id
  vpc_security_group_ids = [aws_security_group.game.id]
  iam_instance_profile   = aws_iam_instance_profile.game.name
  key_name               = var.ssh_public_key != "" ? aws_key_pair.admin[0].key_name : null

  user_data_base64 = base64gzip("#cloud-config\n${yamlencode(local.cloud_config)}")

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
    ignore_changes = [ami, user_data, user_data_base64]
  }
}
