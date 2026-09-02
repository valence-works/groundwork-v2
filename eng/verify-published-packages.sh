#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="${1:-}"
version="${2:-}"
expected_packages="${3:-}"
test -n "$feed" || {
  echo "Usage: $0 <feed-service-index> <exact-version> [expected-package-directory]" >&2
  exit 2
}
test -n "$version" || {
  echo "Usage: $0 <feed-service-index> <exact-version> [expected-package-directory]" >&2
  exit 2
}
[[ "$expected_packages" == "-" ]] && expected_packages=""
if [[ -n "$expected_packages" ]] && ! test -d "$expected_packages"; then
  echo "Expected package directory '$expected_packages' does not exist." >&2
  exit 2
fi
if [[ -z "$expected_packages" ]]; then
  current_release="$(sed -n 's:.*<GroundworkCurrentRelease>\(.*\)</GroundworkCurrentRelease>.*:\1:p' "$repo_root/Directory.Build.props")"
  test -n "$current_release" || {
    echo "Could not determine GroundworkCurrentRelease from Directory.Build.props." >&2
    exit 1
  }
  [[ "$version" == "$current_release" ]] || {
    echo "Remote package verification version '$version' is not GroundworkCurrentRelease '$current_release'." >&2
    exit 1
  }
fi

probe_root="$(mktemp -d)"
package_cache="$(mktemp -d)"
trap 'rm -rf "$probe_root" "$package_cache"' EXIT

dotnet new classlib --name GroundworkFeedProbe --output "$probe_root" --no-restore >/dev/null
config="$probe_root/NuGet.Config"
sed \
  -e 's#key="groundwork-local" value="./feed"#key="groundwork-feedz" value="'"$feed"'"#' \
  -e 's#key="groundwork-local"#key="groundwork-feedz"#' \
  "$repo_root/tests/Groundwork.PublicApi.Acceptance.Tests/Consumer/NuGet.Config" > "$config"

while IFS='|' read -r package_id _; do
  [[ -n "$package_id" && "$package_id" != \#* ]] || continue
  [[ "$package_id" == "Groundwork.Tool" ]] && continue
  dotnet add "$probe_root/GroundworkFeedProbe.csproj" package "$package_id" \
    --version "$version" --no-restore >/dev/null
done < "$repo_root/eng/public-packages.txt"

verify_artifact_hash() {
  local package_id="$1"
  local cache_id="${package_id,,}"
  local restored_hash="$package_cache/$cache_id/$version/$cache_id.$version.nupkg.sha512"
  local restored_package=""
  if [[ "$package_id" == "Groundwork.Tool" ]]; then
    restored_package="$probe_root/tool/.store/$cache_id/$version/$cache_id/$version/$package_id.nupkg"
  fi
  [[ -n "$restored_package" ]] || test -f "$restored_hash" || {
    echo "Restored package hash is missing: $restored_hash" >&2
    exit 1
  }
  [[ -z "$restored_package" ]] || test -f "$restored_package" || {
    echo "Restored tool package is missing: $restored_package" >&2
    exit 1
  }
  [[ -n "$expected_packages" ]] || return 0

  local expected="$expected_packages/$package_id.$version.nupkg"
  test -f "$expected" || {
    echo "Expected artifact is missing: $expected" >&2
    exit 1
  }

  local expected_hash
  local actual_hash
  expected_hash="$(openssl dgst -sha512 -binary "$expected" | openssl base64 -A)"
  if [[ -n "$restored_package" ]]; then
    actual_hash="$(openssl dgst -sha512 -binary "$restored_package" | openssl base64 -A)"
  else
    actual_hash="$(tr -d '\r\n' < "$restored_hash")"
  fi
  [[ "$actual_hash" == "$expected_hash" ]] || {
    echo "Artifact hash mismatch for $package_id $version." >&2
    exit 1
  }
}

restore_succeeded=false
for attempt in {1..12}; do
  if NUGET_PACKAGES="$package_cache" dotnet restore "$probe_root/GroundworkFeedProbe.csproj" \
    --configfile "$config" --force --no-cache --nologo; then
    restore_succeeded=true
    break
  fi
  echo "Feedz has not exposed every package yet; retrying restore ($attempt/12)." >&2
  sleep 10
done
[[ "$restore_succeeded" == true ]] || {
  echo "Could not restore the complete Groundwork $version package family from $feed." >&2
  exit 1
}

while IFS='|' read -r package_id _; do
  [[ -n "$package_id" && "$package_id" != \#* ]] || continue
  [[ "$package_id" == "Groundwork.Tool" ]] && continue
  cache_id="${package_id,,}"
  test -d "$package_cache/$cache_id/$version" || {
    echo "Feed restore did not materialize $package_id $version." >&2
    exit 1
  }
  verify_artifact_hash "$package_id"
done < "$repo_root/eng/public-packages.txt"

tool_succeeded=false
for attempt in {1..12}; do
  if NUGET_PACKAGES="$package_cache" dotnet tool install Groundwork.Tool \
    --version "$version" --tool-path "$probe_root/tool" --configfile "$config" \
    --no-cache --verbosity quiet; then
    tool_succeeded=true
    break
  fi
  echo "Feedz has not exposed Groundwork.Tool yet; retrying ($attempt/12)." >&2
  sleep 10
done
[[ "$tool_succeeded" == true ]] || {
  echo "Could not install Groundwork.Tool $version from $feed." >&2
  exit 1
}
verify_artifact_hash Groundwork.Tool

expected="Groundwork.Tool $version"
actual="$("$probe_root/tool/groundwork" --version)"
[[ "$actual" == "$expected" ]] || {
  echo "Expected '$expected', got '$actual'." >&2
  exit 1
}

echo "Verified every public Groundwork $version package and Groundwork.Tool from Feedz."
