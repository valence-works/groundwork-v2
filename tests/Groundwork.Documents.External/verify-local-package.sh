#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
external_root="$repo_root/tests/Groundwork.Documents.External"
feed="$(mktemp -d)"
package_cache="$(mktemp -d)"
trap 'rm -rf "$feed" "$package_cache"' EXIT

NUGET_PACKAGES="$package_cache" dotnet pack "$repo_root/src/Groundwork.Kernel/Groundwork.Kernel.csproj" -c Release -o "$feed" /p:Version=1.0.0 --nologo -m:1 -v:q
NUGET_PACKAGES="$package_cache" dotnet pack "$repo_root/src/Groundwork.Query.Model/Groundwork.Query.Model.csproj" -c Release -o "$feed" /p:Version=1.0.0 --nologo -m:1 -v:q
NUGET_PACKAGES="$package_cache" dotnet pack "$repo_root/src/Groundwork.Query.Linq/Groundwork.Query.Linq.csproj" -c Release -o "$feed" /p:Version=1.0.0 --nologo -m:1 -v:q
NUGET_PACKAGES="$package_cache" dotnet pack "$repo_root/src/Groundwork.Records/Groundwork.Records.csproj" -c Release -o "$feed" /p:Version=1.0.0 --nologo -m:1 -v:q
NUGET_PACKAGES="$package_cache" dotnet pack "$repo_root/src/Groundwork.Documents/Groundwork.Documents.csproj" -c Release -o "$feed" /p:Version=1.0.0 --nologo -m:1 -v:q

NUGET_PACKAGES="$package_cache" dotnet build "$external_root/Groundwork.Documents.slnx" -c Release --nologo \
  -p:RestoreSources="$feed" -p:RestoreConfigFile="$external_root/NuGet.Config" -m:1 -v:q
NUGET_PACKAGES="$package_cache" dotnet run --project "$external_root/Groundwork.Documents.External.csproj" -c Release --no-build --no-restore
