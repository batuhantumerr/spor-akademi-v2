provider "azurerm" {
  subscription_id     = var.subscription_id
  storage_use_azuread = true
  features {
    key_vault {
      purge_soft_delete_on_destroy = true # staging is disposable
    }
    resource_group {
      prevent_deletion_if_contains_resources = true
    }
  }
}

# Staging: the cheapest shape that still behaves like production (docs/bulut-altyapisi.md, cost table).
module "stack" {
  source = "../../modules/stack"

  environment        = "staging"
  location           = "westeurope" # ADR-0014: Azure has no Turkiye region; the owner decides
  vnet_address_space = "10.41.0.0/16"
  image_registry     = "ghcr.io/batuhantumerr"
  image_tag          = var.image_tag
  deploy_apps        = var.deploy_apps

  postgres_backup_retention_days = 7
  log_retention_days             = 30
  log_daily_quota_gb             = 0.2 # worst case about 6 GB/month

  # Scale to zero between uses; the data stays.
  api_min_replicas    = 0
  api_max_replicas    = 1
  static_min_replicas = 0
  enable_redis        = false
  enable_rabbitmq     = false # turn on with the P5-04 worker (about 9 USD/month more)

  allowed_hosts = "*" # set to the staging host names (DEC-516)
}
