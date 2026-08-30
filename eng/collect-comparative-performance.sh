#!/usr/bin/env bash
set -euo pipefail

requested_sha=${1:?Pass the exact 40-character commit SHA to measure.}
output_root=${2:-artifacts/performance/comparative/$requested_sha}

if [[ "${GROUNDWORK_CONFIRM_IDLE_HOST:-}" != "true" ]]; then
  echo "Set GROUNDWORK_CONFIRM_IDLE_HOST=true only after reserving an otherwise idle host." >&2
  exit 1
fi

actual_sha=$(bash eng/verify-exact-head.sh "$requested_sha")
if [[ -n "$(git status --porcelain)" ]]; then
  echo "Comparative evidence requires a clean exact-head worktree." >&2
  exit 1
fi

mkdir -p "$output_root/benchmarkdotnet"
{
  echo "commit=$actual_sha"
  echo "schema_fingerprint=$(<benchmarks/Groundwork.Benchmarks/evidence/schema-fingerprint.txt)"
  echo "captured_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "host=$(hostname)"
  echo "host_idle_confirmation=true"
  uname -a
  uptime
  dotnet --info
} > "$output_root/manifest.txt"

dotnet restore benchmarks/Groundwork.Benchmarks/Groundwork.Benchmarks.csproj
dotnet list benchmarks/Groundwork.Benchmarks/Groundwork.Benchmarks.csproj package --include-transitive \
  > "$output_root/packages.txt"

dotnet run --project benchmarks/Groundwork.Benchmarks --no-restore --configuration Release -- \
  benchmarks --list flat --filter '*PointRead*' '*CoveredQuery*' '*BatchedWrite*' \
  | grep '^Groundwork\.Benchmarks\.StorageBenchmarks\.' \
  > "$output_root/key-scenarios.txt"
diff -u benchmarks/Groundwork.Benchmarks/evidence/key-scenarios.txt "$output_root/key-scenarios.txt"

dotnet run --project benchmarks/Groundwork.Benchmarks --no-restore --configuration Release -- \
  benchmarks --filter '*PointRead*' '*CoveredQuery*' '*BatchedWrite*' \
  --artifacts "$output_root/benchmarkdotnet" \
  --exporters json markdown csv

echo "Comparative evidence written to $output_root"
