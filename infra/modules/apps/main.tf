locals {
  image_prefix = "${var.image_registry}/spor-akademi-v2"
  use_registry = var.registry_username != ""

  # Secret name (in the app) => secret name in Key Vault. The apps read them with the managed identity; the values
  # are written by the owner and never appear in this code or in state.
  api_secret_env = merge(
    {
      ConnectionStrings__App = "conn-app"
      Jwt__SigningKey        = "jwt-signing-key"
      Mfa__EncryptionKey     = "mfa-encryption-key"
    },
    # V1-03 (DEC-685): keys that were current once only decrypt, until no secret is stored under them (runbook in
    # docs/bulut-altyapisi.md). Secret "mfa-encryption-key-<id>" is read as Mfa:PreviousEncryptionKeys:<id>.
    { for id in var.mfa_previous_key_ids : "Mfa__PreviousEncryptionKeys__${id}" => "mfa-encryption-key-${id}" },
    var.redis_enabled ? { ConnectionStrings__Cache = "conn-cache" } : {},
    # P5-03/P5-04: with the broker, the outbox publishes through it (the API also consumes what the worker does not).
    var.rabbitmq_enabled ? { ConnectionStrings__RabbitMq = "conn-rabbitmq" } : {},
    var.email_secret_env,
    var.extra_secret_env,
  )

  api_plain_env = merge(
    {
      ASPNETCORE_ENVIRONMENT = "Production"
      AllowedHosts           = var.allowed_hosts
      # V1-03 (DEC-685): names the current key in the stored ciphertext; change it together with mfa-encryption-key.
      Mfa__EncryptionKeyId = var.mfa_key_id
      # Container Apps' ingress is the proxy the API sees: trust its subnet and nothing else (DEC-507).
      ForwardedHeaders__KnownNetworks__0 = var.apps_subnet_cidr
      # Two proxies in front of the API: the gateway's ingress and Caddy, then the API's own ingress (DEC-651).
      # Verify on the first staging (docs/bulut-tasima-runbook.md, section 8); a longer chain only changes this number.
      ForwardedHeaders__ForwardLimit = "2"
    },
    var.storage_account_name != "" ? {
      # Azure Blob with the managed identity, no account key (shared keys are off); links are user delegation SAS (DEC-650).
      Storage__Provider                = "AzureBlob"
      Storage__AccountName             = var.storage_account_name
      Storage__Container               = var.storage_container
      Storage__ManagedIdentityClientId = var.identity_client_id
    } : {},
    var.otlp_endpoint != "" ? { OpenTelemetry__Otlp__Endpoint = var.otlp_endpoint } : {},
    # P5-04 (DEC-635): while the worker runs, the API leaves the e-mail consumers (group "notifications") to it.
    # Without the worker (or without the broker) the API handles e-mail itself, as before.
    local.notifications_active ? { RabbitMq__Consumers__ExcludeGroups__0 = "notifications" } : {},
    var.email_env,
    var.extra_env,
  )

  rabbitmq_name = "rabbitmq"

  notifications_active = var.notifications_enabled && var.rabbitmq_enabled

  # The worker: the same database and broker as the API plus the e-mail settings, nothing else (no JWT key, no cache).
  notifications_secret_env = merge(
    {
      ConnectionStrings__App      = "conn-app"
      ConnectionStrings__RabbitMq = "conn-rabbitmq"
    },
    var.email_secret_env,
    var.notifications_extra_secret_env,
  )

  notifications_plain_env = merge(
    { ASPNETCORE_ENVIRONMENT = "Production" },
    var.otlp_endpoint != "" ? { OpenTelemetry__Otlp__Endpoint = var.otlp_endpoint } : {},
    var.email_env,
    var.notifications_extra_env,
  )

  # The Key Vault secret ids need no version: the app then follows the latest one (restart for a new value).
  secret_url = { for n in toset(concat(
    values(local.api_secret_env),
    values(local.notifications_secret_env),
    ["conn-migrator", "rabbitmq-password", "conn-rabbitmq"],
    local.use_registry ? ["ghcr-pull-token"] : [],
  )) : n => "${var.key_vault_uri}secrets/${n}" }
}

