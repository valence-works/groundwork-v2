#!/usr/bin/env bash
set -euo pipefail

evidence_root=${1:?Pass the recurrence evidence root to summarize.}

if [[ "$evidence_root" == /* || "$evidence_root" == "." || "$evidence_root" == ".." ||
      "$evidence_root" == ../* || "$evidence_root" == */../* || "$evidence_root" == */.. ]]; then
  echo "Recurrence evidence root must be a non-parent-traversing relative directory." >&2
  exit 1
fi
if [[ ! -d "$evidence_root" ]]; then
  echo "Recurrence evidence root does not exist: $evidence_root" >&2
  exit 1
fi

manifest_count=0
while IFS= read -r -d '' manifest; do
  manifest_count=$((manifest_count + 1))
  summary=${manifest%/manifest.txt}/summary.txt
  {
    echo "summary_schema=1"
    echo "summary_kind=publication-safe-recurrence"
    LC_ALL=C awk -F= '
      $1 ~ /^(commit|started_utc|logical_cpus|idle_load_limit|finished_utc|final_status)$/ ||
      $1 ~ /^idle_sample_[0-9]+_(utc|load_1m)$/ ||
      $1 ~ /^git_verification_[a-z0-9_]+$/ ||
      $1 ~ /^restore_(status|log_status)$/ ||
      $1 ~ /^run_[0-9]+_(started_utc|status|console_log_status|trx_present|finished_utc)$/ {
        print
      }
    ' "$manifest"
  } > "$summary"
  if ! grep -q '^final_status=' "$summary"; then
    echo "final_status=incomplete" >> "$summary"
  fi
done < <(find "$evidence_root" -type f -name manifest.txt -print0)

if [[ "$manifest_count" -eq 0 ]]; then
  echo "No recurrence manifest was available to summarize." >&2
  exit 1
fi

echo "Prepared $manifest_count publication-safe recurrence summary file(s)."
