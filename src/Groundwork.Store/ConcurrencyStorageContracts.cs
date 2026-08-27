namespace Groundwork.Store;

/// <summary>Optional provider extension for an atomic optimistic conditional upsert.</summary>
public interface IConcurrencyStorageSession
{
    /// <summary>
    /// Executes one provider-native conditional upsert without a pre-read. Conflict detail is
    /// available through the returned outcome when the provider can disambiguate it lazily.
    /// </summary>
    WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null);

    ValueTask<WriteOutcome> ConditionalUpsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default);
}
