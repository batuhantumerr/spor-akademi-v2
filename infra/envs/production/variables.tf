variable "subscription_id" {
  description = "Azure subscription id (not a secret, but specific to the owner): pass it with TF_VAR_subscription_id or a local tfvars file."
  type        = string
}

variable "image_tag" {
  description = "Commit SHA of the images CI pushed to GHCR."
  type        = string
}

variable "deploy_apps" {
  description = "false on the first apply (infrastructure only), true once the Key Vault secrets are written."
  type        = bool
  default     = false
}
