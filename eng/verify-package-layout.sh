#!/usr/bin/env bash
set -euo pipefail

packages_dir="${1:?usage: verify-package-layout.sh <packages-directory> <version>}"
version="${2:?usage: verify-package-layout.sh <packages-directory> <version>}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
allowlist="$repo_root/eng/public-packages.txt"

test -d "$packages_dir" || {
  echo "Package directory does not exist: $packages_dir" >&2
  exit 1
}

expected_ids=()
while IFS='|' read -r package_id project_path; do
  [[ -z "${package_id//[[:space:]]/}" || "$package_id" == \#* ]] && continue
  test -f "$repo_root/$project_path" || {
    echo "Allowlisted project is missing: $project_path" >&2
    exit 1
  }
  expected_ids+=("$package_id")
done < "$allowlist"

[[ "${#expected_ids[@]}" -eq 22 ]] || {
  echo "Expected 22 public packages, found ${#expected_ids[@]} in $allowlist" >&2
  exit 1
}

contains_expected_id() {
  local candidate="$1"
  local expected
  for expected in "${expected_ids[@]}"; do
    [[ "$expected" == "$candidate" ]] && return 0
  done
  return 1
}

for package_id in "${expected_ids[@]}"; do
  nupkg="$packages_dir/$package_id.$version.nupkg"
  snupkg="$packages_dir/$package_id.$version.snupkg"
  test -f "$nupkg" || {
    echo "Missing package: $nupkg" >&2
    exit 1
  }
  test -f "$snupkg" || {
    echo "Missing symbol package: $snupkg" >&2
    exit 1
  }
  symbol_entries="$(unzip -Z1 "$snupkg")"
  grep -Eq '\.pdb$' <<<"$symbol_entries" || {
    echo "Symbol package has no PDB: $snupkg" >&2
    exit 1
  }

  readme_found=false
  package_entries="$(unzip -Z1 "$nupkg")"
  while IFS= read -r entry; do
    if [[ "$entry" == *.md && "$entry" != */* ]]; then
      readme_found=true
      break
    fi
  done <<<"$package_entries"
  [[ "$readme_found" == true ]] || {
    echo "Package has no root README: $nupkg" >&2
    exit 1
  }

  nuspec="$(awk '/\.nuspec$/ { print; exit }' <<<"$package_entries")"
  test -n "$nuspec" || {
    echo "Package has no nuspec: $nupkg" >&2
    exit 1
  }
  metadata="$(unzip -p "$nupkg" "$nuspec")"
  grep -Fq '<repository type="git" url="https://github.com/valence-works/groundwork-v2.git"' <<<"$metadata" || {
    echo "Package is missing repository metadata: $nupkg" >&2
    exit 1
  }
  grep -Fq '<license type="expression">MIT</license>' <<<"$metadata" || {
    echo "Package is missing MIT license metadata: $nupkg" >&2
    exit 1
  }
done

for nupkg in "$packages_dir"/*.nupkg; do
  [[ -e "$nupkg" ]] || continue
  filename="${nupkg##*/}"
  suffix=".$version.nupkg"
  [[ "$filename" == *"$suffix" ]] || {
    echo "Package has an unexpected versioned filename: $filename" >&2
    exit 1
  }
  package_id="${filename%$suffix}"
  contains_expected_id "$package_id" || {
    echo "Unexpected package in release output: $filename" >&2
    exit 1
  }
done

for snupkg in "$packages_dir"/*.snupkg; do
  [[ -e "$snupkg" ]] || continue
  filename="${snupkg##*/}"
  suffix=".$version.snupkg"
  [[ "$filename" == *"$suffix" ]] || {
    echo "Symbol package has an unexpected versioned filename: $filename" >&2
    exit 1
  }
  package_id="${filename%$suffix}"
  contains_expected_id "$package_id" || {
    echo "Unexpected symbol package in release output: $filename" >&2
    exit 1
  }
  test -f "$packages_dir/$package_id.$version.nupkg" || {
    echo "Symbol package has no matching package: $filename" >&2
    exit 1
  }
done

echo "Validated ${#expected_ids[@]} public packages and symbol packages at version $version."
