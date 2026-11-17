variable "name" {
  description = "Short name that prefixes every resource, for example spor-staging."
  type        = string
}

variable "location" {
  description = "Azure region. Azure has no Turkiye region (ADR-0014, docs/bulut-altyapisi.md)."
  type        = string
}

variable "tags" {
  type = map(string)
}

variable "vnet_address_space" {
  description = "Address space of the virtual network. Staging and production use different ranges so they can be peered later."
  type        = string
}

variable "log_retention_days" {
  type    = number
  default = 30
}

variable "log_daily_quota_gb" {
  description = "Daily ingestion cap of the Log Analytics workspace (cost guard). -1 means no cap."
  type        = number
  default     = 0.5
}

variable "key_vault_purge_protection" {
  description = "Purge protection cannot be turned off once on. Off in staging so the environment can be torn down and rebuilt."
  type        = bool
  default     = false
}

variable "key_vault_soft_delete_days" {
  type    = number
  default = 7
}
