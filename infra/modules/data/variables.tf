variable "name" {
  type = string
}

variable "location" {
  type = string
}

variable "resource_group_name" {
  type = string
}

variable "tags" {
  type = map(string)
}

variable "suffix" {
  description = "Random suffix from the foundation module (storage account names are global)."
  type        = string
}

variable "key_vault_id" {
  type = string
}

variable "apps_identity_principal_id" {
  type = string
}

variable "apps_subnet_id" {
  type = string
}

variable "postgres_subnet_id" {
  type = string
}

variable "postgres_private_dns_zone_id" {
  type = string
}

variable "postgres_version" {
  description = "PostgreSQL major version. compose.yaml and the tests run 18; the first apply fails here if the provider or region does not offer it, lower it and write the reason in docs/bulut-altyapisi.md."
  type        = string
  default     = "18"
}

variable "postgres_sku_name" {
  type    = string
  default = "B_Standard_B1ms"
}

variable "postgres_storage_mb" {
  type    = number
  default = 32768
}

variable "postgres_backup_retention_days" {
  description = "Point-in-time recovery window in days (7 to 35). NFR-REL-03 asks for 30 days in production."
  type        = number
  default     = 7
}

variable "postgres_geo_redundant_backup" {
  description = "Geo-redundant backups copy the data to the paired region: a second place the data lives (ADR-0014)."
  type        = bool
  default     = false
}

variable "postgres_admin_password_version" {
  description = "Increase to rotate the generated administrator password."
  type        = number
  default     = 1
}

variable "enable_redis" {
  description = "Azure Cache for Redis (Basic C0) for the HybridCache L2 and the SignalR backplane. Off: in-memory cache only, so the API must run a single replica."
  type        = bool
  default     = false
}

variable "enable_rabbitmq_storage" {
  description = "Storage account and Azure Files share that RabbitMQ keeps its data on."
  type        = bool
  default     = false
}

variable "blob_soft_delete_days" {
  type    = number
  default = 7
}
