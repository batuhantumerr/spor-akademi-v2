# Remote state (owner fills this in before the first apply; docs/bulut-altyapisi.md, checklist). The state holds
# resource ids and the Azure Files key of the RabbitMQ storage, so it must live in a private, encrypted container
# that only the people who run tofu can read. Create the storage account by hand (or a bootstrap script), once, outside
# this code; the account is not managed here so that destroying an environment never destroys its own state.
#
# terraform {
#   backend "azurerm" {
#     resource_group_name  = "rg-spor-tfstate"
#     storage_account_name = "<globally unique, lowercase>"
#     container_name       = "tfstate"
#     key                  = "production.tfstate"
#     use_azuread_auth     = true # Entra ID, no storage key
#   }
# }
#
# Without a backend block the state is a local file (ignored by infra/.gitignore): fine for a trial, never for the
# real environment.
