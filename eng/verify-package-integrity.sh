#!/usr/bin/env bash
set -euo pipefail

operation="${1:-}"
packages_dir="${2:-}"
version="${3:-}"
manifest_name="package-sha256sums.txt"

usage() {
  echo "Usage: $0 <create|verify> <packages-directory> <version>" >&2
  echo "       $0 digest <packages-directory>" >&2
  exit 2
}

[[ "$operation" == "create" || "$operation" == "verify" || "$operation" == "digest" ]] || usage
[[ -n "$packages_dir" ]] || usage
[[ "$operation" == "digest" || -n "$version" ]] || usage
test -d "$packages_dir" || {
  echo "Package directory does not exist: $packages_dir" >&2
  exit 1
}

manifest="$packages_dir/$manifest_name"

die() {
  echo "Package integrity check failed: $*" >&2
  exit 1
}

sha256_file() {
  local file="$1"
  local hash
  if command -v sha256sum >/dev/null 2>&1; then
    hash="$(sha256sum -- "$file" | awk '{ print $1 }')"
  elif command -v shasum >/dev/null 2>&1; then
    hash="$(shasum -a 256 "$file" | awk '{ print $1 }')"
  else
    die "neither sha256sum nor shasum is available"
  fi
  [[ "$hash" =~ ^[0-9a-fA-F]{64}$ ]] || die "could not hash file: $file"
  printf '%s\n' "$hash" | tr '[:upper:]' '[:lower:]'
}

package_names() {
  while IFS= read -r -d '' path; do
    printf '%s\n' "${path#"$packages_dir"/}"
  done < <(find "$packages_dir" \( -name '*.nupkg' -o -name '*.snupkg' \) -print0) |
    LC_ALL=C sort
}

if [[ "$operation" == "digest" ]]; then
  test -f "$manifest" || die "manifest is missing: $manifest"
  sha256_file "$manifest"
  exit 0
fi

validate_package_names() {
  local package_name package_path
  while IFS= read -r package_name; do
    [[ -n "$package_name" ]] || continue
    [[ "$package_name" != */* ]] || die "package is not at the artifact root: $package_name"
    case "$package_name" in
      *."$version".nupkg|*."$version".snupkg) ;;
      *) die "package has an unexpected versioned filename: $package_name" ;;
    esac
    package_path="$packages_dir/$package_name"
    [[ -f "$package_path" && ! -L "$package_path" ]] ||
      die "package is not a regular file: $package_name"
  done
}

if [[ "$operation" == "create" ]]; then
  package_list="$(package_names)"
  [[ -n "$package_list" ]] || die "no .nupkg or .snupkg files were found"
  validate_package_names <<<"$package_list"

  manifest_tmp="$manifest.tmp.$$"
  trap 'rm -f "$manifest_tmp"' EXIT
  : > "$manifest_tmp"
  while IFS= read -r package_name; do
    [[ -n "$package_name" ]] || continue
    hash="$(sha256_file "$packages_dir/$package_name")"
    printf '%s  %s\n' "$hash" "$package_name" >> "$manifest_tmp"
  done <<<"$package_list"
  mv -f "$manifest_tmp" "$manifest"
  echo "Wrote $manifest for $(wc -l < "$manifest" | tr -d ' ') packages."
  exit 0
fi

test -f "$manifest" || die "manifest is missing: $manifest"

manifest_tmp="$(mktemp)"
actual_tmp="$(mktemp)"
expected_tmp="$(mktemp)"
duplicates_tmp="$(mktemp)"
trap 'rm -f "$manifest_tmp" "$actual_tmp" "$expected_tmp" "$duplicates_tmp"' EXIT

# Parse the manifest before hashing so an artifact cannot redirect the check to a
# path outside the downloaded package directory. Package names produced by NuGet are simple
# filenames; a slash, blank filename, duplicate, or malformed digest is refused.
while IFS= read -r line || [[ -n "$line" ]]; do
  [[ "$line" =~ ^[0-9a-fA-F]{64}[[:space:]][[:space:]][^[:space:]][^/]*$ ]] ||
    die "manifest contains a malformed entry"
  package_name="${line#*  }"
  [[ "$package_name" == *.nupkg || "$package_name" == *.snupkg ]] ||
    die "manifest contains a non-package entry: $package_name"
  printf '%s\n' "$package_name" >> "$manifest_tmp"
done < "$manifest"

[[ -s "$manifest_tmp" ]] || die "manifest contains no package entries"
LC_ALL=C sort "$manifest_tmp" | uniq -d > "$duplicates_tmp"
[[ ! -s "$duplicates_tmp" ]] || die "manifest contains duplicate package entries: $(tr '\n' ' ' < "$duplicates_tmp")"

package_names > "$actual_tmp"
validate_package_names < "$actual_tmp"
LC_ALL=C sort "$manifest_tmp" > "$expected_tmp"
if ! diff -u "$expected_tmp" "$actual_tmp"; then
  die "manifest package set does not exactly match downloaded .nupkg/.snupkg files"
fi

while IFS= read -r line; do
  expected_hash="${line%%  *}"
  package_name="${line#*  }"
  actual_hash="$(sha256_file "$packages_dir/$package_name")"
  [[ "$actual_hash" == "$expected_hash" ]] ||
    die "digest mismatch for package: $package_name"
done < "$manifest"

echo "Verified the exact $(wc -l < "$actual_tmp" | tr -d ' ') package set and SHA-256 manifest."
