variable "environment" {
  description = "staging or production."
  type        = string
  validation {
    condition     = contains(["staging", "production"], var.environment)
    error_message = "environment must be staging or production."
  }
}

variable "location" {
  description = "Azure region. Azure has no Turkiye region: read ADR-0014 and docs/bulut-altyapisi.md before choosing."
  type        = string
}

variable "tags" {
  description = "Extra tags; the standard ones are always set."
  type        = map(string)
  default     = {}
}

variable "vnet_address_space" {
  type = string
}

variable "image_registry" {
  type = string
}

variable "image_tag" {
  type = string
}

variable "registry_username" {
  type    = string
  default = ""
}

variable "deploy_apps" {
  description = "Second stage of the first apply: true after the Key Vault secrets exist (docs/bulut-altyapisi.md)."
  type        = bool
  default     = false
}

# Data
variable "postgres_sku_name" {
  type    = string
  default = "B_Standard_B1ms"
}

variable "postgres_storage_mb" {
  type    = number
  default = 32768
}

variable "postgres_backup_retention_days" {
  type    = number
  default = 7
}

variable "postgres_geo_redundant_backup" {
  type    = bool
  default = false
}

variable "enable_redis" {
  type    = bool
  default = false
}

variable "enable_rabbitmq" {
  type    = bool
  default = true
}

variable "enable_notifications_worker" {
  type    = bool
  default = false
}

# Observability and secrets
variable "log_retention_days" {
  type    = number
  default = 30
}

variable "log_daily_quota_gb" {
  type    = number
  default = 0.5
}

variable "key_vault_purge_protection" {
  type    = bool
  default = false
}

variable "key_vault_soft_delete_days" {
  type    = number
  default = 7
}

# Apps
variable "api_min_replicas" {
  type    = number
  default = 1
}

variable "api_max_replicas" {
  type    = number
  default = 1
}

variable "static_min_replicas" {
  type    = number
  default = 1
}

variable "admin_host" {
  type    = string
  default = ""
}

variable "parent_host" {
  type    = string
  default = ""
}

variable "allowed_hosts" {
  type    = string
  default = "*"
}

variable "otlp_endpoint" {
  type    = string
  default = ""
}

variable "extra_env" {
  type    = map(string)
  default = {}
}

variable "extra_secret_env" {
  type    = map(string)
  default = {}
}
