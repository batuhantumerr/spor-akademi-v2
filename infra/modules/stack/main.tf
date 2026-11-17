# One whole environment: foundation (resource group, observability, identity, Key Vault, network), data (PostgreSQL,
# Blob, RabbitMQ storage, optional Redis) and apps (Container Apps). The environments only differ in the values they pass.
locals {
  name = "spor-${var.environment}"
  tags = merge(var.tags, {
    application = "spor-akademi"
    environment = var.environment
    managed-by  = "opentofu"
    repository  = "spor-akademi-v2"
    data        = "personal-data" # children's and health data: ADR-0014
  })
}

module "foundation" {
  source = "../foundation"

  name                       = local.name
  location                   = var.location
  tags                       = local.tags
  vnet_address_space         = var.vnet_address_space
  log_retention_days         = var.log_retention_days
  log_daily_quota_gb         = var.log_daily_quota_gb
  key_vault_purge_protection = var.key_vault_purge_protection
  key_vault_soft_delete_days = var.key_vault_soft_delete_days
}

module "data" {
  source = "../data"

  name                         = local.name
  location                     = module.foundation.location
  resource_group_name          = module.foundation.resource_group_name
  tags                         = local.tags
  suffix                       = module.foundation.suffix
  key_vault_id                 = module.foundation.key_vault_id
  apps_identity_principal_id   = module.foundation.identity_principal_id
  apps_subnet_id               = module.foundation.apps_subnet_id
  postgres_subnet_id           = module.foundation.postgres_subnet_id
  postgres_private_dns_zone_id = module.foundation.postgres_private_dns_zone_id

  postgres_sku_name              = var.postgres_sku_name
  postgres_storage_mb            = var.postgres_storage_mb
  postgres_backup_retention_days = var.postgres_backup_retention_days
  postgres_geo_redundant_backup  = var.postgres_geo_redundant_backup
  enable_redis                   = var.enable_redis
  enable_rabbitmq_storage        = var.enable_rabbitmq

  # The administrator password secret is written with the deployer's own role, the DNS link must exist before the server.
  depends_on = [module.foundation]
}

module "apps" {
  source = "../apps"

  name                       = local.name
  location                   = module.foundation.location
  resource_group_name        = module.foundation.resource_group_name
  tags                       = local.tags
  identity_id                = module.foundation.identity_id
  key_vault_uri              = module.foundation.key_vault_uri
  log_analytics_workspace_id = module.foundation.log_analytics_workspace_id
  apps_subnet_id             = module.foundation.apps_subnet_id
  apps_subnet_cidr           = module.foundation.apps_subnet_cidr

  deploy_apps       = var.deploy_apps
  image_registry    = var.image_registry
  image_tag         = var.image_tag
  registry_username = var.registry_username

  api_min_replicas    = var.api_min_replicas
  api_max_replicas    = var.api_max_replicas
  static_min_replicas = var.static_min_replicas
  redis_enabled       = var.enable_redis

  parent_host           = var.parent_host
  allowed_hosts         = var.allowed_hosts
  storage_public_origin = trimsuffix(module.data.documents_blob_endpoint, "/")
  otlp_endpoint         = var.otlp_endpoint
  extra_env             = var.extra_env
  extra_secret_env      = var.extra_secret_env

  rabbitmq_enabled              = var.enable_rabbitmq
  rabbitmq_storage_account_name = module.data.rabbitmq_account_name
  rabbitmq_storage_share_name   = module.data.rabbitmq_share_name
  rabbitmq_storage_account_key  = module.data.rabbitmq_account_key
  notifications_enabled         = var.enable_notifications_worker
}
