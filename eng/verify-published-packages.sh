#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="${1:-}"
version="${2:-}"
test -n "$feed" || {
  echo "Usage: $0 <feed-service-index> <exact-version>" >&2
  exit 2
}
test -n "$version" || {
  echo "Usage: $0 <feed-service-index> <exact-version>" >&2
  exit 2
}

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

expected="Groundwork.Tool $version"
actual="$("$probe_root/tool/groundwork" --version)"
[[ "$actual" == "$expected" ]] || {
  echo "Expected '$expected', got '$actual'." >&2
  exit 1
}

echo "Verified every public Groundwork $version package and Groundwork.Tool from Feedz."
