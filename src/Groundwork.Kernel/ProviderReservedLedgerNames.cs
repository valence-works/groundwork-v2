namespace Groundwork.Kernel;

/// <summary>Names owned by Groundwork provider catalogs and lifecycle ledgers.</summary>
internal static class ProviderReservedLedgerNames
{
    internal const string DefaultAppendLedger = "__groundwork_operations";
    internal const string DefaultRetentionLedger = "__groundwork_retention_operations";

    internal static readonly string[] All =
    [
        "__groundwork_metadata",
        "__groundwork_sequences",
        "__groundwork_schema_history",
        "__groundwork_schema_locks",
        "__groundwork_schema_fences",
        "__groundwork_search_key_algorithms",
        "__groundwork_sequence_high_waters",
        DefaultAppendLedger,
        DefaultRetentionLedger
    ];
}
