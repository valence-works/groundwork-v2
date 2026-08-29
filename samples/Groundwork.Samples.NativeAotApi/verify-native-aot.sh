#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
sample_root="$repo_root/samples/Groundwork.Samples.NativeAotApi"
feed="${1:-$repo_root/artifacts/aot-packages}"
runtime="${2:-osx-arm64}"
evidence="${3:-$repo_root/artifacts/native-aot-sample/evidence.md}"
package_cache="$(mktemp -d)"
build_root="$(mktemp -d)"
sample_pid=""

cleanup() {
  if [[ -n "$sample_pid" ]] && kill -0 "$sample_pid" 2>/dev/null; then
    kill "$sample_pid" 2>/dev/null || true
    wait "$sample_pid" 2>/dev/null || true
  fi
  rm -rf "$package_cache" "$build_root"
}
trap cleanup EXIT

test -d "$feed" || {
  echo "Missing packed artifacts at '$feed'." >&2
  exit 1
}

version="$(find "$feed" -maxdepth 1 -name 'Groundwork.Sqlite.*.nupkg' -print -quit | sed -E 's#^.*/Groundwork\.Sqlite\.([0-9][^/]*)\.nupkg$#\1#')"
test -n "$version" || {
  echo "Could not determine the Groundwork.Sqlite package version in '$feed'." >&2
  exit 1
}

mkdir -p "$build_root/feed"
cp "$sample_root/Groundwork.Samples.NativeAotApi.csproj" "$build_root/"
cp "$sample_root/Program.cs" "$build_root/"
cp "$sample_root/TodoItem.cs" "$build_root/"
cp "$sample_root/NuGet.Config" "$build_root/"
cp "$feed"/Groundwork.*.nupkg "$build_root/feed/"

isolation_args=(
  -p:ImportDirectoryBuildProps=false
  -p:ImportDirectoryBuildTargets=false
  -p:ManagePackageVersionsCentrally=false
  -p:BaseIntermediateOutputPath="$build_root/obj/"
  -p:MSBuildProjectExtensionsPath="$build_root/obj/"
  -p:BaseOutputPath="$build_root/bin/"
  -p:GroundworkVersion="$version"
  -p:UsePackedGroundwork=true
  -p:PublishAot=true
)

NUGET_PACKAGES="$package_cache" dotnet restore "$build_root/Groundwork.Samples.NativeAotApi.csproj" \
  --runtime "$runtime" --force --force-evaluate --packages "$package_cache" --nologo \
  -p:RestoreConfigFile="$build_root/NuGet.Config" "${isolation_args[@]}" -m:1 -v:q
NUGET_PACKAGES="$package_cache" dotnet publish "$build_root/Groundwork.Samples.NativeAotApi.csproj" \
  --configuration Release --framework net10.0 --runtime "$runtime" --self-contained true \
  --output "$build_root/publish" --no-restore --nologo "${isolation_args[@]}" -m:1 -v:minimal

binary="$build_root/publish/Groundwork.Samples.NativeAotApi"
test -x "$binary"
native_description="$(file "$binary")"
echo "$native_description"
echo "$native_description" | grep -Eq '(Mach-O|ELF).*executable'

database="$build_root/native-aot-sample.db"
startup_runs="${GROUNDWORK_AOT_STARTUP_RUNS:-1}"
case "$startup_runs" in
  ''|*[!0-9]*|0) echo "GROUNDWORK_AOT_STARTUP_RUNS must be a positive integer." >&2; exit 1 ;;
esac

stop_sample() {
  if [[ -n "$sample_pid" ]] && kill -0 "$sample_pid" 2>/dev/null; then
    kill "$sample_pid" 2>/dev/null || true
    wait "$sample_pid" 2>/dev/null || true
  fi
  sample_pid=""
}

