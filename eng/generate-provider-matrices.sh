#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mode="${1:-check}"
if [[ "$mode" != "check" && "$mode" != "write" ]]; then
  echo "Usage: $0 [check|write]" >&2
  exit 2
fi

output_root="$repo_root/docs/v2/generated"
temporary_root="$(mktemp -d "${TMPDIR:-/tmp}/groundwork-provider-matrix.XXXXXX")"
cleanup() {
  find "$temporary_root" -type f -exec unlink {} \; 2>/dev/null || true
  rmdir "$temporary_root" 2>/dev/null || true
}
trap cleanup EXIT

dotnet run --project "$repo_root/eng/provider-matrix/ProviderMatrix.csproj" \
  --configuration Release \
  "$repo_root/eng/public-packages.txt" \
  "$temporary_root/provider-capability-matrix.json" \
  "$temporary_root/provider-capability-matrix.md" \
  "$temporary_root/package-matrix.md"

if [[ "$mode" == "write" ]]; then
  mkdir -p "$output_root"
  cp "$temporary_root/provider-capability-matrix.json" "$output_root/provider-capability-matrix.json"
  cp "$temporary_root/provider-capability-matrix.md" "$output_root/provider-capability-matrix.md"
  cp "$temporary_root/package-matrix.md" "$output_root/package-matrix.md"
  echo "Wrote generated provider and package matrices under ${output_root#$repo_root/}."
  exit 0
fi

status=0
for file in provider-capability-matrix.json provider-capability-matrix.md package-matrix.md; do
  expected="$output_root/$file"
  actual="$temporary_root/$file"
  if [[ ! -f "$expected" ]]; then
    echo "Missing generated matrix: ${expected#$repo_root/}" >&2
    status=1
    continue
  fi
  if ! diff -u "$expected" "$actual"; then
    status=1
  fi
done
if [[ "$status" != 0 ]]; then
  echo "Generated matrices are stale; run eng/generate-provider-matrices.sh write." >&2
  exit "$status"
fi
echo "Generated provider and package matrices are current."
