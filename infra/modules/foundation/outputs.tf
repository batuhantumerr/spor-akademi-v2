output "resource_group_name" { value = azurerm_resource_group.this.name }
output "location" { value = azurerm_resource_group.this.location }
output "suffix" { value = random_string.suffix.result }
output "log_analytics_workspace_id" { value = azurerm_log_analytics_workspace.this.id }
output "application_insights_connection_string" {
  value     = azurerm_application_insights.this.connection_string
  sensitive = true
}
output "identity_id" { value = azurerm_user_assigned_identity.apps.id }
output "identity_principal_id" { value = azurerm_user_assigned_identity.apps.principal_id }
output "key_vault_id" { value = azurerm_key_vault.this.id }
output "key_vault_name" { value = azurerm_key_vault.this.name }
output "key_vault_uri" { value = azurerm_key_vault.this.vault_uri }
output "apps_subnet_id" { value = azurerm_subnet.apps.id }
output "apps_subnet_cidr" { value = azurerm_subnet.apps.address_prefixes[0] }
output "postgres_subnet_id" { value = azurerm_subnet.postgres.id }
output "postgres_private_dns_zone_id" { value = azurerm_private_dns_zone.postgres.id }
output "deployer_role_assignment_id" { value = azurerm_role_assignment.deployer_write_secrets.id }
