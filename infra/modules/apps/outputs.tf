output "environment_default_domain" { value = azurerm_container_app_environment.this.default_domain }
output "environment_static_ip" { value = azurerm_container_app_environment.this.static_ip_address }
output "gateway_fqdn" {
  description = "Public address of the gateway; point the DNS CNAME of the application host names here."
  value       = var.deploy_apps ? azurerm_container_app.gateway[0].ingress[0].fqdn : null
}
output "custom_domain_verification_id" { value = azurerm_container_app_environment.this.custom_domain_verification_id }
