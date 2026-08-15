using System.Collections;
using System.Runtime.CompilerServices;
using Groundwork.Kernel;

namespace Groundwork.Records;

internal static class DefaultValueSnapshot
{
    public static object? Create(object? value, PortableType type)
    {
        var active = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Snapshot(value, type, active);
    }

    private static object? Snapshot(object? value, PortableType type, ISet<object> active)
    {
        if (value is null || IsImmutable(value))
            return value;

        if (value is byte[] bytes)
            return (byte[])bytes.Clone();

        if (type != PortableType.Json)
            throw new ArgumentException(
                "Mutable default values are supported only for byte[] and JSON object/array graphs.",
                nameof(value));

        if (!active.Add(value))
            throw new ArgumentException("JSON default values cannot contain reference cycles.", nameof(value));

        try
        {
            if (value is IDictionary dictionary)
                return SnapshotDictionary(dictionary, type, active);

            if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
                return SnapshotReadOnlyDictionary(readOnlyDictionary, type, active);

            if (value is IEnumerable sequence)
                return SnapshotSequence(sequence, type, active);
        }
        finally
        {
            active.Remove(value);
        }

        throw new ArgumentException(
            "JSON defaults must be scalars, byte arrays, dictionaries with string keys, or enumerable arrays/lists.",
            nameof(value));
    }

    private static Dictionary<string, object?> SnapshotDictionary(
        IDictionary dictionary,
        PortableType type,
        ISet<object> active)
    {
        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not string key)
                throw new ArgumentException("JSON object default keys must be strings.", nameof(dictionary));

            snapshot[key] = Snapshot(entry.Value, type, active);
        }

        return snapshot;
    }

    private static Dictionary<string, object?> SnapshotReadOnlyDictionary(
        IReadOnlyDictionary<string, object?> dictionary,
        PortableType type,
        ISet<object> active)
    {
        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in dictionary)
            snapshot[entry.Key] = Snapshot(entry.Value, type, active);

        return snapshot;
    }

    private static List<object?> SnapshotSequence(
        IEnumerable sequence,
        PortableType type,
        ISet<object> active)
    {
        var snapshot = new List<object?>();
        foreach (var item in sequence)
            snapshot.Add(Snapshot(item, type, active));

        return snapshot;
    }

    private static bool IsImmutable(object value) => value is
        string or
        bool or
        byte or
        sbyte or
        short or
        ushort or
        int or
        uint or
        long or
        ulong or
        float or
        double or
        decimal or
        char or
        DateTime or
        DateTimeOffset or
        Guid;

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);

        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
}
