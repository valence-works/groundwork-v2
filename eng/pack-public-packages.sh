#!/usr/bin/env bash
set -euo pipefail

output_directory="${1:?Usage: pack-public-packages.sh OUTPUT_DIRECTORY [PACKAGE_VERSION]}"
package_version="${2:-}"

rm -rf "$output_directory"
mkdir -p "$output_directory"

pack_options=(
  --configuration Release
  --no-restore
  --output "$output_directory"
  --nologo
  --verbosity minimal
  -p:ContinuousIntegrationBuild=true
  -p:IncludeSymbols=true
  -p:SymbolPackageFormat=snupkg
)

if [[ -n "$package_version" ]]; then
  pack_options+=("-p:PackageVersion=$package_version")
fi

while IFS='|' read -r package_id project_path; do
  [[ -z "${package_id//[[:space:]]/}" || "$package_id" == \#* ]] && continue
  dotnet pack "$project_path" "${pack_options[@]}"
done < eng/public-packages.txt
