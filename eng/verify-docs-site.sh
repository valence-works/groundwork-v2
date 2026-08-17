#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
site_root="$repository_root/docs/portal/_site"

required_files=(
  "$site_root/index.html"
  "$site_root/index.json"
  "$site_root/versions.html"
  "$site_root/v0.1/index.html"
  "$site_root/v0.1/api/toc.html"
  "$site_root/v0.1/getting-started/install.html"
  "$site_root/v0.1/getting-started/quickstart.html"
  "$site_root/v0.1/providers/sqlite.html"
  "$site_root/v0.1/providers/postgresql.html"
  "$site_root/v0.1/providers/sql-server.html"
  "$site_root/v0.1/providers/mongodb.html"
)

for file in "${required_files[@]}"; do
  [[ -s "$file" ]] || {
    echo "Documentation artifact is missing or empty: $file" >&2
    exit 1
  }
done

api_pages="$(find "$site_root/v0.1/api" -type f -name '*.html' | wc -l | tr -d ' ')"
if (( api_pages < 450 )); then
  echo "Expected the full public API reference; found only $api_pages HTML pages." >&2
  exit 1
fi

grep -Fq 'Groundwork.Store' "$site_root/index.json"
grep -Fq 'https://f.feedz.io/valence-works/groundwork/nuget/index.json' \
  "$site_root/v0.1/getting-started/install.html"
grep -Fq 'public static class Program' "$site_root/v0.1/getting-started/quickstart.html"

for page in "${required_files[@]}"; do
  [[ "$page" == *.html ]] || continue
  [[ "$page" == */api/toc.html ]] && continue
  grep -Fq '<html lang="en">' "$page"
  grep -Fq '<main ' "$page"
done

grep -Fq 'role="search"' "$site_root/v0.1/getting-started/quickstart.html"
echo "Verified versioned docs, local search, Feedz install, compiled sample, accessibility landmarks, and $api_pages API pages."
