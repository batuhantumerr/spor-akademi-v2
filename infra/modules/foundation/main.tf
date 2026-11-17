data "azurerm_client_config" "current" {}

# Short random suffix: Key Vault and storage account names are global.
resource "random_string" "suffix" {
  length  = 5
  lower   = true
  upper   = false
  numeric = true
  special = false
}

resource "azurerm_resource_group" "this" {
  name     = "rg-${var.name}"
  location = var.location
  tags     = var.tags
}

# --- Observability -------------------------------------------------------------------------------------------
# Container Apps writes every container's stdout (the API's JSON logs) here. The workspace-based Application
# Insights is created for the Azure Monitor tooling; the API exports OTLP only (docs/gozlemlenebilirlik.md), so
# it receives data once an OTLP collector with the Azure Monitor exporter is deployed (docs/bulut-altyapisi.md).
resource "azurerm_log_analytics_workspace" "this" {
  name                = "log-${var.name}"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  sku                 = "PerGB2018"
  retention_in_days   = var.log_retention_days
  daily_quota_gb      = var.log_daily_quota_gb
  tags                = var.tags
}

resource "azurerm_application_insights" "this" {
  name                = "appi-${var.name}"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  workspace_id        = azurerm_log_analytics_workspace.this.id
  application_type    = "web"
  tags                = var.tags
}

# --- Identity and secrets ------------------------------------------------------------------------------------
# One user-assigned identity for every container app and job: it reads secrets from Key Vault and (later,
# P5-06) the Blob container. It exists before the apps, so role assignments are in place when they start.
resource "azurerm_user_assigned_identity" "apps" {
  name                = "id-${var.name}-apps"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  tags                = var.tags
}

resource "azurerm_key_vault" "this" {
  name                          = "kv-${replace(var.name, "-", "")}-${random_string.suffix.result}"
  location                      = azurerm_resource_group.this.location
  resource_group_name           = azurerm_resource_group.this.name
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true
  purge_protection_enabled      = var.key_vault_purge_protection
  soft_delete_retention_days    = var.key_vault_soft_delete_days
  public_network_access_enabled = true # RBAC + Entra ID only; a private endpoint is the later hardening step
  tags                          = var.tags

  # Scanner finding AZU-0013 accepted (DEC-641): Container Apps reads Key Vault secrets from the platform, not from
  # this subnet, so default Deny would need a private endpoint (about 7 USD/month plus a DNS zone) and the owner's
  # workstation would need an IP rule for every apply. Access is RBAC + Entra ID only, no access policies, no keys.
  # Revisit with the private endpoint when the budget allows (docs/bulut-altyapisi.md).
  network_acls {
    #trivy:ignore:AZU-0013
    default_action = "Allow"
    bypass         = "AzureServices"
  }
}

resource "azurerm_role_assignment" "apps_read_secrets" {
  scope                = azurerm_key_vault.this.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.apps.principal_id
}

# Whoever runs tofu writes the generated database administrator password into the vault (write-only, never in state).
resource "azurerm_role_assignment" "deployer_write_secrets" {
  scope                = azurerm_key_vault.this.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

# --- Network -------------------------------------------------------------------------------------------------
# Private access: PostgreSQL has no public endpoint, only the Container Apps environment's subnet reaches it.
resource "azurerm_virtual_network" "this" {
  name                = "vnet-${var.name}"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  address_space       = [var.vnet_address_space]
  tags                = var.tags
}

resource "azurerm_subnet" "apps" {
  name                 = "snet-apps"
  resource_group_name  = azurerm_resource_group.this.name
  virtual_network_name = azurerm_virtual_network.this.name
  address_prefixes     = [cidrsubnet(var.vnet_address_space, 8, 0)] # /24, Container Apps needs at least /27

  # The RabbitMQ share's firewall lets this subnet in.
  service_endpoint {
    service = "Microsoft.Storage"
  }

  delegation {
    name = "container-apps"
    service_delegation {
      name    = "Microsoft.App/environments"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

resource "azurerm_subnet" "postgres" {
  name                 = "snet-postgres"
  resource_group_name  = azurerm_resource_group.this.name
  virtual_network_name = azurerm_virtual_network.this.name
  address_prefixes     = [cidrsubnet(var.vnet_address_space, 8, 1)]

  delegation {
    name = "postgres"
    service_delegation {
      name    = "Microsoft.DBforPostgreSQL/flexibleServers"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

resource "azurerm_private_dns_zone" "postgres" {
  name                = "${var.name}.postgres.database.azure.com"
  resource_group_name = azurerm_resource_group.this.name
  tags                = var.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "postgres" {
  name                = "postgres"
  private_dns_zone_id = azurerm_private_dns_zone.postgres.id
  virtual_network_id  = azurerm_virtual_network.this.id
  tags                = var.tags
}
