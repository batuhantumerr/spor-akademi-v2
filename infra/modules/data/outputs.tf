output "postgres_fqdn" { value = azurerm_postgresql_flexible_server.this.fqdn }
output "postgres_server_name" { value = azurerm_postgresql_flexible_server.this.name }
output "postgres_database" { value = azurerm_postgresql_flexible_server_database.app.name }
output "documents_account_name" { value = azurerm_storage_account.documents.name }
output "documents_container" { value = azurerm_storage_container.documents.name }
output "documents_blob_endpoint" { value = azurerm_storage_account.documents.primary_blob_endpoint }

output "rabbitmq_account_name" {
  value = var.enable_rabbitmq_storage ? azurerm_storage_account.rabbitmq[0].name : null
}
output "rabbitmq_share_name" {
  value = var.enable_rabbitmq_storage ? azurerm_storage_share.rabbitmq[0].name : null
}
output "rabbitmq_account_key" {
  value     = var.enable_rabbitmq_storage ? azurerm_storage_account.rabbitmq[0].primary_access_key : null
  sensitive = true
}

output "redis_enabled" { value = var.enable_redis }
output "redis_hostname" {
  value = var.enable_redis ? azurerm_redis_cache.this[0].hostname : null
}
output "redis_ssl_port" {
  value = var.enable_redis ? azurerm_redis_cache.this[0].ssl_port : null
}
