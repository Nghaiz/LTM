output "instance_id" {
  description = "EC2 instance id of the game server."
  value       = aws_instance.game.id
}

output "public_ip" {
  description = "Elastic IP of the game server."
  value       = aws_eip.game.public_ip
}

output "advertised_ip" {
  description = "IP address advertised to master and connecting players."
  value       = aws_eip.game.public_ip
}

output "game_endpoints" {
  description = "UDP endpoints where game servers listen."
  value       = [for p in var.game_ports : "${aws_eip.game.public_ip}:${p}"]
}

output "master_host" {
  description = "Hostname of the master server this game server registers with."
  value       = local.master_host
}

output "region" {
  value = var.region
}

output "session_command" {
  description = "Shell on the instance via SSM Session Manager (no SSH required)."
  value       = "aws ssm start-session --region ${var.region} --target ${aws_instance.game.id}"
}
