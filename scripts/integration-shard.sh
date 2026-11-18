#!/usr/bin/env bash
# Runs one shard of SporAkademi.IntegrationTests (#181). The suite runs serially in one xUnit collection with one
# PostgreSQL container per process, so CI splits it by namespace into parallel jobs, each with its own container.
#
#   bash scripts/integration-shard.sh <shard>/<count> [extra dotnet test arguments, e.g. --no-build -c Release]
#
# <count> must equal the number of shards below (the CI matrix passes its own size), so a matrix that is not resized
# with this list fails instead of silently skipping the last shard.
#
# Shards 1..N-1 list their namespaces (sized by test count and the CI timings of each shard). The last shard runs
# everything the others do not list, so a new namespace (or a test outside any listed one) is never skipped.
# A listed namespace that no longer exists makes its shard run fewer tests; one that matches nothing fails the shard
# (xUnit exit code 8, zero tests ran).
set -euo pipefail

shards=(
  "Billing Groups Caching Health"
  "Identity Security Tenancy Notifications"
  "Students Outbox Parents Platform Observability"
  # The last shard: everything else (Attendance, Enrollments, Storage, Jobs, Organization, Audit, Performance,
  # Announcements, Persistence, DemoSeed, Reports, Realtime, Dashboard and any new namespace).
  ""
)

arg="${1:?usage: integration-shard.sh <shard>/${#shards[@]} [dotnet test arguments]}"
shift
if ! [[ "$arg" =~ ^([0-9]+)/([0-9]+)$ ]] || ((BASH_REMATCH[2] != ${#shards[@]})); then
  echo "Expected <shard>/${#shards[@]} (this script defines ${#shards[@]} shards), got '$arg'." >&2
  exit 2
fi
shard=${BASH_REMATCH[1]}
if ((shard < 1 || shard > ${#shards[@]})); then
  echo "Shard must be 1..${#shards[@]}, got $shard." >&2
  exit 2
fi

prefix=SporAkademi.IntegrationTests
filters=()
if ((shard < ${#shards[@]})); then
  for ns in ${shards[shard - 1]}; do filters+=(--filter-namespace "$prefix.$ns"); done
else
  for listed in "${shards[@]}"; do
    for ns in $listed; do filters+=(--filter-not-namespace "$prefix.$ns"); done
  done
fi

echo "Integration shard $shard/${#shards[@]}: ${filters[*]}"
cd "$(dirname "$0")/.."
exec dotnet test --project tests/SporAkademi.IntegrationTests "$@" "${filters[@]}"
