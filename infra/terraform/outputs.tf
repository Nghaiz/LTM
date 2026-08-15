# Outputs for the Ironfront single-VM baseline (phase 03 task 4).
#
# Deliberately no secrets here: the shared secret, TLS password and GHCR credential never
# pass through Terraform, so there is nothing sensitive to emit. These outputs are the
# hand-off to the manual DNS + TLS + compose steps in the README.

output "public_ip_address" {
  description = "Static public IP of the VM. Point your domain's A record here."
  value       = azurerm_public_ip.main.ip_address
}

output "dns_a_record_reminder" {
  description = "The DNS record you must create by hand — Terraform does not manage DNS."
  value       = var.dns_hostname != "" ? "Create an A record: ${var.dns_hostname} -> ${azurerm_public_ip.main.ip_address}" : "Set var.dns_hostname, then create an A record for it -> ${azurerm_public_ip.main.ip_address}"
}

output "ssh_command" {
  description = "SSH in as the admin user (only from an IP inside ssh_source_cidrs)."
  value       = "ssh ${var.admin_username}@${azurerm_public_ip.main.ip_address}"
}

output "resource_group_name" {
  description = "Resource group holding every resource in this deployment."
  value       = azurerm_resource_group.main.name
}

output "backup_storage_account_name" {
  description = "Storage account for off-host DB backups. Set IRONFRONT_BACKUP_BLOB_ACCOUNT to this."
  value       = azurerm_storage_account.backups.name
}

output "backup_container_name" {
  description = "Blob container for backups. Set IRONFRONT_BACKUP_BLOB_CONTAINER to this."
  value       = azurerm_storage_container.backups.name
}

output "public_endpoints" {
  description = "What is reachable from the Internet, for the external-exposure check."
  value = {
    master_tcp = "${azurerm_public_ip.main.ip_address}:${var.master_tcp_port} (TLS)"
    game_udp   = [for p in var.game_udp_ports : "${azurerm_public_ip.main.ip_address}:${p}/udp"]
    metrics    = "NOT exposed (27001 is loopback-only; reach it over SSH)"
  }
}
