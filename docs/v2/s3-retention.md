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
bounded statement per pass. MongoDB sorts by order column and then primary key, groups the ordered
identities per partition, and submits one bounded `deleteMany` set. This preserves the same
deterministic tie break as the relational and in-memory providers. MongoDB does not use capped
collections because a cap is collection-wide, cannot express N rows per partition, and prevents
normal document growth and index updates.

`OnAppend` performs bounded cleanup after a successful single-row or native batch append. Concurrent
committed appenders coalesce on a per-unit/per-scope dirty signal: one owner recomputes retention
until the signal is drained while the other writers return without waiting behind cleanup. The
cleanup commands are observable through `IWritePathObserver`; the live proof compares their count
against an equal-size serial baseline. Cancellation is checked between native delete batches. SQL
providers roll back the interrupted pass, while MongoDB may leave a completed bounded batch; both
forms resume by recomputing the watermark and converge on the exact retained count.
