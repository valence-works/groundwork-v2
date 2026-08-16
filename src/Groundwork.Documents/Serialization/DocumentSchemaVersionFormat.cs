namespace Groundwork.Documents.Serialization;

/// <summary>Maps caller-owned persisted schema stamps to positive contiguous versions.</summary>
public sealed class DocumentSchemaVersionFormat
{
    private readonly Func<string, string, int?> parser;
    private readonly Func<string, int, string> formatter;

    public DocumentSchemaVersionFormat(Func<string, string, int?> parser, Func<string, int, string> formatter)
    {
        this.parser = parser ?? throw new ArgumentNullException(nameof(parser));
        this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
    }

    internal int Parse(string kind, string id, string stamp, int minimum, int current)
    {
        int? parsed;
        try { parsed = parser(kind, stamp); }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new DocumentSchemaVersionException(DocumentSchemaVersionFailure.MalformedStamp,
                $"Document '{id}' of kind '{kind}' carries schema-version stamp '{stamp}', which the configured format could not parse.",
                kind, id, stamp, minimumReadableVersion: minimum, currentVersion: current, innerException: exception);
        }
        if (parsed is null or < 1)
            throw new DocumentSchemaVersionException(DocumentSchemaVersionFailure.MalformedStamp,
                $"Document '{id}' of kind '{kind}' carries unrecognized schema-version stamp '{stamp}'.",
                kind, id, stamp, parsed, minimum, current);
        return parsed.Value;
    }

    internal string Stamp(DocumentSchemaVersionPolicy policy, int version)
    {
        string stamp;
        try { stamp = formatter(policy.DocumentKind, version); }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new DocumentSchemaVersionException(DocumentSchemaVersionFailure.InvalidVersionFormat,
                $"The configured schema-version format could not stamp version {version} for document kind '{policy.DocumentKind}'.",
                policy.DocumentKind, parsedVersion: version,
                minimumReadableVersion: policy.MinimumReadableVersion, currentVersion: policy.CurrentVersion,
                innerException: exception);
        }
        if (string.IsNullOrWhiteSpace(stamp))
            throw new DocumentSchemaVersionException(DocumentSchemaVersionFailure.InvalidVersionFormat,
                $"The configured schema-version format produced an empty stamp for version {version} of document kind '{policy.DocumentKind}'.",
                policy.DocumentKind, schemaVersion: stamp, parsedVersion: version,
                minimumReadableVersion: policy.MinimumReadableVersion, currentVersion: policy.CurrentVersion);
        return stamp;
    }

    internal void ValidateRoundTrips(DocumentSchemaVersionPolicy policy)
    {
        for (var version = policy.MinimumReadableVersion; ; version++)
        {
            var stamp = Stamp(policy, version);
            int? parsed;
            try { parsed = parser(policy.DocumentKind, stamp); }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                throw new DocumentSchemaVersionException(DocumentSchemaVersionFailure.InvalidVersionFormat,
                    $"The configured schema-version format cannot parse its own stamp '{stamp}' for version {version} of document kind '{policy.DocumentKind}'.",
                    policy.DocumentKind, schemaVersion: stamp, parsedVersion: version,
                    minimumReadableVersion: policy.MinimumReadableVersion, currentVersion: policy.CurrentVersion,
                    innerException: exception);
            }
            if (parsed != version)
                throw new DocumentSchemaVersionException(DocumentSchemaVersionFailure.InvalidVersionFormat,
                    $"The configured schema-version format stamps version {version} of document kind '{policy.DocumentKind}' as '{stamp}', but parses that stamp as {parsed?.ToString() ?? "unrecognized"}.",
                    policy.DocumentKind, schemaVersion: stamp, parsedVersion: parsed,
                    minimumReadableVersion: policy.MinimumReadableVersion, currentVersion: policy.CurrentVersion);
            if (version == policy.CurrentVersion) break;
        }
    }

    private static bool IsRecoverable(Exception exception) => exception is not OperationCanceledException and not OutOfMemoryException;
}
