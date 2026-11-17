# --- PostgreSQL Flexible Server ------------------------------------------------------------------------------
# The administrator password is generated here as an EPHEMERAL value: it goes to the server and to Key Vault through
# write-only attributes and is never stored in state or plan files. The application roles (spor_akademi_migrator,
# spor_akademi_app, the backup roles) and their passwords are created by the owner with the same script the
# compose setup uses (docker/postgres/initdb/10-create-roles.sh); their connection strings are Key Vault secrets
# the owner writes (docs/bulut-altyapisi.md, first apply checklist).
ephemeral "random_password" "postgres_admin" {
  length           = 40
  special          = true
  override_special = "-_.~"
}

resource "azurerm_postgresql_flexible_server" "this" {
  name                = "psql-${var.name}-${var.suffix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  version             = var.postgres_version
  sku_name            = var.postgres_sku_name
  storage_mb          = var.postgres_storage_mb
  auto_grow_enabled   = false

  # Private access: reachable only from the virtual network (Container Apps subnet), no public endpoint, no firewall rules.
  delegated_subnet_id           = var.postgres_subnet_id
  private_dns_zone_id           = var.postgres_private_dns_zone_id
  public_network_access_enabled = false

  administrator_login               = "sa_admin"
  administrator_password_wo         = ephemeral.random_password.postgres_admin.result
  administrator_password_wo_version = var.postgres_admin_password_version

  # Platform backups with point-in-time restore replace the WAL archive scripts of docs/geri-yukleme-tatbikati.md.
  backup_retention_days        = var.postgres_backup_retention_days
  geo_redundant_backup_enabled = var.postgres_geo_redundant_backup

  tags = var.tags

  lifecycle {
    ignore_changes = [zone]
  }
}

resource "azurerm_postgresql_flexible_server_database" "app" {
  name      = "spor_akademi"
  server_id = azurerm_postgresql_flexible_server.this.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

# pg_stat_statements (docs/performans.md): allow-list the extension and preload it. The preload is a static
# parameter, so the server restarts once on the first apply. CREATE EXTENSION is run by the owner (checklist).
resource "azurerm_postgresql_flexible_server_configuration" "extensions" {
  name      = "azure.extensions"
  server_id = azurerm_postgresql_flexible_server.this.id
  value     = "PG_STAT_STATEMENTS"
}

resource "azurerm_postgresql_flexible_server_configuration" "preload" {
  name      = "shared_preload_libraries"
  server_id = azurerm_postgresql_flexible_server.this.id
  value     = "pg_stat_statements"

  depends_on = [azurerm_postgresql_flexible_server_configuration.extensions]
}

resource "azurerm_postgresql_flexible_server_configuration" "track" {
  name      = "pg_stat_statements.track"
  server_id = azurerm_postgresql_flexible_server.this.id
  value     = "top"

  depends_on = [azurerm_postgresql_flexible_server_configuration.preload]
}

# The role-creation script sets passwords with SQL; keep them out of the statement statistics and server log.
resource "azurerm_postgresql_flexible_server_configuration" "track_utility" {
  name      = "pg_stat_statements.track_utility"
  server_id = azurerm_postgresql_flexible_server.this.id
  value     = "off"

  depends_on = [azurerm_postgresql_flexible_server_configuration.preload]
}

resource "azurerm_key_vault_secret" "postgres_admin_password" {
  name             = "postgres-admin-password"
  key_vault_id     = var.key_vault_id
  value_wo         = ephemeral.random_password.postgres_admin.result
  value_wo_version = var.postgres_admin_password_version
  content_type     = "PostgreSQL administrator sa_admin; break-glass and role bootstrap only"
}

# --- Document storage (Azure Blob) ---------------------------------------------------------------------------
# P5-06 adds the Blob adapter. It authenticates with the apps' managed identity and signs short-lived user delegation
# SAS links, so account keys are switched off and nothing is public.
resource "azurerm_storage_account" "documents" {
  name                = "stdoc${replace(var.name, "-", "")}${var.suffix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  tags                = var.tags

  account_kind                    = "StorageV2"
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  https_traffic_only_enabled      = true
  allow_nested_items_to_be_public = false
  shared_access_key_enabled       = false
  default_to_oauth_authentication = true

  # Scanner finding AZU-0012 accepted (DEC-641): browsers download documents with short-lived user delegation SAS links
  # the API signs (P5-06), so the endpoint must be reachable from the internet. Protection: no anonymous access, no
  # account keys, TLS 1.2, Entra ID roles only, links of at most 15 minutes (Storage:DownloadLinkLifetime).
  network_rules {
    #trivy:ignore:AZU-0012
    default_action = "Allow"
    bypass         = ["AzureServices"]
  }

  blob_properties {
    versioning_enabled = true
    delete_retention_policy {
      days = var.blob_soft_delete_days
    }
    container_delete_retention_policy {
      days = var.blob_soft_delete_days
    }
  }
}

resource "azurerm_storage_container" "documents" {
  name                  = "documents"
  storage_account_id    = azurerm_storage_account.documents.id
  container_access_type = "private"
}

resource "azurerm_role_assignment" "apps_blob" {
  scope                = azurerm_storage_account.documents.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = var.apps_identity_principal_id
}

# --- RabbitMQ data (Azure Files) -----------------------------------------------------------------------------
# Container Apps mounts Azure Files with the account key (managed identity is not supported for the mount), so this
# account keeps shared keys on and holds nothing but the broker's data.
resource "azurerm_storage_account" "rabbitmq" {
  count               = var.enable_rabbitmq_storage ? 1 : 0
  name                = "strmq${replace(var.name, "-", "")}${var.suffix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  tags                = var.tags

  account_kind                    = "StorageV2"
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  https_traffic_only_enabled      = true
  allow_nested_items_to_be_public = false

  # Only the Container Apps subnet (service endpoint) reaches the broker's data.
  network_rules {
    default_action             = "Deny"
    bypass                     = ["AzureServices"]
    virtual_network_subnet_ids = [var.apps_subnet_id]
  }
}

resource "azurerm_storage_share" "rabbitmq" {
  count              = var.enable_rabbitmq_storage ? 1 : 0
  name               = "rabbitmq"
  storage_account_id = azurerm_storage_account.rabbitmq[0].id
  quota              = 5
}

# --- Cache (optional) ----------------------------------------------------------------------------------------
resource "azurerm_redis_cache" "this" {
  count                         = var.enable_redis ? 1 : 0
  name                          = "redis-${var.name}-${var.suffix}"
  location                      = var.location
  resource_group_name           = var.resource_group_name
  capacity                      = 0
  family                        = "C"
  sku_name                      = "Basic"
  non_ssl_port_enabled          = false
  minimum_tls_version           = "1.2"
  public_network_access_enabled = true # Basic cannot join a virtual network; password + TLS only
  tags                          = var.tags

  redis_configuration {
    maxmemory_policy = "allkeys-lru"
  }
}