resource "azurerm_container_app_environment" "this" {
  name                       = "cae-${var.name}"
  location                   = var.location
  resource_group_name        = var.resource_group_name
  log_analytics_workspace_id = var.log_analytics_workspace_id
  infrastructure_subnet_id   = var.apps_subnet_id
  tags                       = var.tags

  # Consumption-only profile: no dedicated nodes, so nothing is billed while no replica runs.
  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }

  lifecycle {
    ignore_changes = [infrastructure_resource_group_name]
  }
}

# --- API -----------------------------------------------------------------------------------------------------
resource "azurerm_container_app" "api" {
  count                        = var.deploy_apps ? 1 : 0
  name                         = "api"
  container_app_environment_id = azurerm_container_app_environment.this.id
  resource_group_name          = var.resource_group_name
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [var.identity_id]
  }

  dynamic "registry" {
    for_each = local.use_registry ? [1] : []
    content {
      server               = split("/", var.image_registry)[0]
      username             = var.registry_username
      password_secret_name = "ghcr-pull-token"
    }
  }

  dynamic "secret" {
    for_each = toset(concat(values(local.api_secret_env), local.use_registry ? ["ghcr-pull-token"] : []))
    content {
      name                = secret.value
      identity            = var.identity_id
      key_vault_secret_id = local.secret_url[secret.value]
    }
  }

  ingress {
    external_enabled           = false # only the gateway is public
    target_port                = 8080
    transport                  = "auto"
    allow_insecure_connections = false
    # SignalR: negotiate and the WebSocket that follows must reach the same replica (cookie affinity, as the load
    # test's proxy does, DEC-565). With one replica it is moot; it matters as soon as api_max_replicas > 1.
    session_affinity_enabled = true

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = var.api_min_replicas
    max_replicas = var.api_max_replicas

    container {
      name   = "api"
      image  = "${local.image_prefix}-api:${var.image_tag}"
      cpu    = var.api_cpu
      memory = var.api_memory

      dynamic "env" {
        for_each = local.api_plain_env
        content {
          name  = env.key
          value = env.value
        }
      }

      dynamic "env" {
        for_each = local.api_secret_env
        content {
          name        = env.key
          secret_name = env.value
        }
      }

      # /health/live has no dependency checks; /health/ready checks the database, outbox and the other dependencies
      # (src/SporAkademi.Api/HealthEndpoints.cs). A failing readiness probe only takes the replica out of rotation.
      startup_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        interval_seconds        = 3
        failure_count_threshold = 20
      }

      liveness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        interval_seconds        = 10
        failure_count_threshold = 3
      }

      readiness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/ready"
        interval_seconds        = 10
        failure_count_threshold = 3
      }
    }

    http_scale_rule {
      name                = "http"
      concurrent_requests = "100"
    }
  }

  lifecycle {
    # SignalR keeps each connection in the memory of one replica. Without the Redis backplane a second replica
    # would drop real-time messages (docs/performans.md), so the plan refuses it.
    precondition {
      condition     = var.api_max_replicas <= 1 || var.redis_enabled
      error_message = "api_max_replicas > 1 needs redis_enabled (HybridCache L2 and the SignalR backplane)."
    }
  }
}

# --- Admin and parent (nginx) --------------------------------------------------------------------------------
resource "azurerm_container_app" "admin" {
  count                        = var.deploy_apps ? 1 : 0
  name                         = "admin"
  container_app_environment_id = azurerm_container_app_environment.this.id
  resource_group_name          = var.resource_group_name
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [var.identity_id]
  }

  dynamic "registry" {
    for_each = local.use_registry ? [1] : []
    content {
      server               = split("/", var.image_registry)[0]
      username             = var.registry_username
      password_secret_name = "ghcr-pull-token"
    }
  }

  dynamic "secret" {
    for_each = local.use_registry ? ["ghcr-pull-token"] : []
    content {
      name                = secret.value
      identity            = var.identity_id
      key_vault_secret_id = local.secret_url[secret.value]
    }
  }

  ingress {
    external_enabled = false
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = var.static_min_replicas
    max_replicas = 2

    container {
      name   = "admin"
      image  = "${local.image_prefix}-admin:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "STORAGE_PUBLIC_ORIGIN"
        value = var.storage_public_origin
      }

      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/index.html"
      }

      readiness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/index.html"
      }
    }
  }
}

