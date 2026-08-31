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

sha256_file() {
  local file="$1"
  local hash
  if command -v sha256sum >/dev/null 2>&1; then
    hash=$(sha256sum -- "$file" | awk '{ print $1 }')
  elif command -v shasum >/dev/null 2>&1; then
    hash=$(shasum -a 256 "$file" | awk '{ print $1 }')
  else
    echo "Neither sha256sum nor shasum is available." >&2
    return 1
  fi
  if [[ ! "$hash" =~ ^[0-9a-fA-F]{64}$ ]]; then
    echo "Could not hash benchmark result: $file" >&2
    return 1
  fi
  printf '%s\n' "$hash" | tr '[:upper:]' '[:lower:]'
}

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
  benchmarks --list flat \
  --filter '*PointRead*' '*CoveredQuery*' '*PagedQuery*' '*BatchedWrite*' '*UnitOfWorkCommit*' \
  | grep '^Groundwork\.Benchmarks\.StorageBenchmarks\.' \
  > "$output_root/key-scenarios.txt"
diff -u benchmarks/Groundwork.Benchmarks/evidence/key-scenarios.txt "$output_root/key-scenarios.txt"

dotnet run --project benchmarks/Groundwork.Benchmarks --no-restore --configuration Release -- \
  benchmarks --filter '*PointRead*' '*CoveredQuery*' '*PagedQuery*' '*BatchedWrite*' '*UnitOfWorkCommit*' \
  --artifacts "$output_root/benchmarkdotnet" \
  --exporters json markdown csv

shopt -s nullglob
benchmark_results=("$output_root"/benchmarkdotnet/results/*.json)
shopt -u nullglob
if [[ "${#benchmark_results[@]}" -ne 1 ]]; then
  echo "Expected exactly one BenchmarkDotNet JSON result, found ${#benchmark_results[@]}." >&2
  exit 1
fi
benchmark_result=${benchmark_results[0]}
benchmark_result_relative=${benchmark_result#"$output_root"/}
benchmark_result_sha256=$(sha256_file "$benchmark_result")
{
  echo "benchmark_result=$benchmark_result_relative"
  echo "benchmark_result_sha256=$benchmark_result_sha256"
} >> "$output_root/manifest.txt"

echo "Comparative evidence written to $output_root"
