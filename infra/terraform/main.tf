# Ironfront single-VM baseline (phase 03 task 4).
#
# One resource group holding: VNet/subnet, an NSG that opens ONLY the ports the game needs,
# a static public IP, an Ubuntu VM with a system-assigned identity, and a private Blob
# container for off-host backups the VM writes with that identity (no key, no SAS).
#
# This is process/capacity resilience, not high availability. A VM/disk/region failure is
# a full outage — say so in the report.

data "azurerm_client_config" "current" {}

# Storage account names are global and must be 3-24 lowercase alphanumerics.
resource "random_string" "sa_suffix" {
  length  = 6
  lower   = true
  upper   = false
  numeric = true
  special = false
}

resource "azurerm_resource_group" "main" {
  name     = "${var.name_prefix}-rg"
  location = var.location
  tags     = var.tags
}

# ---------------------------------------------------------------------------
# Network
# ---------------------------------------------------------------------------
resource "azurerm_virtual_network" "main" {
  name                = "${var.name_prefix}-vnet"
  address_space       = ["10.42.0.0/24"]
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = var.tags
}

resource "azurerm_subnet" "main" {
  name                 = "${var.name_prefix}-subnet"
  resource_group_name  = azurerm_resource_group.main.name
  virtual_network_name = azurerm_virtual_network.main.name
  address_prefixes     = ["10.42.0.0/26"]
}

resource "azurerm_network_security_group" "main" {
  name                = "${var.name_prefix}-nsg"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = var.tags

  # SSH — admin CIDRs only. The variable validation forbids 0.0.0.0/0.
  security_rule {
    name                       = "allow-ssh"
    priority                   = 100
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "22"
    source_address_prefixes    = var.ssh_source_cidrs
    destination_address_prefix = "*"
  }

  # Master: MSP + TLS, public.
  security_rule {
    name                       = "allow-master-tcp"
    priority                   = 110
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = tostring(var.master_tcp_port)
    source_address_prefix      = "Internet"
    destination_address_prefix = "*"
  }

  # Game servers: UDP data plane, public.
  security_rule {
    name                       = "allow-game-udp"
    priority                   = 120
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Udp"
    source_port_range          = "*"
    destination_port_ranges    = [for p in var.game_udp_ports : tostring(p)]
    source_address_prefix      = "Internet"
    destination_address_prefix = "*"
  }

  # ACME HTTP-01, public. Nothing listens on 80 except certbot --standalone, and only for
  # the seconds it takes to answer a challenge. The rule exists because the alternative is
  # worse: DNS-01 needs a TXT record, and the deployment's hostname is a wildcard-DNS name
  # (nip.io) whose zone nobody here can edit. With 80 permanently reachable,
  # `certbot renew` is unattended; with it opened by hand per issuance, a renewal that
  # nobody remembers is a certificate that expires mid-demo.
  dynamic "security_rule" {
    for_each = var.acme_http_enabled ? [1] : []
    content {
      name                       = "allow-acme-http"
      priority                   = 130
      direction                  = "Inbound"
      access                     = "Allow"
      protocol                   = "Tcp"
      source_port_range          = "*"
      destination_port_range     = "80"
      source_address_prefix      = "Internet"
      destination_address_prefix = "*"
    }
  }

  # There is deliberately NO rule for 27001 (metrics). It is unauthenticated and binds to
  # the host loopback only; operators reach it over SSH. Azure's default inbound-deny rule
  # keeps it — and everything else — closed.
}

resource "azurerm_subnet_network_security_group_association" "main" {
  subnet_id                 = azurerm_subnet.main.id
  network_security_group_id = azurerm_network_security_group.main.id
}

resource "azurerm_public_ip" "main" {
  name                = "${var.name_prefix}-pip"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  # Static + Standard: the IP must not change under the DNS A record, and a Standard IP is
  # secure-by-default (no inbound except what the NSG allows).
  allocation_method = "Static"
  sku               = "Standard"
  tags              = var.tags
}

resource "azurerm_network_interface" "main" {
  name                = "${var.name_prefix}-nic"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = var.tags

  ip_configuration {
    name                          = "primary"
    subnet_id                     = azurerm_subnet.main.id
    private_ip_address_allocation = "Dynamic"
    public_ip_address_id          = azurerm_public_ip.main.id
  }
}

