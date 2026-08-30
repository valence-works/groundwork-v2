# MySQL/MariaDB provider evidence

The implementation targets both server families, but the executable evidence in this report runs
against MySQL 8.4.6 only. It does not claim a MariaDB live lane or production-support evidence;
provider/version support tiers are assigned by the [support matrix](support-matrix.md).

The fifth provider is the proof that the shared relational substrate makes a new relational
provider smaller than the three implementations that preceded it. This report fixes the comparison
to two immutable repository boundaries:

- **Before:** `65936ec9ad4000c2bc0d5a68b508cae986b202a1`, immediately before the shared session and
  unit-of-work extraction in #316.
- **After:** `7d96d84`, the merge of the MySQL/MariaDB provider in #318.

Run the comparison from a repository checkout with:

```bash
eng/provider-loc-report.sh \
  65936ec9ad4000c2bc0d5a68b508cae986b202a1 \
  7d96d84
```

The measure is deliberately simple and reproducible: nonblank checked-in C# source lines under each
component, including comments and excluding project files and generated build output. It is an
authoring-size comparison, not a complexity or performance metric.

| Component | Before | After MySQL landed | Change |
| --- | ---: | ---: | ---: |
| `Groundwork.Sqlite` | 3,496 | 3,129 | -367 |
| `Groundwork.PostgreSql` | 3,191 | 2,846 | -345 |
| `Groundwork.SqlServer` | 3,803 | 3,446 | -357 |
| `Groundwork.MySql` | 0 | 2,190 | +2,190 |
| `Groundwork.Substrate.Relational` | 4,433 | 6,784 | +2,351 |

The three original providers shed 1,069 provider-owned lines in aggregate (10.2%). Their
pre-extraction average was 3,497 lines; the new MySQL/MariaDB provider is 2,190 lines, **37.4% less
provider-owned code than that baseline average**. Adding a fourth relational provider increased the
total provider-specific source from 10,490 to 11,611 lines—10.7% more provider code for 33% more
providers—because the shared state machines live once in `Groundwork.Substrate.Relational`.

## Executable evidence

The `mysql-provider` correctness job starts MySQL 8.4.6 once, then runs the provider conformance
suite sequentially on `net8.0` and `net10.0` and drives `groundwork plan`, `apply`, and `status`
through the discovered MySQL schema-tool factory. The job inspects its TRX files and fails if the
live conformance or schema-tool proof was skipped.

The main-only and exact-ref `Concurrency` workflow supplies the same live MySQL service to the
provider-neutral harness. It requires all ten MySQL concurrency cases to pass on each target
framework. This remains separate from ordinary pull-request correctness feedback, and a skipped job
is never reported as evidence.
