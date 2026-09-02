#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
acceptance_root="$repo_root/tests/Groundwork.PublicApi.Acceptance.Tests"
consumer_root="$acceptance_root/Consumer"
package_cache="$(mktemp -d)"
build_root="$(mktemp -d)"
trap 'rm -rf "$package_cache" "$build_root"' EXIT

remote_only="${GROUNDWORK_PUBLIC_API_REMOTE_ONLY:-false}"
feedz_source="${GROUNDWORK_PUBLIC_API_FEEDZ_SOURCE:-}"
version="${GROUNDWORK_PUBLIC_API_VERSION:-}"
if [[ "$remote_only" == true ]]; then
  test -n "$feedz_source" || {
    echo "GROUNDWORK_PUBLIC_API_FEEDZ_SOURCE is required in remote-only mode." >&2
    exit 1
  }
  current_release="$(sed -n 's:.*<GroundworkCurrentRelease>\(.*\)</GroundworkCurrentRelease>.*:\1:p' "$repo_root/Directory.Build.props")"
  test -n "$current_release" || {
    echo "Could not determine GroundworkCurrentRelease from Directory.Build.props." >&2
    exit 1
  }
  version="${version:-$current_release}"
  [[ "$version" == "$current_release" ]] || {
    echo "Remote clean-room version '$version' is not GroundworkCurrentRelease '$current_release'." >&2
    exit 1
  }
else
  feed="${GROUNDWORK_PUBLIC_API_PACKAGES:-$repo_root/artifacts/acceptance-packages}"
  test -d "$feed" || {
    echo "Missing packed artifacts at '$feed'. Run 'eng/pack-public-packages.sh artifacts/acceptance-packages' first." >&2
    exit 1
  }
  if [[ -z "$version" ]]; then
    version="$(find "$feed" -maxdepth 1 -name 'Groundwork.Documents.*.nupkg' -print -quit | sed -E 's#^.*/Groundwork\.Documents\.([0-9][^/]*)\.nupkg$#\1#')"
  fi
fi
test -n "$version" || {
  echo "Could not determine the package version." >&2
  exit 1
}

if [[ "$remote_only" != true ]]; then
  for required in \
    Groundwork.Kernel Groundwork.Query.Model Groundwork.Query.Linq Groundwork.Query.Linq.Execution \
    Groundwork.Query.Planning Groundwork.Schema \
    Groundwork.Records Groundwork.Store Groundwork.Records.Store Groundwork.Diagnostics \
    Groundwork.Substrate.Relational Groundwork.Sqlite Groundwork.MySql Groundwork.Documents \
    Groundwork.EntityFrameworkCore Groundwork.Testing Groundwork.Tool; do
    test -f "$feed/$required.$version.nupkg" || {
      echo "The local feed is missing $required.$version." >&2
      exit 1
    }
  done
fi

if grep -REn '<ProjectReference|TestingAdapter|InternalsVisibleTo|System\.Reflection|\.\./.*src' "$consumer_root" --include='*.cs' --include='*.csproj'; then
  echo "The clean-room consumer contains a forbidden internal or source dependency." >&2
  exit 1
fi
while IFS= read -r approved; do
  test -z "$approved" && continue
  symbol="${approved##*.}"
  grep -Eq "typeof\(([^)]*\.)?$symbol(<[^)]*>)?\)" "$consumer_root/PublicApiApprovalFixture.cs" || {
    echo "Approved public API symbol '$approved' is absent from the compile-time fixture." >&2
    exit 1
  }
done < "$consumer_root/public-api.approved.txt"

