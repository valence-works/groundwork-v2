using System.Collections.Concurrent;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Store;

/// <summary>
/// Raised before provider I/O when a retained session no longer matches the declaration published
/// through its provider connection.
/// </summary>
public sealed class StaleStorageSessionException : InvalidOperationException
{
    /// <summary>The stable diagnostic code for stale retained-session refusals.</summary>
    public const string DiagnosticCode = "GW-RUNTIME-005";

    /// <summary>Creates a stale-session refusal for the affected storage unit.</summary>
    /// <param name="storageUnitId">The stable identity of the unit whose declaration changed.</param>
    /// <param name="message">A diagnostic message describing the refusal and recovery.</param>
    public StaleStorageSessionException(StorageUnitId storageUnitId, string message)
        : base(message)
    {
        StorageUnitId = storageUnitId;
    }

    /// <summary>Gets the stable diagnostic code <c>GW-RUNTIME-005</c>.</summary>
    public string Code => DiagnosticCode;

    /// <summary>Gets the stable identity of the affected storage unit.</summary>
    public StorageUnitId StorageUnitId { get; }
}

/// <summary>
/// Tracks the declaration most recently published through one provider connection. Sessions capture
/// a lease after runtime admission so an in-process schema publication cannot leave them issuing
/// commands against physical names that no longer belong to their declaration.
/// </summary>
internal sealed class SchemaSessionPublicationRegistry
{
    private readonly ConcurrentDictionary<PhysicalSchemaTargetIdentity, Publication> publications = [];

    internal SchemaSessionLease Capture(PhysicalSchemaTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Capture(target.Identity, target.Fingerprint);
    }

    internal SchemaSessionLease Capture(PhysicalSchemaTargetIdentity identity, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        var current = publications.GetOrAdd(
            identity,
            _ => new Publication(fingerprint, Epoch: 0));
        if (!string.Equals(current.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new StaleStorageSessionException(
                identity.SubjectId,
                $"{StaleStorageSessionException.DiagnosticCode}: Cannot open a session for storage unit " +
                $"'{identity.SubjectId.Value}' " +
                "because this provider connection has already published a different applied declaration. " +
                "Run runtime admission with the current declaration and open a new session.");
        }
        return new SchemaSessionLease(this, identity, current.Fingerprint, current.Epoch);
    }

    internal void Publish(PhysicalSchemaTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        publications.AddOrUpdate(
            target.Identity,
            _ => new Publication(target.Fingerprint, Epoch: 0),
            (_, current) => string.Equals(current.Fingerprint, target.Fingerprint, StringComparison.Ordinal)
                ? current
                : new Publication(target.Fingerprint, checked(current.Epoch + 1)));
    }

    internal void EnsureCurrent(PhysicalSchemaTargetIdentity identity, string fingerprint, long epoch)
    {
        if (publications.TryGetValue(identity, out var current) &&
            current.Epoch == epoch &&
            string.Equals(current.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        throw new StaleStorageSessionException(
            identity.SubjectId,
            $"{StaleStorageSessionException.DiagnosticCode}: The storage session for unit " +
            $"'{identity.SubjectId.Value}' is stale because " +
            "this provider connection published a different applied declaration after the session opened. " +
            "Close the retained session and open a new one with the current declaration.");
    }

    private sealed record Publication(string Fingerprint, long Epoch);
}

internal sealed class SchemaSessionLease(
    SchemaSessionPublicationRegistry registry,
    PhysicalSchemaTargetIdentity identity,
    string fingerprint,
    long epoch)
{
    internal void EnsureCurrent() => registry.EnsureCurrent(identity, fingerprint, epoch);
}