# ---------------------------------------------------------------------------
# Virtual machine
# ---------------------------------------------------------------------------
resource "azurerm_linux_virtual_machine" "main" {
  name                  = "${var.name_prefix}-vm"
  location              = azurerm_resource_group.main.location
  resource_group_name   = azurerm_resource_group.main.name
  size                  = var.vm_size
  admin_username        = var.admin_username
  network_interface_ids = [azurerm_network_interface.main.id]
  tags                  = var.tags

  # SSH key only. Password auth stays off, so a weak/guessable password is impossible.
  disable_password_authentication = true

  admin_ssh_key {
    username   = var.admin_username
    public_key = var.admin_ssh_public_key
  }

  os_disk {
    caching              = "ReadWrite"
    storage_account_type = var.os_disk_type
    disk_size_gb         = var.os_disk_size_gb
  }

  source_image_reference {
    publisher = var.ubuntu_image.publisher
    offer     = var.ubuntu_image.offer
    sku       = var.ubuntu_image.sku
    version   = var.ubuntu_image.version
  }

  # The managed identity the backup uploader authenticates as. No key or SAS anywhere.
  identity {
    type = "SystemAssigned"
  }

  # Machine bootstrap only — installs Docker/Compose/az/certbot, opens the host firewall,
  # creates the directory tree. It writes NO secret; the .env, the shared secret and the
  # certificate are placed on the box out of band afterwards.
  custom_data = base64encode(templatefile("${path.module}/cloud-init.yaml", {
    admin_username = var.admin_username
    master_port    = var.master_tcp_port
    udp_ports      = join(" ", [for p in var.game_udp_ports : tostring(p)])
    repo_clone_url = var.repo_clone_url
    dns_hostname   = var.dns_hostname
    # A whole line, not a flag, so the rendered script has no dead `if` in it when ACME
    # HTTP-01 is off. ufw and the NSG have to agree — opening one without the other is the
    # failure that reads as "certbot just hangs".
    acme_http_ufw_rule = var.acme_http_enabled ? "ufw allow 80/tcp" : "# 80/tcp closed (acme_http_enabled = false)"
  }))
}

# ---------------------------------------------------------------------------
# Off-host backups: private Blob, written by the VM identity over Entra ID
# ---------------------------------------------------------------------------
resource "azurerm_storage_account" "backups" {
  name                     = "${var.name_prefix}${random_string.sa_suffix.result}"
  resource_group_name      = azurerm_resource_group.main.name
  location                 = azurerm_resource_group.main.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"
  min_tls_version          = "TLS1_2"

  # The standing rule: no plaintext secret in Terraform state. With key auth disabled the
  # account has no key to read into state — every access, Terraform's and the VM's, is over
  # Entra ID. Data is encrypted at rest with Microsoft-managed keys by default.
  shared_access_key_enabled = false

  blob_properties {
    versioning_enabled = true
    delete_retention_policy {
      days = var.backup_retention_days
    }
  }

  tags = var.tags
}

# Terraform itself needs the data-plane role to create the container over AAD (key auth is
# off). Scoped to this one account.
resource "azurerm_role_assignment" "deployer_blob" {
  scope                = azurerm_storage_account.backups.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = data.azurerm_client_config.current.object_id
}

# RBAC is eventually consistent; give the assignment a moment before the container create
# tries to use it, or the first apply flakes with an authorization error.
resource "time_sleep" "wait_for_rbac" {
  depends_on      = [azurerm_role_assignment.deployer_blob]
  create_duration = "60s"
}

resource "azurerm_storage_container" "backups" {
  name                  = var.backup_container_name
  storage_account_name  = azurerm_storage_account.backups.name
  container_access_type = "private"

  depends_on = [time_sleep.wait_for_rbac]
}

# The VM's identity gets the same role, scoped as tightly as possible — the backup
# container, not the whole account.
resource "azurerm_role_assignment" "vm_blob" {
  scope                = "${azurerm_storage_account.backups.id}/blobServices/default/containers/${azurerm_storage_container.backups.name}"
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_linux_virtual_machine.main.identity[0].principal_id
}

# Off-host retention: delete backup blobs (and old versions) past the window. The local
# retention in tools/backup.sh is separate and shorter; this bounds the cloud copy.
resource "azurerm_storage_management_policy" "backups" {
  storage_account_id = azurerm_storage_account.backups.id

  rule {
    name    = "expire-old-backups"
    enabled = true
    filters {
      blob_types = ["blockBlob"]
    }
    actions {
      base_blob {
        delete_after_days_since_modification_greater_than = var.backup_retention_days
      }
      version {
        delete_after_days_since_creation = var.backup_retention_days
      }
    }
  }
}
