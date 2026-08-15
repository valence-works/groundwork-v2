# S3 retention

`StorageUnit.Retention` is an opt-in, provider-neutral count policy:

```csharp
Retention = new RetentionDeclaration
{
    KeepNewest = 100,
    OrderColumn = "sequence",
    PartitionColumns = ["tenant"],
    Trigger = RetentionTrigger.Explicit
}
```

The declaration is rejected with `GW-PORT-007` before schema I/O when `KeepNewest` is not
positive, the order column is missing, nullable, or not one of the portable ordered types, or a
partition column is missing. `session.ApplyRetention()` is the public execution seam; providers
implement it natively and the testing provider supplies the reference behavior. The optional
`MaxRowsPerBatch` bound makes a pass interruptible: cancellation is checked between bounded delete
commands, and a later pass recomputes the current watermark and resumes safely.

Relational providers rank rows with `ROW_NUMBER()` and delete only rows beyond the newest N in one
bounded statement per pass. MongoDB uses `$setWindowFields` to compute the rank, then a bounded
`deleteMany` by `_id`. MongoDB does not use capped collections because a cap is collection-wide,
cannot express N rows per partition, and prevents normal document growth and index updates.

`OnAppend` performs a bounded cleanup after a successful append. It does not add a read/probe to the
append write path; the cleanup command is observable through `IWritePathObserver` for throughput
measurements.
