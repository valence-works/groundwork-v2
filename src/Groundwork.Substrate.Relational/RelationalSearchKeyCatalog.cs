using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Substrate.Relational;

/// <summary>Shared validation and command behavior for relational derived search-key catalogs.</summary>
public static class RelationalSearchKeyCatalog
{
    public static void Apply(
        DbConnection connection,
        DbTransaction transaction,
        ProviderPhysicalSchemaDefinition definition,
        string upsertSql)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(definition.Kind, RelationalDialect.SearchKeyDefinitionKind, StringComparison.Ordinal))
            throw new InvalidOperationException($"Provider definition '{definition.Kind}' is not a search-key algorithm definition.");

        var separator = definition.SubjectIdentity.IndexOf(RelationalDialect.SearchKeyDefinitionSeparator, StringComparison.Ordinal);
        if (separator <= 0 ||
            separator != definition.SubjectIdentity.LastIndexOf(RelationalDialect.SearchKeyDefinitionSeparator, StringComparison.Ordinal) ||
            separator == definition.SubjectIdentity.Length - RelationalDialect.SearchKeyDefinitionSeparator.Length)
        {
            throw new InvalidOperationException("A relational search-key provider definition requires a table and column identity.");
        }

        PortableSearchKeyAlgorithmIdentity.Parse(definition.CanonicalDefinition);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = upsertSql;
        AddParameter(command, "@table", definition.SubjectIdentity[..separator]);
        AddParameter(command, "@column", definition.SubjectIdentity[(separator + RelationalDialect.SearchKeyDefinitionSeparator.Length)..]);
        AddParameter(command, "@algorithm", definition.CanonicalDefinition);
        command.ExecuteNonQuery();
    }

    public static IReadOnlyDictionary<string, string> Read(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string selectSql)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = selectSql;
        AddParameter(command, "@table", table);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
