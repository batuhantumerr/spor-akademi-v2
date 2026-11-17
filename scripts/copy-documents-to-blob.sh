#!/usr/bin/env bash
# P5-06 (DEC-655): copies the uploaded documents of the S3-compatible store (SeaweedFS or any S3 store) to the Azure Blob
# container, key for key (an object's S3 key is its blob name, the application never rewrites keys).
#
# Needs: aws CLI (Apache-2.0) and azcopy (MIT), already signed in to Azure (`azcopy login`, with a role that can write blobs,
# e.g. Storage Blob Data Contributor). No account key is used or accepted. Nothing is read from .env: everything comes in as
# environment variables you set in your own shell.
#
#   S3_ENDPOINT_URL       S3 endpoint, e.g. https://dosya.example.com (empty for AWS S3)
#   S3_BUCKET             source bucket
#   AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY   read access to the bucket (your shell, not a file in the repository)
#   BLOB_ACCOUNT          storage account name (tofu output documents_account_name)
#   BLOB_CONTAINER        container name (tofu output documents_container)
#
# Usage: bash scripts/copy-documents-to-blob.sh [--dry-run]
# Re-running is safe: blobs already there and not older than the source are skipped.
set -euo pipefail

dry_run=false
case "${1:-}" in
  "") ;;
  --dry-run) dry_run=true ;;
  *) echo "usage: $0 [--dry-run]" >&2; exit 2 ;;
esac

for name in S3_BUCKET BLOB_ACCOUNT BLOB_CONTAINER AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY; do
  if [ -z "${!name:-}" ]; then
    echo "$name is not set" >&2
    exit 2
  fi
done
command -v aws >/dev/null || { echo "aws CLI is required" >&2; exit 2; }
command -v azcopy >/dev/null || { echo "azcopy is required" >&2; exit 2; }

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

endpoint=()
if [ -n "${S3_ENDPOINT_URL:-}" ]; then
  endpoint=(--endpoint-url "$S3_ENDPOINT_URL")
fi

echo "Downloading s3://$S3_BUCKET to a temporary directory..."
aws s3 sync ${endpoint[@]+"${endpoint[@]}"} "s3://$S3_BUCKET" "$work" --only-show-errors
count=$(find "$work" -type f | wc -l | tr -d ' ')
echo "Found $count object(s)."

target="https://$BLOB_ACCOUNT.blob.core.windows.net/$BLOB_CONTAINER"
if [ "$dry_run" = true ]; then
  echo "Dry run: would copy $count object(s) to $target"
  exit 0
fi

# The directory's own name must not become part of the blob names: copy its contents.
azcopy copy "$work/*" "$target" --recursive --overwrite=ifSourceNewer

echo "Copied $count object(s). Check the count in the container (Azure portal, or: azcopy list $target)."
