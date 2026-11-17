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

variable "identity_id" {
  type = string
}

variable "key_vault_uri" {
  description = "Vault URI with a trailing slash, for example https://kv-example.vault.azure.net/."
  type        = string
}

variable "log_analytics_workspace_id" {
  type = string
}

variable "apps_subnet_id" {
  type = string
}

variable "apps_subnet_cidr" {
  description = "Trusted as a proxy network by the API (ForwardedHeaders:KnownNetworks)."
  type        = string
}

variable "deploy_apps" {
  description = "false creates only the Container Apps environment and the Azure Files link; the apps and the job need the Key Vault secrets the owner writes first."
  type        = bool
  default     = false
}

# --- Images --------------------------------------------------------------------------------------------------
variable "image_registry" {
  description = "Registry path the CI publishes to (DEC-089), for example ghcr.io/<owner>. Images are <path>/spor-akademi-v2-<api|migrator|admin|parent>:<tag>."
  type        = string
}

variable "image_tag" {
  description = "The commit SHA CI pushed (no latest). A digest-only pin is not supported yet: the tag is the commit SHA, which CI builds once."
  type        = string
}

variable "registry_username" {
  description = "GHCR user for a private package. Empty when the packages are public. The token is the Key Vault secret ghcr-pull-token."
  type        = string
  default     = ""
}

variable "gateway_image" {
  description = "Caddy, the same image compose.yaml uses for the load test."
  type        = string
  default     = "docker.io/library/caddy:2.11.8-alpine@sha256:f6228111e5fafa1a5238258d0858e103f10599b8d1659a146e7726681a002bb9"
}

variable "rabbitmq_image" {
  description = "Pin by digest before the first apply (not pinned here: no registry access from this repo)."
  type        = string
  default     = "docker.io/library/rabbitmq:4-alpine"
}

# --- Sizing --------------------------------------------------------------------------------------------------
variable "api_min_replicas" {
  description = "0 lets the API scale to zero (staging); the SignalR connections and the first request then wait for a cold start."
  type        = number
  default     = 1
}

variable "api_max_replicas" {
  description = "More than 1 needs the Redis cache: HybridCache L2 and the SignalR backplane (docs/performans.md)."
  type        = number
  default     = 1
}

variable "api_cpu" {
  type    = number
  default = 0.5
}

variable "api_memory" {
  type    = string
  default = "1Gi"
}

variable "static_min_replicas" {
  description = "Replicas of admin, parent and the gateway."
  type        = number
  default     = 1
}

variable "redis_enabled" {
  type    = bool
  default = false
}

# --- Application settings ------------------------------------------------------------------------------------
variable "parent_host" {
  description = "Host name of the parent (guardian) app. Empty: no host routes to it."
  type        = string
  default     = ""
}

variable "allowed_hosts" {
  description = "ASP.NET AllowedHosts: semicolon-separated host names (DEC-516). Must be set for production."
  type        = string
  default     = "*"
}

variable "storage_public_origin" {
  description = "Origin of the document store, for the admin app's Content-Security-Policy img-src (STORAGE_PUBLIC_ORIGIN)."
  type        = string
  default     = ""
}

variable "otlp_endpoint" {
  description = "OTLP collector the API sends traces and metrics to. Empty: telemetry stays off (docs/gozlemlenebilirlik.md)."
  type        = string
  default     = ""
}

variable "extra_env" {
  description = "More plain API settings (ASP.NET name with double underscores, value)."
  type        = map(string)
  default     = {}
}

variable "extra_secret_env" {
  description = "More API settings that come from Key Vault: environment variable name => secret name."
  type        = map(string)
  default     = {}
}

# --- RabbitMQ and the notifications worker (P5-04) -----------------------------------------------------------
variable "rabbitmq_enabled" {
  type    = bool
  default = false
}

variable "rabbitmq_storage_account_name" {
  type    = string
  default = null
}

variable "rabbitmq_storage_share_name" {
  type    = string
  default = null
}

variable "rabbitmq_storage_account_key" {
  type      = string
  default   = null
  sensitive = true
}

variable "notifications_enabled" {
  description = "Run the P5-04 notifications worker; the API then leaves the e-mail consumers to it. Needs rabbitmq_enabled."
  type        = bool
  default     = false
}

variable "notifications_min_replicas" {
  type    = number
  default = 1
}

variable "notifications_max_replicas" {
  type    = number
  default = 1
}

variable "notifications_cpu" {
  type    = number
  default = 0.25
}

variable "notifications_memory" {
  type    = string
  default = "0.5Gi"
}

variable "email_env" {
  description = "Plain e-mail settings for the API and the worker (Email__Smtp__Host, Email__FromAddress, Email__AppBaseUrl, ...)."
  type        = map(string)
  default     = {}
}

variable "email_secret_env" {
  description = "E-mail settings from Key Vault for the API and the worker: environment variable name => secret name (e.g. Email__Smtp__Password = smtp-password)."
  type        = map(string)
  default     = {}
}

variable "notifications_extra_env" {
  description = "More plain settings for the worker only."
  type        = map(string)
  default     = {}
}

variable "notifications_extra_secret_env" {
  description = "More Key Vault settings for the worker only: environment variable name => secret name."
  type        = map(string)
  default     = {}
}
