using System.Globalization;

namespace Groundwork.Kernel;

/// <summary>
/// The two value domains of <see cref="PortableType.Double"/>: what can be written, and the
/// narrower set of what can be declared as a default.
/// </summary>
/// <remarks>
/// <para>
/// Groundwork admits exactly the binary64 values that PostgreSQL <c>double precision</c>,
/// SQL Server <c>float</c>, SQLite <c>REAL</c>, and MongoDB <c>double</c> all return
/// bit-for-bit. Measured against those four stores, that domain is the finite values other
/// than negative zero:
/// </para>
/// <list type="bullet">
/// <item><description>
/// SQL Server refuses <c>NaN</c>, <c>+Infinity</c>, and <c>-Infinity</c> at the wire protocol;
/// SQLite's driver refuses <c>NaN</c>. Two of the four stores cannot hold them at all.
/// </description></item>
/// <item><description>
/// SQLite and MongoDB both return <c>+0</c> for a stored <c>-0</c>: SQLite because a REAL that
/// is exactly an integer is kept in the record's integer serial form, MongoDB because the
/// driver's cached <c>BsonDouble</c> lookup treats <c>-0.0</c> as <c>0.0</c>.
/// </description></item>
/// </list>
/// <para>
/// Every other value — including <see cref="double.Epsilon"/>, subnormals,
/// <see cref="double.MaxValue"/>, and <see cref="double.MinValue"/> — round-trips identically
/// on all four. Values outside the domain are refused rather than normalized, because
/// normalizing would silently return a value the caller did not write.
/// </para>
/// <para>
/// A declared default is held to a narrower domain, because it reaches the store as a SQL
/// literal rather than as a parameter. See <see cref="IsStorableAsDefault"/>.
/// </para>
/// </remarks>
public static class PortableDouble
{
    /// <summary>The code carried by every refusal of a value outside the storable domain.</summary>
    public const string RefusalCode = "GW-VALUE-DOUBLE-001";

    /// <summary>Whether every supported store returns <paramref name="value"/> bit-for-bit.</summary>
    public static bool IsStorable(double value) =>
        double.IsFinite(value) && !(value == 0d && double.IsNegative(value));

    /// <summary>
    /// Whether every supported store returns <paramref name="value"/> bit-for-bit when it is
    /// written into DDL as a declared default rather than sent as a parameter. This is strictly
    /// narrower than <see cref="IsStorable"/>: SQL Server's T-SQL float literal parser flushes a
    /// subnormal to zero — measured, the smallest normal survives and the largest subnormal does
    /// not — while the same value sent as a parameter round-trips exactly. A subnormal is
    /// therefore writable but not defaultable.
    /// </summary>
    public static bool IsStorableAsDefault(double value) =>
        IsStorable(value) && (value == 0d || double.IsNormal(value));

    /// <summary>
    /// The refusal text for a value outside the storable domain, carrying the code. Only
    /// meaningful for a value <see cref="IsStorable"/> rejects, so it stays internal to the
    /// assemblies that do the rejecting.
    /// </summary>
    internal static string RefusalMessage(string column, double value) =>
        $"{RefusalCode}: {Explain(column, value)}";

    /// <summary>The refusal text for a declared default outside the defaultable domain.</summary>
    internal static string ExplainDefault(string column, double value) =>
        IsStorable(value)
            ? $"Double column '{column}' cannot default to the subnormal value " +
              $"{ToLiteral(value)}: SQL Server's float literal parser flushes a subnormal to zero, " +
              "so the deployed default would be zero there and the declared value everywhere else. " +
              "Declare a normal default, and write the subnormal as a value instead."
            : Explain(column, value);

    /// <summary>
    /// The refusal text for a declared default whose runtime type is not <see cref="double"/> — for
    /// example an <see cref="int"/> literal such as <c>.Default(1)</c>. Providers disagree on
    /// whether they coerce it: MongoDB's codec dispatches on the column's portable type and throws
    /// for anything but a <see cref="double"/>, while other providers accept the narrower CLR type.
    /// Refusing at declaration time keeps that disagreement from surfacing only when a schema is
    /// applied to one provider and not another.
    /// </summary>
    internal static string ExplainNonDoubleDefault(string column, object value) =>
        $"Double column '{column}' declares a default of type '{value.GetType().Name}', not " +
        "double. MongoDB's codec accepts only a double for this column and refuses the value " +
        "outright; declare the default as a double literal, for example .Default(1.0) rather " +
        "than .Default(1).";

    /// <summary>The refusal text without a code, for callers that carry the code separately.</summary>
    internal static string Explain(string column, double value) =>
        double.IsFinite(value)
            ? $"Double column '{column}' cannot hold negative zero: SQLite and MongoDB both return " +
              "positive zero for a stored negative zero, so the value a reader gets would depend " +
              "on the provider. Write positive zero, or declare Decimal or Int64."
            : $"Double column '{column}' cannot hold {(double.IsNaN(value) ? "NaN" : value > 0 ? "positive infinity" : "negative infinity")}: " +
              "SQL Server refuses NaN and both infinities outright, and SQLite refuses NaN, so " +
              "the same write would succeed on one provider and fail on another. Write a finite " +
              "value, or declare Decimal or Int64.";

    /// <summary>
    /// The shortest representation that parses back to the same binary64 value, so a DDL
    /// default renders and re-parses without losing a bit.
    /// </summary>
    public static string ToLiteral(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);
}
