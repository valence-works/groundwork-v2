#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
consumer_root="$repo_root/tests/Groundwork.Aot.Conformance"
feed="${1:-$repo_root/artifacts/aot-packages}"
runtime="${2:-osx-arm64}"
package_cache="$(mktemp -d)"
build_root="$(mktemp -d)"
trap 'rm -rf "$package_cache" "$build_root"' EXIT

test -d "$feed" || {
  echo "Missing packed artifacts at '$feed'." >&2
  exit 1
}

version="$(find "$feed" -maxdepth 1 -name 'Groundwork.Testing.*.nupkg' -print -quit | sed -E 's#^.*/Groundwork\.Testing\.([0-9][^/]*)\.nupkg$#\1#')"
test -n "$version" || {
  echo "Could not determine the Groundwork.Testing package version in '$feed'." >&2
  exit 1
}

mkdir -p "$build_root/feed"
cp "$consumer_root/Groundwork.Aot.Conformance.csproj" "$build_root/"
cp "$consumer_root/Program.cs" "$build_root/"
cp "$consumer_root/NuGet.Config" "$build_root/"
cp "$feed"/Groundwork.*.nupkg "$build_root/feed/"

if grep -REn '<ProjectReference|\.\./.*src' "$build_root" --include='*.cs' --include='*.csproj'; then
  echo "The Native AOT consumer contains a forbidden source dependency." >&2
  exit 1
fi

isolation_args=(
  -p:ImportDirectoryBuildProps=false
  -p:ImportDirectoryBuildTargets=false
  -p:ManagePackageVersionsCentrally=false
  -p:BaseIntermediateOutputPath="$build_root/obj/"
  -p:MSBuildProjectExtensionsPath="$build_root/obj/"
  -p:BaseOutputPath="$build_root/bin/"
  -p:GroundworkVersion="$version"
  -p:PublishAot=true
)

NUGET_PACKAGES="$package_cache" dotnet restore "$build_root/Groundwork.Aot.Conformance.csproj" \
  --runtime "$runtime" --force --force-evaluate --packages "$package_cache" --nologo \
  -p:RestoreConfigFile="$build_root/NuGet.Config" "${isolation_args[@]}" -m:1 -v:q
NUGET_PACKAGES="$package_cache" dotnet publish "$build_root/Groundwork.Aot.Conformance.csproj" \
  --configuration Release --framework net10.0 --runtime "$runtime" --self-contained true \
  --output "$build_root/publish" --no-restore --nologo "${isolation_args[@]}" -m:1 -v:minimal

binary="$build_root/publish/Groundwork.Aot.Conformance"
test -x "$binary"
native_description="$(file "$binary")"
echo "$native_description"
echo "$native_description" | grep -Eq '(Mach-O|ELF).*executable'
"$binary"
