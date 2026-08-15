#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
external_root="$repo_root/tests/Groundwork.Documents.External"
feed="$(mktemp -d)"
package_cache="$(mktemp -d)"
build_root="$(mktemp -d)"
trap 'rm -rf "$feed" "$package_cache" "$build_root"' EXIT

pack_public_project() {
  local project="$1"
  dotnet restore "$project" --force --force-evaluate --nologo -m:1 -v:q
  dotnet pack "$project" -c Release -o "$feed" /p:Version=1.0.0 --no-restore --nologo -m:1 -v:q
}

pack_public_project "$repo_root/src/Groundwork.Kernel/Groundwork.Kernel.csproj"
pack_public_project "$repo_root/src/Groundwork.Query.Model/Groundwork.Query.Model.csproj"
pack_public_project "$repo_root/src/Groundwork.Query.Linq/Groundwork.Query.Linq.csproj"
pack_public_project "$repo_root/src/Groundwork.Records/Groundwork.Records.csproj"
pack_public_project "$repo_root/src/Groundwork.Store/Groundwork.Store.csproj"
pack_public_project "$repo_root/src/Groundwork.Documents/Groundwork.Documents.csproj"

source_project="$external_root/Groundwork.Documents.Source.csproj"
if rg -n '<ProjectReference\b' "$source_project"; then
  echo "The isolated Documents source project must not contain a ProjectReference." >&2
  exit 1
fi

msbuild_isolation_args=(
  -p:ImportDirectoryBuildProps=false
  -p:ImportDirectoryBuildTargets=false
  -p:ManagePackageVersionsCentrally=false
)
properties="$(dotnet msbuild "$source_project" -getProperty:PackageProjectUrl -getProperty:DirectoryBuildPropsPath --nologo "${msbuild_isolation_args[@]}")"
if printf '%s\n' "$properties" | rg -q '"PackageProjectUrl": "[^"]+"|"DirectoryBuildPropsPath": "[^"]+"'; then
  echo "The isolated Documents source project imported repository MSBuild properties." >&2
  exit 1
fi

build_external_project() {
  local name="$1"
  local project="$2"
  local intermediate="$build_root/$name/obj/"
  local output="$build_root/$name/bin/"
  NUGET_PACKAGES="$package_cache" dotnet restore "$project" --force --force-evaluate --packages "$package_cache" --nologo \
    -p:RestoreSources="$feed" -p:RestoreConfigFile="$external_root/NuGet.Config" \
    -p:BaseIntermediateOutputPath="$intermediate" -p:MSBuildProjectExtensionsPath="$intermediate" -p:BaseOutputPath="$output" \
    "${msbuild_isolation_args[@]}" -m:1 -v:q
  NUGET_PACKAGES="$package_cache" dotnet build "$project" -c Release --no-restore --nologo \
    -p:BaseIntermediateOutputPath="$intermediate" -p:MSBuildProjectExtensionsPath="$intermediate" -p:BaseOutputPath="$output" \
    "${msbuild_isolation_args[@]}" -m:1 -v:q
}

build_external_project source "$source_project"
build_external_project consumer "$external_root/Groundwork.Documents.External.csproj"
NUGET_PACKAGES="$package_cache" dotnet run --project "$external_root/Groundwork.Documents.External.csproj" -c Release --no-build --no-restore \
  -p:BaseIntermediateOutputPath="$build_root/consumer/obj/" -p:MSBuildProjectExtensionsPath="$build_root/consumer/obj/" -p:BaseOutputPath="$build_root/consumer/bin/" \
  "${msbuild_isolation_args[@]}"
