#!/usr/bin/env bash
set -euo pipefail

output_directory="${1:?Usage: pack-public-packages.sh OUTPUT_DIRECTORY [PACKAGE_VERSION]}"
package_version="${2:-}"

rm -rf "$output_directory"
mkdir -p "$output_directory"

# One in-process MSBuild node, not reused. A reused worker node outlives the pack that started it
# and holds on to inherited stdout/stderr handles, so a caller that reads this script's output to
# end blocks long after the packages are written.
export MSBUILDDISABLENODEREUSE=1

pack_options=(
  --configuration Release
  --no-restore
  --output "$output_directory"
  --nologo
  --verbosity minimal
  -m:1
  -nodeReuse:false
  -p:ContinuousIntegrationBuild=true
  -p:IncludeSymbols=true
  -p:SymbolPackageFormat=snupkg
)

if [[ -n "$package_version" ]]; then
  # PackageVersion controls the .nupkg identity, while Version also flows into
  # AssemblyInformationalVersion (used by Groundwork.Tool --version). Keep both
  # identities aligned for controlled preview releases.
  pack_options+=("-p:PackageVersion=$package_version" "-p:Version=$package_version")
fi

while IFS='|' read -r package_id project_path; do
  [[ -z "${package_id//[[:space:]]/}" || "$package_id" == \#* ]] && continue
  dotnet pack "$project_path" "${pack_options[@]}"
done < eng/public-packages.txt
