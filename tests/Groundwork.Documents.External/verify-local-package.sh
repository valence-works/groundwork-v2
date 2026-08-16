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

external_build_root="$build_root/external"
mkdir -p "$external_build_root/Source/Serialization"
cp "$repo_root/src/Groundwork.Documents/AssemblyInfo.cs" "$external_build_root/Source/AssemblyInfo.cs"
cp "$repo_root/src/Groundwork.Documents/DocumentUnit.cs" "$external_build_root/Source/DocumentUnit.cs"
cp "$repo_root/src/Groundwork.Documents/Serialization/"*.cs "$external_build_root/Source/Serialization/"
cp "$external_root/Groundwork.Documents.Source.csproj" "$external_build_root/Groundwork.Documents.Source.csproj"
cp "$external_root/Groundwork.Documents.External.csproj" "$external_build_root/Groundwork.Documents.External.csproj"
cp "$external_root/Program.cs" "$external_build_root/Program.cs"
cp "$external_root/NuGet.Config" "$external_build_root/NuGet.Config"
cp "$external_root/Groundwork.Documents.slnx" "$external_build_root/Groundwork.Documents.slnx"
cp "$external_root/Directory.Build.props" "$external_build_root/Directory.Build.props"

source_project="$external_build_root/Groundwork.Documents.Source.csproj"
consumer_project="$external_build_root/Groundwork.Documents.External.csproj"
solution="$external_build_root/Groundwork.Documents.slnx"
if grep -En '<ProjectReference\b|PackageReference Include="Groundwork\.Documents"|\.\./.*src' "$source_project" ||
  grep -En '<ProjectReference\b|\.\./.*src' "$consumer_project" ||
  ! grep -Eq 'PackageReference Include="Groundwork\.Documents"' "$consumer_project"; then
  echo "The isolated Documents solution must use copied source and package references only." >&2
  exit 1
fi
if ! grep -Eq 'Groundwork\.Documents\.Source\.csproj' "$solution" || ! grep -Eq 'Groundwork\.Documents\.External\.csproj' "$solution"; then
  echo "The isolated external solution must build both the copied source and package consumer projects." >&2
  exit 1
fi

msbuild_isolation_args=(
  -p:ImportDirectoryBuildTargets=false
  -p:ManagePackageVersionsCentrally=false
)
properties="$(dotnet msbuild "$source_project" -getProperty:PackageProjectUrl -getProperty:DirectoryBuildPropsPath --nologo "${msbuild_isolation_args[@]}")"
if printf '%s\n' "$properties" | grep -Eq '"PackageProjectUrl": "[^"]+"' ||
  printf '%s\n' "$properties" | grep -Fq "$repo_root" ||
  ! printf '%s\n' "$properties" | grep -Eq 'external/Directory\.Build\.props'; then
  echo "The isolated Documents source project did not use only its copied external MSBuild properties." >&2
  exit 1
fi

NUGET_PACKAGES="$package_cache" dotnet restore "$solution" --force --force-evaluate --packages "$package_cache" --nologo \
  -p:RestoreSources="$feed" -p:RestoreConfigFile="$external_build_root/NuGet.Config" \
  "${msbuild_isolation_args[@]}" -m:1 -v:q
NUGET_PACKAGES="$package_cache" dotnet build "$solution" -c Release --no-restore --nologo \
  "${msbuild_isolation_args[@]}" -m:1 -v:q
NUGET_PACKAGES="$package_cache" dotnet run --project "$consumer_project" -c Release --no-build --no-restore \
  "${msbuild_isolation_args[@]}"
