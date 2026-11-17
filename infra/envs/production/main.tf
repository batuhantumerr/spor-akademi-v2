provider "azurerm" {
  subscription_id     = var.subscription_id
  storage_use_azuread = true
  features {
    key_vault {
      purge_soft_delete_on_destroy = false
    }
    resource_group {
      prevent_deletion_if_contains_resources = true
    }
  }
}

module "stack" {
  source = "../../modules/stack"

  environment        = "production"
  location           = "westeurope" # ADR-0014: Azure has no Turkiye region; the owner decides
  vnet_address_space = "10.40.0.0/16"
  image_registry     = "ghcr.io/batuhantumerr"
  image_tag          = var.image_tag
  deploy_apps        = var.deploy_apps

  # NFR-REL-03: 30 days of point-in-time restore. Backups stay in the region (no geo-redundancy): ADR-0014.
  postgres_backup_retention_days = 30
  postgres_geo_redundant_backup  = false
  key_vault_purge_protection     = true
  key_vault_soft_delete_days     = 90
  log_retention_days             = 30
  log_daily_quota_gb             = 0.5 # worst case about 15 GB/month; the first 5 GB are free

  # One API replica until the Redis cache is on (SignalR backplane); then raise api_max_replicas.
  api_min_replicas    = 1
  api_max_replicas    = 1
  static_min_replicas = 1
  enable_redis        = false
  enable_rabbitmq     = false # turn on with the P5-04 worker (about 9 USD/month more)

  allowed_hosts = "*" # MUST be set to the real host names before real data (DEC-516)
}
