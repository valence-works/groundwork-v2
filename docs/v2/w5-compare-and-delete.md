# W5: compare-and-delete

Compare-and-delete is the narrow public claim-release operation. It deletes one row only when
the provider-owned atomic decision finds the declared key and every supplied declared value
equal to the current row.

```csharp
var capability = connection.Capabilities.SingleOrDefault(item =>
    item.Id == BatchWriteCapabilities.CompareAndDelete);
if (capability is null)
    throw new NotSupportedException("This deployment cannot release claims atomically.");

var result = session.CompareAndDelete(
    new StorageKey(new Dictionary<string, object?> { ["id"] = claimId }),
    new Dictionary<string, object?>
    {
        ["owner"] = owner,
        ["fence"] = fence
    });

switch (result.Status)
{
    case WriteOutcomeStatus.Deleted:
        // The claim was released.
        break;
    case WriteOutcomeStatus.NotFound:
        // The claim was already absent.
        break;
    case WriteOutcomeStatus.ComparisonMismatch:
        // A successor reclaimed or changed the claim; do not delete it.
        break;
}
```

The same operation can participate in an all-or-nothing exact unit of work. The staged write
is attributed if a successor wins the comparison, and every staged unit rolls back together:

```csharp
using var work = connection.BeginUnitOfWork(
    StorageAccess.Global,
    BatchWriteOptions.Exact,
    claimsUnit,
    auditUnit);
work.Stage(RowWrite.Insert(auditUnit, new StorageValues(new Dictionary<string, object?>
{
    ["id"] = operationId,
    ["value"] = "released"
})));
work.Stage(RowWrite.CompareAndDelete(
    claimsUnit,
    new StorageKey(new Dictionary<string, object?> { ["id"] = claimId }),
    new Dictionary<string, object?> { ["owner"] = owner, ["fence"] = fence }));

var committed = work.CommitWithOutcomes();
```

The equality set is deliberately a dictionary of declared scalar or binary columns, not a
general predicate. `PortableType.Json` is refused before provider I/O because JSON equality and
property ordering differ across the supported stores, and `PortableType.Double` because binary
floating point has no comparison semantics that hold across them. A nullable expected value represents the
logical null value; an omitted nullable field is normalized to that same value. Provider-owned
scope, version, action, and derived search-key columns cannot be compared.

`WriteOptions.IfVersion` remains available when a caller requires a revision CAS. It is separate
from the owner/fence equality set: a claim can be renewed to a new revision and still be released
when its owner and fence match. In exact unit-of-work mode, a comparison mismatch is attributed
to its original `RowWrite` and rolls back the entire transaction. Transactional MongoDB is
required; standalone MongoDB does not advertise this capability and refuses before flushing a
staged batch or emitting a write-path event.
