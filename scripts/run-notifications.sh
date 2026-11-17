#!/usr/bin/env bash
# Runs the notifications worker (P5-04, DEC-633) locally: it consumes the e-mail messages from RabbitMQ. Needs the compose
# "messaging" profile's rabbitmq service. Run it next to scripts/run-api.sh, and tell the API to leave those messages alone:
#   API_EXCLUDE_NOTIFICATION_CONSUMERS=1 bash scripts/run-api.sh
# (run-api.sh then sets RabbitMq__Consumers__ExcludeGroups__0=notifications). Without the worker, run-api.sh alone keeps
# handling e-mail itself, exactly as before.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib/env.sh"

load_env
require_env SPOR_AKADEMI_APP_PASSWORD RABBITMQ_PASSWORD

if ! (cd "$REPO_ROOT" && docker compose --profile messaging ps --status running --services 2>/dev/null | grep -qx rabbitmq); then
  echo "error: RabbitMQ is not running (docker compose --profile messaging up -d --wait); the worker has nothing to consume." >&2
  exit 1
fi

ConnectionStrings__App="$(connection_string spor_akademi_app SPOR_AKADEMI_APP_PASSWORD)"
ConnectionStrings__RabbitMq="amqp://spor_akademi:$RABBITMQ_PASSWORD@127.0.0.1:${RABBITMQ_HOST_PORT:-55672}/"
export ConnectionStrings__App ConnectionStrings__RabbitMq
echo "run-notifications: consuming e-mail messages from RabbitMQ (management UI http://127.0.0.1:${RABBITMQ_UI_HOST_PORT:-55673})"
# Optional (DEC-357): e-mail goes to the local Mailpit catcher when the compose "mail" profile's mailpit service is running;
# otherwise the worker only logs each e-mail's template name.
if (cd "$REPO_ROOT" && docker compose --profile mail ps --status running --services 2>/dev/null | grep -qx mailpit); then
  export Email__Smtp__Host=127.0.0.1
  export Email__Smtp__Port="${MAILPIT_SMTP_HOST_PORT:-51025}"
  export Email__Smtp__Security=None
  echo "run-notifications: e-mail goes to Mailpit (web UI http://127.0.0.1:${MAILPIT_UI_HOST_PORT:-58025})"
else
  echo "run-notifications: Mailpit not running (docker compose --profile mail up -d --wait); e-mails are only logged by template name"
fi
# Optional (DEC-554): traces and metrics go over OTLP to the local Jaeger when the compose "observability" profile is running.
if (cd "$REPO_ROOT" && docker compose --profile observability ps --status running --services 2>/dev/null | grep -qx jaeger); then
  export OpenTelemetry__Otlp__Endpoint="http://127.0.0.1:${OTLP_GRPC_HOST_PORT:-54317}"
  export OpenTelemetry__Otlp__Protocol=grpc
  echo "run-notifications: telemetry goes to Jaeger (traces http://127.0.0.1:${JAEGER_UI_HOST_PORT:-56686})"
else
  echo "run-notifications: observability profile not running; telemetry off"
fi
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://127.0.0.1:5081}"
export Seq__ServerUrl="http://127.0.0.1:${SEQ_HOST_PORT:-5341}"

cd "$REPO_ROOT"
exec dotnet run --project src/SporAkademi.Notifications --no-launch-profile "$@"
