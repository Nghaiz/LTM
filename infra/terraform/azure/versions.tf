# Provider and version pins for the Ironfront single-VM baseline (phase 03 task 4).
#
# azurerm is pinned to 3.x on purpose: 4.x makes subscription_id a required provider
# argument, whereas 3.x uses the `az login` context and the account's default
# subscription, which is one less thing for a student deployment to get wrong. Bump this
# deliberately, not incidentally.

terraform {
  required_version = ">= 1.5.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.116"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
    # Used only for a short pause after granting the deployer its data-plane role, so the
    # RBAC assignment has propagated before the storage container is created over AAD.
    time = {
      source  = "hashicorp/time"
      version = "~> 0.12"
    }
  }

  # Remote state is STRONGLY recommended: this configuration disables storage account key
  # auth so no key ever lands in state, but state still holds resource IDs and the VM's
  # identity details, and local state on a laptop is easy to lose or leak. Configure a
  # backend by copying backend.tf.example to backend.tf and filling it in — it is a
  # template, NOT credentials, and `az login` provides the auth.
  #
  # backend "azurerm" {}
}

provider "azurerm" {
  features {}

  # Authenticate over Entra ID for the storage DATA plane too, so Terraform creates the
  # backup container without ever reading an account key. Pairs with
  # shared_access_key_enabled = false on the account (see main.tf) and the standing rule:
  # no plaintext secrets in Terraform state.
  storage_use_azuread = true
}
