#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd "$(dirname "$0")" && pwd)
repository_root=$(git -C "$script_directory/.." rev-parse --show-toplevel)
cd "$repository_root"

requested_sha=${1:?Pass the exact 40-character commit SHA to exercise.}
output_root=${2:-artifacts/recurrence/full-solution/$requested_sha}
lock_path=/tmp/groundwork-tests.lock
neighbor_pattern='[d]otnet[[:space:]]+test|[t]esthost|[v]stest'

if [[ "${GROUNDWORK_CONFIRM_IDLE_HOST:-}" != "true" ]]; then
  echo "Set GROUNDWORK_CONFIRM_IDLE_HOST=true only after reserving an otherwise idle host." >&2
  exit 1
fi

exec 9>"$lock_path"
if command -v flock >/dev/null 2>&1; then
  if ! flock -n 9; then
    echo "Could not acquire the exclusive recurrence lock at $lock_path." >&2
    exit 1
  fi
  lock_backend=flock
elif command -v lockf >/dev/null 2>&1; then
  if ! lockf -t 0 9; then
    echo "Could not acquire the exclusive recurrence lock at $lock_path." >&2
    exit 1
  fi
  lock_backend=lockf
else
  echo "Neither flock nor lockf is available; the recurrence run cannot be serialized." >&2
  exit 1
fi

actual_sha=$(bash eng/verify-exact-head.sh "$requested_sha")
if [[ -n "$(git status --porcelain)" ]]; then
  echo "Recurrence evidence requires a clean exact-head worktree." >&2
  exit 1
fi
if [[ -e "$output_root" ]]; then
  echo "Refusing to overwrite existing recurrence evidence at $output_root." >&2
  exit 1
fi

logical_cpus=$(getconf _NPROCESSORS_ONLN 2>/dev/null || sysctl -n hw.logicalcpu)
mkdir -p "$output_root"
manifest="$output_root/manifest.txt"
{
  echo "commit=$actual_sha"
  echo "started_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "host=$(hostname)"
  echo "logical_cpus=$logical_cpus"
  echo "lock_path=$lock_path"
  echo "lock_backend=$lock_backend"
  echo "idle_load_limit=1.0"
  uname -a
  dotnet --info
} > "$manifest"

check_no_neighboring_tests() {
  local phase=$1
  local neighbors
  neighbors=$(pgrep -fl "$neighbor_pattern" || true)
  if [[ -n "$neighbors" ]]; then
    {
      echo "neighbor_check=$phase"
      echo "$neighbors"
      echo "final_status=invalid-neighbor-$phase"
    } | tee -a "$manifest" >&2
    echo "A neighboring test process makes this recurrence run invalid." >&2
    return 1
  fi
}

record_idle_sample() {
  local sample=$1
  local snapshot load_one
  snapshot=$(uptime)
  load_one=$(sed -E 's/.*load average(s)?:[[:space:]]*([0-9]+([.][0-9]+)?).*/\2/' <<< "$snapshot")
  if [[ ! "$load_one" =~ ^[0-9]+([.][0-9]+)?$ ]]; then
    {
      echo "load_parse_failure=$snapshot"
      echo "final_status=invalid-load-sample-$sample"
    } | tee -a "$manifest" >&2
    return 1
  fi
  {
    echo "idle_sample_${sample}_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "idle_sample_${sample}_load_1m=$load_one"
  } >> "$manifest"
  if ! awk -v load="$load_one" 'BEGIN { exit !(load <= 1.0) }'; then
    {
      echo "load_refusal=One-minute load $load_one exceeds the idle-host limit of 1.0."
      echo "final_status=invalid-load-sample-$sample"
    } | tee -a "$manifest" >&2
    return 1
  fi
}

echo "Confirming a five-minute clean window before restore."
for sample in $(seq 1 11); do
  check_no_neighboring_tests "idle-sample-$sample"
  record_idle_sample "$sample"
  if [[ "$sample" -lt 11 ]]; then
    sleep 30
  fi
done

set +e
dotnet restore Groundwork.slnx --nologo 2>&1 | tee "$output_root/restore.log"
restore_status=${PIPESTATUS[0]}
set -e
echo "restore_status=$restore_status" >> "$manifest"
if [[ "$restore_status" -ne 0 ]]; then
  echo "final_status=restore-failed" >> "$manifest"
  exit "$restore_status"
fi

for iteration in $(seq 1 5); do
  check_no_neighboring_tests "before-run-$iteration"
  run_directory="$output_root/run-$iteration"
  mkdir -p "$run_directory"
  echo "run_${iteration}_started_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$manifest"

  set +e
  dotnet test Groundwork.slnx \
    --no-restore --configuration Release \
    --logger trx \
    --results-directory "$run_directory" \
    --blame-hang --blame-hang-timeout 20m --blame-hang-dump-type full \
    2>&1 | tee "$run_directory/console.log"
  test_status=${PIPESTATUS[0]}
  set -e

  echo "run_${iteration}_status=$test_status" >> "$manifest"
  echo "run_${iteration}_finished_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$manifest"
  if [[ "$test_status" -ne 0 ]]; then
    echo "final_status=failed-on-run-$iteration" >> "$manifest"
    exit "$test_status"
  fi
  if ! find "$run_directory" -type f -name '*.trx' -print -quit | grep -q .; then
    echo "final_status=missing-trx-on-run-$iteration" >> "$manifest"
    echo "Run $iteration passed without producing a TRX result." >&2
    exit 1
  fi
done

{
  echo "finished_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "final_status=passed-5-of-5"
} >> "$manifest"
echo "Five clean full-solution recurrence runs completed at $output_root."
