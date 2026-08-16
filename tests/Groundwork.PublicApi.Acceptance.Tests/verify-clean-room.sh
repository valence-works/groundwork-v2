#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
acceptance_root="$repo_root/tests/Groundwork.PublicApi.Acceptance.Tests"
consumer_root="$acceptance_root/Consumer"
package_cache="$(mktemp -d)"
build_root="$(mktemp -d)"
trap 'rm -rf "$package_cache" "$build_root"' EXIT

feed="${GROUNDWORK_PUBLIC_API_PACKAGES:-$repo_root/artifacts/packages}"
test -d "$feed" || {
  echo "Missing packed artifacts at '$feed'. Run 'dotnet pack Groundwork.slnx --configuration Release --output artifacts/packages' first." >&2
  exit 1
}

for required in \
  Groundwork.Kernel Groundwork.Query.Model Groundwork.Query.Linq Groundwork.Query.Planning \
  Groundwork.Records Groundwork.Store Groundwork.Records.Store Groundwork.Diagnostics \
  Groundwork.Substrate.Relational Groundwork.Sqlite Groundwork.Documents; do
  test -f "$feed/$required.1.0.0.nupkg" || {
    echo "The local feed is missing $required." >&2
    exit 1
  }
done

if grep -REn '<ProjectReference|Groundwork\.Testing|TestingAdapter|InternalsVisibleTo|System\.Reflection|\.\./.*src' "$consumer_root" --include='*.cs' --include='*.csproj'; then
  echo "The clean-room consumer contains a forbidden internal or source dependency." >&2
  exit 1
fi
while IFS= read -r approved; do
  test -z "$approved" && continue
  symbol="${approved##*.}"
  grep -Eq "typeof\(([^)]*\.)?$symbol(<>)?\)" "$consumer_root/PublicApiApprovalFixture.cs" || {
    echo "Approved public API symbol '$approved' is absent from the compile-time fixture." >&2
    exit 1
  }
done < "$consumer_root/public-api.approved.txt"

run_external_consumer() {
  local run_number="$1"
  local external_root="$build_root/run-$run_number"
  local intermediate="$external_root/obj"
  local output="$external_root/bin"
  mkdir -p "$external_root/feed"
  cp "$consumer_root/Groundwork.PublicApi.Consumer.csproj" "$external_root/"
  cp "$consumer_root/Program.cs" "$external_root/"
  cp "$consumer_root/PublicApiApprovalFixture.cs" "$external_root/"
  cp "$consumer_root/NuGet.Config" "$external_root/"
  cp "$feed"/Groundwork.*.nupkg "$external_root/feed/"

  if grep -En '<ProjectReference|Groundwork\.Testing|TestingAdapter|InternalsVisibleTo|\.\./.*src' "$external_root" --include='*.cs' --include='*.csproj'; then
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
    "${isolation_args[@]}" -m:1 -v:q
  NUGET_PACKAGES="$package_cache" dotnet build "$external_root/Groundwork.PublicApi.Consumer.csproj" \
    -c Release --no-restore --nologo "${isolation_args[@]}" -m:1 -v:q
  NUGET_PACKAGES="$package_cache" dotnet run --project "$external_root/Groundwork.PublicApi.Consumer.csproj" \
    -c Release --no-build --no-restore --nologo "${isolation_args[@]}"
}

run_external_consumer 1
run_external_consumer 2
echo "Groundwork public API clean-room proof passed twice."
