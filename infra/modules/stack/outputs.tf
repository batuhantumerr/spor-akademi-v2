output "resource_group_name" { value = module.foundation.resource_group_name }
output "key_vault_name" { value = module.foundation.key_vault_name }
output "postgres_fqdn" { value = module.data.postgres_fqdn }
output "documents_account_name" { value = module.data.documents_account_name }
output "documents_container" { value = module.data.documents_container }
output "identity_client_id" { value = module.foundation.identity_client_id }
output "gateway_fqdn" { value = module.apps.gateway_fqdn }
output "custom_domain_verification_id" { value = module.apps.custom_domain_verification_id }
output "application_insights_connection_string" {
  value     = module.foundation.application_insights_connection_string
  sensitive = true
}
output "redis_hostname" { value = module.data.redis_hostname }
