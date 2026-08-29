#!/usr/bin/env bash
set -euo pipefail

requested_ref=${1:?Pass the exact 40-character commit SHA requested for this run.}
if [[ ! "$requested_ref" =~ ^[0-9a-fA-F]{40}$ ]]; then
  echo "::error::Expected a full 40-character commit SHA, received '$requested_ref'." >&2
  exit 1
fi
actual_sha=$(git rev-parse HEAD)
expected_sha=$(git rev-parse "$requested_ref^{commit}")

if [[ "$actual_sha" != "$expected_sha" ]]; then
  echo "::error::Checked out $actual_sha instead of requested $expected_sha ($requested_ref)." >&2
  exit 1
fi

if [[ -n "${GITHUB_ENV:-}" ]]; then
  echo "GROUNDWORK_TESTED_SHA=$actual_sha" >> "$GITHUB_ENV"
fi
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  echo "Verified exact-head evidence for \`$actual_sha\`." >> "$GITHUB_STEP_SUMMARY"
fi

echo "$actual_sha"