resource "azurerm_container_app" "parent" {
  count                        = var.deploy_apps ? 1 : 0
  name                         = "parent"
  container_app_environment_id = azurerm_container_app_environment.this.id
  resource_group_name          = var.resource_group_name
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [var.identity_id]
  }

  dynamic "registry" {
    for_each = local.use_registry ? [1] : []
    content {
      server               = split("/", var.image_registry)[0]
      username             = var.registry_username
      password_secret_name = "ghcr-pull-token"
    }
  }

  dynamic "secret" {
    for_each = local.use_registry ? ["ghcr-pull-token"] : []
    content {
      name                = secret.value
      identity            = var.identity_id
      key_vault_secret_id = local.secret_url[secret.value]
    }
  }

  ingress {
    external_enabled = false
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = var.static_min_replicas
    max_replicas = 2

    container {
      name   = "parent"
      image  = "${local.image_prefix}-parent:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"

      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/index.html"
      }

      readiness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/index.html"
      }
    }
  }
}

# --- Gateway (Caddy): the only public ingress ----------------------------------------------------------------
resource "azurerm_container_app" "gateway" {
  count                        = var.deploy_apps ? 1 : 0
  name                         = "gateway"
  container_app_environment_id = azurerm_container_app_environment.this.id
  resource_group_name          = var.resource_group_name
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = var.tags

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = var.static_min_replicas
    max_replicas = 2

    container {
      name   = "caddy"
      image  = var.gateway_image
      cpu    = 0.25
      memory = "0.5Gi"

      command = ["/bin/sh", "-c", "printf '%s\\n' \"$CADDYFILE\" > /tmp/Caddyfile && exec caddy run --config /tmp/Caddyfile --adapter caddyfile"]

      env {
        name = "CADDYFILE"
        value = templatefile("${path.module}/Caddyfile.tftpl", {
          trusted_cidr = var.apps_subnet_cidr
          api_fqdn     = azurerm_container_app.api[0].ingress[0].fqdn
          admin_fqdn   = azurerm_container_app.admin[0].ingress[0].fqdn
          parent_fqdn  = azurerm_container_app.parent[0].ingress[0].fqdn
          parent_host  = var.parent_host
        })
      }

      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/healthz"
      }

      readiness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/healthz"
      }
    }
  }
}

# --- Migrations: run by hand (or by the deploy workflow) before a new API revision ---------------------------
# Start with: az containerapp job start -n migrator -g <resource group>
resource "azurerm_container_app_job" "migrator" {
  count                        = var.deploy_apps ? 1 : 0
  name                         = "migrator"
  location                     = var.location
  resource_group_name          = var.resource_group_name
  container_app_environment_id = azurerm_container_app_environment.this.id
  workload_profile_name        = "Consumption"
  replica_timeout_in_seconds   = 900
  replica_retry_limit          = 0
  tags                         = var.tags

  manual_trigger_config {
    parallelism              = 1
    replica_completion_count = 1
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [var.identity_id]
  }

  dynamic "registry" {
    for_each = local.use_registry ? [1] : []
    content {
      server               = split("/", var.image_registry)[0]
      username             = var.registry_username
      password_secret_name = "ghcr-pull-token"
    }
  }

  dynamic "secret" {
    for_each = toset(concat(["conn-migrator"], local.use_registry ? ["ghcr-pull-token"] : []))
    content {
      name                = secret.value
      identity            = var.identity_id
      key_vault_secret_id = local.secret_url[secret.value]
    }
  }

  template {
    container {
      name   = "migrator"
      image  = "${local.image_prefix}-migrator:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      env {
        name        = "ConnectionStrings__Migrator"
        secret_name = "conn-migrator"
      }
    }
  }
}

# --- RabbitMQ (P5-04 broker) ---------------------------------------------------------------------------------
resource "azurerm_container_app_environment_storage" "rabbitmq" {
  count                        = var.rabbitmq_enabled ? 1 : 0
  name                         = "rabbitmq-data"
  container_app_environment_id = azurerm_container_app_environment.this.id
  account_name                 = var.rabbitmq_storage_account_name
  share_name                   = var.rabbitmq_storage_share_name
  access_key                   = var.rabbitmq_storage_account_key
  access_mode                  = "ReadWrite"
}