run_external_consumer() {
  local run_number="$1"
  local framework="$2"
  local external_root="$build_root/run-$run_number"
  local intermediate="$external_root/obj"
  local output="$external_root/bin"
  mkdir -p "$external_root/feed"
  cp "$consumer_root/Groundwork.PublicApi.Consumer.csproj" "$external_root/"
  cp "$consumer_root/Program.cs" "$external_root/"
  cp "$consumer_root/PublicApiApprovalFixture.cs" "$external_root/"
  if [[ "$remote_only" == true ]]; then
    sed \
      -e 's#key="groundwork-local" value="./feed"#key="groundwork-feedz" value="'"$feedz_source"'"#' \
      -e 's#key="groundwork-local"#key="groundwork-feedz"#' \
      "$consumer_root/NuGet.Config" > "$external_root/NuGet.Config"
    if grep -Eq 'groundwork-local|value="\./feed"' "$external_root/NuGet.Config"; then
      echo "The remote clean-room consumer retained a local package source." >&2
      exit 1
    fi
  else
    cp "$consumer_root/NuGet.Config" "$external_root/"
    cp "$feed"/Groundwork.*.nupkg "$external_root/feed/"
  fi

  if grep -En '<ProjectReference|TestingAdapter|InternalsVisibleTo|\.\./.*src' "$external_root" --include='*.cs' --include='*.csproj'; then
    echo "The copied consumer contains a forbidden dependency." >&2
    exit 1
  fi

  isolation_args=(
    -p:ImportDirectoryBuildProps=false
    -p:ImportDirectoryBuildTargets=false
    -p:ManagePackageVersionsCentrally=false
    -p:BaseIntermediateOutputPath="$intermediate/"
    -p:MSBuildProjectExtensionsPath="$intermediate/"
    -p:BaseOutputPath="$output/"
  )
  NUGET_PACKAGES="$package_cache" dotnet restore "$external_root/Groundwork.PublicApi.Consumer.csproj" \
    --force --force-evaluate --packages "$package_cache" --nologo \
    -p:RestoreConfigFile="$external_root/NuGet.Config" \
    -p:GroundworkVersion="$version" \
    "${isolation_args[@]}" -m:1 -v:q
  NUGET_PACKAGES="$package_cache" dotnet build "$external_root/Groundwork.PublicApi.Consumer.csproj" \
    -c Release --framework "$framework" --no-restore --nologo -p:GroundworkVersion="$version" "${isolation_args[@]}" -m:1 -v:q
  NUGET_PACKAGES="$package_cache" dotnet run --project "$external_root/Groundwork.PublicApi.Consumer.csproj" \
    -c Release --framework "$framework" --no-build --no-restore --nologo -p:GroundworkVersion="$version" "${isolation_args[@]}"

  tool_root="$external_root/tool"
  mkdir -p "$tool_root"
  NUGET_PACKAGES="$package_cache" dotnet tool install Groundwork.Tool --version "$version" \
    --tool-path "$tool_root" --configfile "$external_root/NuGet.Config" --no-cache --verbosity quiet
  test "$("$tool_root/groundwork" --version)" = "Groundwork.Tool $version"

  schema_file="$external_root/groundwork.schema.json"
  database="$external_root/groundwork.db"
  printf '%s\n' '{"tables":[{"name":"tickets","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied"}],"key":["id"],"indexes":[]}]}' > "$schema_file"

  "$tool_root/groundwork" apply --help > "$external_root/apply-help.txt"
  grep -Fq 'Usage: groundwork apply' "$external_root/apply-help.txt"
  "$tool_root/groundwork" apply --schema "$schema_file" --provider sqlite \
    --database "$database" --safe --output json > "$external_root/apply-first.json"
  test -f "$database"
  "$tool_root/groundwork" apply --schema "$schema_file" --provider sqlite \
    --database "$database" --safe --output json > "$external_root/apply-second.json"
  grep -Eq '"targetMutated"[[:space:]]*:[[:space:]]*false' "$external_root/apply-second.json"
  "$tool_root/groundwork" plan --schema "$schema_file" --provider sqlite \
    --database "$database" --output json > "$external_root/plan.json"
  grep -Eq '"targetMutated"[[:space:]]*:[[:space:]]*false' "$external_root/plan.json"
}

# Twice on the primary framework, because a second clean build from the same artifacts is what
# catches a first-run-only success. Then once on every other framework the runtime packages ship:
# a package that restores and runs on one target is no evidence about the other.
primary_framework=net10.0
run_external_consumer 1 "$primary_framework"
run_external_consumer 2 "$primary_framework"
echo "Groundwork public API clean-room proof passed twice on $primary_framework."

run_number=2
for framework in $(sed -n 's:.*<TargetFrameworks>\(.*\)</TargetFrameworks>.*:\1:p' \
                     "$consumer_root/Groundwork.PublicApi.Consumer.csproj" | tr ';' ' '); do
  [[ "$framework" == "$primary_framework" ]] && continue
  run_number=$((run_number + 1))
  run_external_consumer "$run_number" "$framework"
  echo "Groundwork public API clean-room proof passed on $framework."
done
