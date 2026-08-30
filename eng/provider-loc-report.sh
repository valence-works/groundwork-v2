#!/usr/bin/env bash
set -euo pipefail

baseline_ref=${1:-65936ec9ad4000c2bc0d5a68b508cae986b202a1}
target_ref=${2:-HEAD}
components=(
  Groundwork.Sqlite
  Groundwork.PostgreSql
  Groundwork.SqlServer
  Groundwork.MySql
  Groundwork.Substrate.Relational
)

git cat-file -e "$baseline_ref^{commit}"
git cat-file -e "$target_ref^{commit}"

count_nonblank_csharp_lines() {
  local ref=$1
  local component=$2
  local files
  files=$(git ls-tree -r --name-only "$ref" "src/$component" | awk '/\.cs$/')
  if [ -z "$files" ]; then
    echo 0
    return
  fi

  while IFS= read -r file; do
    git show "$ref:$file"
  done <<< "$files" | awk 'NF { count++ } END { print count + 0 }'
}

printf 'component\tbaseline\ttarget\tdelta\n'
for component in "${components[@]}"; do
  baseline=$(count_nonblank_csharp_lines "$baseline_ref" "$component")
  target=$(count_nonblank_csharp_lines "$target_ref" "$component")
  printf '%s\t%d\t%d\t%+d\n' "$component" "$baseline" "$target" "$((target - baseline))"
done
