output "resource_group_name" { value = module.stack.resource_group_name }
output "key_vault_name" { value = module.stack.key_vault_name }
output "postgres_fqdn" { value = module.stack.postgres_fqdn }
output "documents_account_name" { value = module.stack.documents_account_name }
output "documents_container" { value = module.stack.documents_container }
output "identity_client_id" { value = module.stack.identity_client_id }
output "gateway_fqdn" { value = module.stack.gateway_fqdn }
output "custom_domain_verification_id" { value = module.stack.custom_domain_verification_id }
