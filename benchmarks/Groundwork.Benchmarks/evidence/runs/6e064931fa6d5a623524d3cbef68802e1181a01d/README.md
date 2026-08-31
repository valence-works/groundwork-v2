# Controlled baseline 6e064931

This directory contains the publication-safe comparative bundle collected from exact commit
`6e064931fa6d5a623524d3cbef68802e1181a01d` on `groundwork-controlled-m2` on
2026-08-31. The run used BenchmarkDotNet 0.15.8, .NET SDK 10.0.300, .NET 10.0.8,
macOS 26.5, and the Apple M2's eight physical cores.

Before collection, all other foreground applications except Finder and Codex were closed. No
Groundwork test, build, benchmark, provider, or container workload was running. Repeated process
samples showed 94-95% CPU idle immediately before the collector started. The manifest retains the
contemporaneous one-minute load snapshot and the full host, runtime, package, schema, report-path,
and report-hash provenance. Spotlight background workers were stopped for the measurement window
and restored immediately after collection. The measured report bytes are unchanged; publication
metadata uses a stable pseudonymous host identifier, scrubs the checkout root, and excludes the raw
BenchmarkDotNet execution log because that log embeds workstation paths.

The five Groundwork results had observed standard deviations between 0.34% and 0.90% of their
means. `../../performance-policy.json` sets latency ceilings from 5% for the lowest-variance read
paths through 10% for the highest-variance unit-of-work path. Every latency ceiling is at least
roughly eleven observed standard deviations above this baseline. Allocation ceilings are 2% because
allocation measurements are deterministic while still allowing small per-operation amortization
differences in a future controlled run. Budget changes require an explicit evidence review; a red
comparison never updates the baseline automatically.

The report's high-priority warning is expected on macOS without elevated scheduler permission. All
15 benchmark processes exited successfully. BenchmarkDotNet removed isolated high outliers from
some workloads according to its standard analysis; the five Groundwork distributions remained
unimodal (`MValue = 2`) and their retained statistics are recorded in the JSON and Markdown reports.
