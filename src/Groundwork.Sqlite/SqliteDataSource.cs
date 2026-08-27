using Microsoft.Data.Sqlite;

namespace Groundwork.Sqlite;

/// <summary>One shared interpretation of SQLite data sources for locking, admission, and deployment tooling.</summary>
internal static class SqliteDataSource
{
    internal static bool IsMemory(SqliteConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Mode == SqliteOpenMode.Memory)
            return true;
        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) ||
            dataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return false;
        var query = dataSource.Split('?', 2);
        return query.Length == 2 && query[1]
            .Split('&')
            .Any(parameter => parameter.Equals("mode=memory", StringComparison.OrdinalIgnoreCase));
    }

    internal static string FullPath(string dataSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSource);
        var path = dataSource;
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            path = path[5..].Split('?', 2)[0];
            if (path.StartsWith("//", StringComparison.Ordinal))
            {
                path = path[2..];
                var separator = path.IndexOf('/');
                path = separator < 0 ? string.Empty : path[separator..];
            }
            if (path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':')
                path = path[1..];
        }
        return Path.GetFullPath(path);
    }
}
