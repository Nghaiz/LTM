output "instance_id" {
  description = "EC2 instance id: target of deploy.sh and of `aws ssm start-session`."
  value       = aws_instance.master.id
}

output "public_ip" {
  description = "Elastic IP of the master."
  value       = aws_eip.master.public_ip
}

output "master_host" {
  description = "Hostname clients and game servers dial (TLS, cert name = this host)."
  value       = local.domain
}

output "master_endpoints" {
  description = "Where the master listens. Both ports are the same TLS listener."
  value       = ["${local.domain}:443", "${local.domain}:27000"]
}

output "backup_bucket" {
  description = "S3 bucket holding the daily database backups (prefix backups/)."
  value       = aws_s3_bucket.backups.id
}

output "region" {
  value = var.region
}

output "session_command" {
  description = "Shell on the instance without SSH (needs the Session Manager plugin)."
  value       = "aws ssm start-session --region ${var.region} --target ${aws_instance.master.id}"
}

output "vpc_id" {
  description = "VPC ID where master runs."
  value       = aws_vpc.main.id
}

output "subnet_id" {
  description = "Public subnet ID where master runs."
  value       = aws_subnet.public.id
}

