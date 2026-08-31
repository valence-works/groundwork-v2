#!/usr/bin/env bash
set -euo pipefail

requested_sha=${1:?Pass the exact 40-character commit SHA to measure.}
output_root=${2:-artifacts/performance/comparative/$requested_sha}
benchmark_output_root="$output_root/benchmarkdotnet"
controlled_host_id=${GROUNDWORK_CONTROLLED_HOST_ID:-}

if [[ "${GROUNDWORK_CONFIRM_IDLE_HOST:-}" != "true" ]]; then
  echo "Set GROUNDWORK_CONFIRM_IDLE_HOST=true only after reserving an otherwise idle host." >&2
  exit 1
fi

if [[ -e "$output_root" || -L "$output_root" ]]; then
  echo "Comparative evidence output must be a new directory." >&2
  exit 1
fi

if [[ ! "$controlled_host_id" =~ ^[a-zA-Z0-9][a-zA-Z0-9._-]{2,63}$ ]]; then
  echo "Set GROUNDWORK_CONTROLLED_HOST_ID to a stable, publish-safe host identifier (3-64 characters)." >&2
  exit 1
fi

if [[ "$output_root" == /* || "$output_root" == "." || "$output_root" == ".." ||
      "$output_root" == ../* || "$output_root" == */../* || "$output_root" == */.. ]]; then
  echo "Comparative evidence output must be a non-parent-traversing relative directory." >&2
  exit 1
fi

candidate_path=
IFS='/' read -r -a output_components <<< "$output_root"
for output_component in "${output_components[@]}"; do
  [[ -n "$output_component" && "$output_component" != "." ]] || continue
  candidate_path=${candidate_path:+$candidate_path/}$output_component
  if [[ -L "$candidate_path" ]]; then
    echo "Comparative evidence output must not traverse symbolic links." >&2
    exit 1
  fi
done

workspace_root=$(pwd -P)
actual_sha=$(bash eng/verify-exact-head.sh "$requested_sha")
user_home=${HOME:-}
machine_hostname=$(hostname)
private_user_segment="/$(id -un)/"
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

sanitize_private_paths() {
  local line
  while IFS= read -r line; do
    line=${line//"$workspace_root"/<workspace>}
    if [[ -n "$user_home" ]]; then
      line=${line//"$user_home"/<home>}
    fi
    line=${line//"$machine_hostname"/<host>}
    printf '%s\n' "$line"
  done
}

remove_private_logs() {
  if [[ -d "$benchmark_output_root" ]]; then
    find "$benchmark_output_root" -type f -name '*.log' -delete
  fi
}

require_publication_safe_output() {
  local private_value
  for private_value in "$workspace_root" "$user_home" "$machine_hostname" "$private_user_segment"; do
    if [[ -n "$private_value" ]] && grep -R -I -F -q -- "$private_value" "$output_root"; then
      echo "Comparative evidence contains private workstation identity." >&2
      return 1
    fi
  done
  if find "$output_root" -type f -name '*.log' -print -quit | grep -q .; then
    echo "Comparative evidence contains a private BenchmarkDotNet execution log." >&2
    return 1
  fi
}

mkdir -p "$benchmark_output_root"
resolved_output_root=$(cd "$output_root" && pwd -P)
resolved_benchmark_output_root=$(cd "$benchmark_output_root" && pwd -P)
if [[ "$resolved_output_root" != "$workspace_root/"* ||
      "$resolved_benchmark_output_root" != "$resolved_output_root/benchmarkdotnet" ]]; then
  echo "Comparative evidence output resolved outside the workspace." >&2
  exit 1
fi
trap remove_private_logs EXIT
{
  echo "commit=$actual_sha"
  echo "schema_fingerprint=$(<benchmarks/Groundwork.Benchmarks/evidence/schema-fingerprint.txt)"
  echo "captured_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "host=$controlled_host_id"
  echo "host_idle_confirmation=true"
  echo "os_name=$(uname -s)"
  echo "kernel_release=$(uname -r)"
  echo "architecture=$(uname -m)"
  uptime
  dotnet --info | sanitize_private_paths
} > "$output_root/manifest.txt"

dotnet restore benchmarks/Groundwork.Benchmarks/Groundwork.Benchmarks.csproj
dotnet list benchmarks/Groundwork.Benchmarks/Groundwork.Benchmarks.csproj package --include-transitive \
  | awk '
      { sub(/[[:space:]]+$/, ""); lines[++count] = $0 }
      END {
        while (count > 0 && lines[count] == "") count--
        for (line_number = 1; line_number <= count; line_number++) print lines[line_number]
      }
    ' \
  > "$output_root/packages.txt"

dotnet run --project benchmarks/Groundwork.Benchmarks --no-restore --configuration Release -- \
  benchmarks --list flat \
  --filter '*PointRead*' '*CoveredQuery*' '*PagedQuery*' '*BatchedWrite*' '*UnitOfWorkCommit*' \
  | grep '^Groundwork\.Benchmarks\.StorageBenchmarks\.' \
  > "$output_root/key-scenarios.txt"
diff -u benchmarks/Groundwork.Benchmarks/evidence/key-scenarios.txt "$output_root/key-scenarios.txt"

dotnet run --project benchmarks/Groundwork.Benchmarks --no-restore --configuration Release -- \
  benchmarks --filter '*PointRead*' '*CoveredQuery*' '*PagedQuery*' '*BatchedWrite*' '*UnitOfWorkCommit*' \
  --artifacts "$benchmark_output_root" \
  --exporters json markdown csv

# BenchmarkDotNet's execution log embeds absolute checkout paths. The structured JSON, Markdown,
# and CSV reports retain the reviewed measurement data without publishing workstation identity.
remove_private_logs

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

require_publication_safe_output
echo "Comparative evidence written to $output_root"