resource "azurerm_container_app" "rabbitmq" {
  count                        = var.rabbitmq_enabled && var.deploy_apps ? 1 : 0
  name                         = local.rabbitmq_name
  container_app_environment_id = azurerm_container_app_environment.this.id
  resource_group_name          = var.resource_group_name
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [var.identity_id]
  }

  secret {
    name                = "rabbitmq-password"
    identity            = var.identity_id
    key_vault_secret_id = local.secret_url["rabbitmq-password"]
  }

  # AMQP stays inside the environment: other apps reach it at rabbitmq:5672.
  ingress {
    external_enabled = false
    target_port      = 5672
    exposed_port     = 5672
    transport        = "tcp"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    # One replica only: the queues live on one node's disk.
    min_replicas = 1
    max_replicas = 1

    volume {
      name         = "data"
      storage_type = "AzureFile"
      storage_name = azurerm_container_app_environment_storage.rabbitmq[0].name
      # RabbitMQ runs as uid/gid 999 in the image; SMB needs it spelled out.
      mount_options = "uid=999,gid=999,dir_mode=0770,file_mode=0660,nobrl,mfsymlinks,cache=none"
    }

    container {
      name   = "rabbitmq"
      image  = var.rabbitmq_image
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "RABBITMQ_DEFAULT_USER"
        value = "spor_akademi"
      }
      env {
        name        = "RABBITMQ_DEFAULT_PASS"
        secret_name = "rabbitmq-password"
      }

      volume_mounts {
        name = "data"
        path = "/var/lib/rabbitmq"
      }

      liveness_probe {
        transport = "TCP"
        port      = 5672
      }
    }
  }
}

# --- Notifications worker (P5-04, DEC-635) ---------------------------------------------------------------------
# Runs only the e-mail consumers (src/SporAkademi.Notifications). No ingress: nobody calls it, it only consumes from the
# broker, and Container Apps probes it on port 8080 without one. It reads the same Key Vault secrets as the API for the
# database, the broker and the e-mail settings. Needs the broker (the precondition below refuses a plan without it).
resource "azurerm_container_app" "notifications" {
  count                        = var.notifications_enabled && var.deploy_apps ? 1 : 0
  name                         = "notifications"
  container_app_environment_id = azurerm_container_app_environment.this.id
  resource_group_name          = var.resource_group_name
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [var.identity_id]
  }

  dynamic "registry" {
    for_each = local.use_registry ? [1] : []
    content {
      server               = split("/", var.image_registry)[0]
      username             = var.registry_username
      password_secret_name = "ghcr-pull-token"
    }
  }

  dynamic "secret" {
    for_each = toset(concat(values(local.notifications_secret_env), local.use_registry ? ["ghcr-pull-token"] : []))
    content {
      name                = secret.value
      identity            = var.identity_id
      key_vault_secret_id = local.secret_url[secret.value]
    }
  }

  template {
    # One replica keeps e-mail flowing; more replicas share the queues (competing consumers) when the backlog grows.
    min_replicas = var.notifications_min_replicas
    max_replicas = var.notifications_max_replicas

    container {
      name   = "notifications"
      image  = "${local.image_prefix}-notifications:${var.image_tag}"
      cpu    = var.notifications_cpu
      memory = var.notifications_memory

      dynamic "env" {
        for_each = local.notifications_plain_env
        content {
          name  = env.key
          value = env.value
        }
      }

      dynamic "env" {
        for_each = local.notifications_secret_env
        content {
          name        = env.key
          secret_name = env.value
        }
      }

      # /health/live has no dependency checks, so a database or broker outage never restarts the replica. There is no
      # readiness probe on purpose: without ingress it routes nothing, and a broker blip would only flap the revision's
      # health. /health/ready (database, broker, consumers running; src/SporAkademi.Notifications/NotificationsHost.cs)
      # is for the CI smoke test and for operators. The startup probe holds the revision until the process answers.
      startup_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        interval_seconds        = 3
        failure_count_threshold = 20
      }

      liveness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        interval_seconds        = 10
        failure_count_threshold = 3
      }

    }
  }

  lifecycle {
    precondition {
      condition     = var.rabbitmq_enabled
      error_message = "The notifications worker consumes from the broker: turn on rabbitmq_enabled (enable_rabbitmq) with it."
    }
    # Without an SMTP host the worker only logs e-mails and acknowledges them: they would be lost (the worker refuses to start too).
    precondition {
      condition     = contains(keys(merge(var.email_env, var.notifications_extra_env)), "Email__Smtp__Host")
      error_message = "The notifications worker needs Email__Smtp__Host in email_env (or notifications_extra_env); otherwise e-mails are acknowledged and never sent."
    }
  }
}