start_sample() {
  local apply_schema="$1"
  local label="$2"
  port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()')"
  sample_log="$build_root/$label.log"
  health_json="$build_root/$label-health.json"
  local start_ns
  local ready_ns
  local ready=false
  start_ns="$(python3 -c 'import time; print(time.monotonic_ns())')"
  ASPNETCORE_URLS="http://127.0.0.1:$port" \
  Groundwork__ConnectionString="Data Source=$database" \
  Groundwork__DevelopmentApplySchema="$apply_schema" \
    "$binary" >"$sample_log" 2>&1 &
  sample_pid=$!

  for _ in $(seq 1 500); do
    if curl --silent --fail "http://127.0.0.1:$port/health" >"$health_json" 2>/dev/null; then
      ready=true
      break
    fi
    if ! kill -0 "$sample_pid" 2>/dev/null; then
      break
    fi
    sleep 0.01
  done
  ready_ns="$(python3 -c 'import time; print(time.monotonic_ns())')"

  if [[ "$ready" != true ]]; then
    cat "$sample_log" >&2
    echo "Native AOT sample did not become ready." >&2
    exit 1
  fi

  grep -q 'GROUNDWORK_NATIVE_AOT_READY' "$sample_log"
  grep -q 'dynamic_codegen=0' "$sample_log"
  python3 -c 'import json,sys; assert json.load(open(sys.argv[1])) == {"status":"ready"}' "$health_json"
  startup_ms="$(( (ready_ns - start_ns) / 1000000 ))"
}

# The first launch deliberately includes schema creation and owns the functional HTTP proof. Startup
# evidence below reuses that deployed SQLite catalog with auto-apply disabled, so it measures service
# startup rather than first-deployment DDL.
start_sample true smoke

create_status="$(curl --silent --show-error --output "$build_root/created.json" --write-out '%{http_code}' \
  --header 'content-type: application/json' --data '{"id":"todo-1","title":"Ship Native AOT","isDone":false}' \
  "http://127.0.0.1:$port/todos")"
test "$create_status" = 201
python3 -c 'import json,sys; assert json.load(open(sys.argv[1])) == {"id":"todo-1","title":"Ship Native AOT","isDone":False}' "$build_root/created.json"

curl --silent --show-error --fail "http://127.0.0.1:$port/todos/todo-1" >"$build_root/read.json"
curl --silent --show-error --fail "http://127.0.0.1:$port/todos?done=false" >"$build_root/query.json"
python3 -c 'import json,sys; expected={"id":"todo-1","title":"Ship Native AOT","isDone":False}; assert json.load(open(sys.argv[1])) == expected; assert json.load(open(sys.argv[2])) == [expected]' "$build_root/read.json" "$build_root/query.json"

duplicate_status="$(curl --silent --show-error --output "$build_root/conflict.json" --write-out '%{http_code}' \
  --header 'content-type: application/json' --data '{"id":"todo-1","title":"Duplicate","isDone":false}' \
  "http://127.0.0.1:$port/todos")"
test "$duplicate_status" = 409

stop_sample

startup_values="$build_root/startup-ms.txt"
for run in $(seq 1 "$startup_runs"); do
  start_sample false "startup-$run"
  echo "$startup_ms" >>"$startup_values"
  stop_sample
done

read -r startup_median_ms startup_p95_ms < <(python3 - "$startup_values" <<'PY'
import math
import statistics
import sys

values = sorted(int(line) for line in open(sys.argv[1]) if line.strip())
print(round(statistics.median(values)), values[max(0, math.ceil(len(values) * .95) - 1)])
PY
)

binary_bytes="$(wc -c <"$binary" | tr -d ' ')"
publish_payload_bytes="$(find "$build_root/publish" -type f ! -name '*.pdb' \
  -exec sh -c 'for file do wc -c <"$file"; done' sh {} + | awk '{ total += $1 } END { print total }')"
commit="$(git -C "$repo_root" rev-parse HEAD)"

mkdir -p "$(dirname "$evidence")"
{
  echo "# Groundwork Native AOT minimal-API evidence"
  echo
  echo "- Commit: \`$commit\`"
  echo "- Runtime identifier: \`$runtime\`"
  echo "- Host: \`$(uname -s) $(uname -m)\`"
  echo "- Native executable: \`$native_description\`"
  echo "- Main executable size: \`$binary_bytes bytes\`"
  echo "- Self-contained deploy payload, excluding PDB files: \`$publish_payload_bytes bytes\`"
  echo "- Pre-applied SQLite startup repetitions: \`$startup_runs\`"
  echo "- Spawn-to-first-\`/health\` median: \`$startup_median_ms ms\`"
  echo "- Spawn-to-first-\`/health\` p95: \`$startup_p95_ms ms\`"
  echo "- Generated Records dynamic-code count: \`0\`"
  echo
  echo "Startup is measured with a monotonic clock from process launch to the first successful health"
  echo "response against an already-applied SQLite catalog. These observations are evidence, not a"
  echo "regression threshold. A separate schema-creating launch creates, reads, queries, and refuses a"
  echo "duplicate todo through the running native HTTP executable."
} | tee "$evidence"

echo "Native AOT minimal API passed its declaration, unit-of-work, query, and HTTP smoke proof."
