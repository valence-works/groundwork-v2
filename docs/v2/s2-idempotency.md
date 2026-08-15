# S2: idempotent append

`StorageUnit.AppendIdempotency` opts a unit into `IStorageSession.Append`. The
operation identity is an `OperationId` containing an application-issued
timestamp and a caller-chosen nonce. The timestamp is metadata only: replay
expiry is evaluated against the provider's recorded ledger time.

```csharp
var unit = new StorageUnit
{
    Id = new StorageUnitId("events"),
    Name = "events",
    Columns =
    [
        new() { Name = "id", Type = PortableType.String, MaxLength = 450, IsNullable = false },
        new() { Name = "payload", Type = PortableType.String, MaxLength = 450, IsNullable = false }
    ],
    Key = new KeyDefinition { Columns = ["id"] },
    AppendIdempotency = new AppendIdempotencyDeclaration
    {
        Window = TimeSpan.FromMinutes(10)
    }
};

var result = session.Append(
    new OperationId(DateTimeOffset.UtcNow, "import-2026-08-15-001"),
    [new StorageValues(new Dictionary<string, object?>
    {
        ["id"] = "event-1", ["payload"] = "created"
    })]);
```

Every provider owns the durable ledger and commits its `(unit, scope, nonce)`
entry with the payload rows in one transaction. A nonce found within the
declared window returns `WriteOutcomeStatus.Replayed` without issuing a payload
write. Once the provider-recorded entry expires, the append is admitted again.
Expired entries are reclaimed in bounded batches, so retention work cannot
grow a single append without limit.

Providers advertise the contract through
`BatchWriteCapabilities.AppendIdempotency`; the stream-capability proof covers
replay, provider-time skew, expiry, and rollback of a failed payload batch.
