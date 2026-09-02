#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="${1:-}"
version="${2:-}"
report="${3:-}"
test -n "$feed" && test -n "$version" && test -n "$report" || {
  echo "Usage: $0 <feed-service-index> <exact-version> <publication-safe-report>" >&2
  exit 2
}

report_dir="$(dirname "$report")"
mkdir -p "$report_dir"
start_utc="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
start_epoch="$(date +%s)"
source_ref="${GITHUB_REF:-unknown}"
source_sha="${GITHUB_SHA:-unknown}"
if [[ "$source_ref" == unknown ]]; then
  source_ref="$(git -C "$repo_root" symbolic-ref --short HEAD 2>/dev/null || printf 'detached')"
fi
if [[ "$source_sha" == unknown ]]; then
  source_sha="$(git -C "$repo_root" rev-parse HEAD 2>/dev/null || printf 'unknown')"
fi
os_info="$(uname -srm)"
dotnet_version="$(dotnet --version 2>/dev/null || printf 'unavailable')"
current_release=""
status=failed
failed_step="not started"
declare_status=not-run
apply_status=not-run
write_status=not-run
query_status=not-run
aggregation_status=not-run
evidence_root=""

write_report() {
  local end_utc end_epoch elapsed
  end_utc="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
  end_epoch="$(date +%s)"
  elapsed=$((end_epoch - start_epoch))
  cat > "$report" <<EOF
# Groundwork newcomer SQLite evidence

- Result: **$status**
- Feed source: \`$feed\`
- Requested version: \`$version\`
- GroundworkCurrentRelease: \`${current_release:-unavailable}\`
- Source ref: \`$source_ref\`
- Checkout SHA: \`$source_sha\`
- Started (UTC): \`$start_utc\`
- Finished (UTC): \`$end_utc\`
- Elapsed seconds: \`$elapsed\`
- Runner OS: \`$os_info\`
- .NET SDK: \`$dotnet_version\`

## Steps

| Step | Outcome |
| --- | --- |
| Declare the \`visits\` table and \`by_customer\` index | $declare_status |
| Apply the declared SQLite schema | $apply_status |
| Write three rows | $write_status |
| Execute the index-covered customer query | $query_status |
| Execute the declared customer aggregation | $aggregation_status |

The journey uses a temporary package consumer with \`Groundwork.Sqlite\` and
\`Groundwork.Records.Store\` restored from the Feedz source above. No local package artifacts,
repository project references, internal APIs, reflection shortcuts, or raw provider queries are
used. The temporary consumer and raw command output are intentionally not retained in this report.
EOF
  if [[ "$status" != passed ]]; then
    printf '\nFailed step: `%s`\n' "$failed_step" >> "$report"
  fi
}
trap write_report EXIT

failed_step="version validation"
if [[ -f "$repo_root/Directory.Build.props" ]]; then
  current_release="$(sed -n 's:.*<GroundworkCurrentRelease>\(.*\)</GroundworkCurrentRelease>.*:\1:p' "$repo_root/Directory.Build.props")"
fi
test -n "$current_release" || {
  echo "Could not determine GroundworkCurrentRelease from Directory.Build.props." >&2
  exit 1
}
[[ "$version" == "$current_release" ]] || {
  echo "Newcomer evidence version '$version' is not GroundworkCurrentRelease '$current_release'." >&2
  exit 1
}

evidence_root="$(mktemp -d)"
consumer_root="$evidence_root/consumer"
package_cache="$evidence_root/packages"
run_log="$evidence_root/run.log"
trap 'write_report; rm -rf "$evidence_root"' EXIT

failed_step="create the temporary package consumer"
dotnet new console --framework net10.0 --name GroundworkNewcomer --output "$consumer_root" --no-restore >/dev/null
cp "$repo_root/docs/v2/newcomer-sqlite/Program.cs" "$consumer_root/Program.cs"
cat > "$consumer_root/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="groundwork-feedz" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="groundwork-feedz">
      <package pattern="Groundwork.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF
declare_status=passed

failed_step="add the package references"
dotnet add "$consumer_root/GroundworkNewcomer.csproj" package Groundwork.Sqlite \
  --version "$version" --no-restore >/dev/null
dotnet add "$consumer_root/GroundworkNewcomer.csproj" package Groundwork.Records.Store \
  --version "$version" --no-restore >/dev/null
if grep -En '<ProjectReference|InternalsVisibleTo|\.\./|GroundworkV2' \
  "$consumer_root/Program.cs" "$consumer_root/GroundworkNewcomer.csproj"; then
  echo "The newcomer fixture contains a local or internal dependency." >&2
  exit 1
fi
if grep -Eq 'groundwork-local|value="\./feed"' "$consumer_root/NuGet.Config"; then
  echo "The newcomer fixture retained a local package source." >&2
  exit 1
fi

failed_step="restore the package consumer from Feedz"
NUGET_PACKAGES="$package_cache" dotnet restore "$consumer_root/GroundworkNewcomer.csproj" \
  --configfile "$consumer_root/NuGet.Config" --force --no-cache --nologo -v:q

failed_step="apply the schema and run the SQLite journey"
(cd "$consumer_root" && NUGET_PACKAGES="$package_cache" dotnet run \
  --project "$consumer_root/GroundworkNewcomer.csproj" \
  --configuration Release --no-restore --nologo > "$run_log" 2>&1)
cat "$run_log"
grep -Fq 'schema=applied' "$run_log"
apply_status=passed
grep -Fq 'rows_inserted=3' "$run_log"
write_status=passed
grep -Fq 'covered_query=ada:2' "$run_log"
query_status=passed
grep -Fq 'declared_aggregation=ada:2' "$run_log"
aggregation_status=passed
grep -Fq 'newcomer_sqlite=passed' "$run_log"
status=passed
failed_step=""
echo "Newcomer SQLite evidence passed for Groundwork $version."
