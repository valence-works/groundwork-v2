using System.Globalization;

namespace Groundwork.Kernel;

/// <summary>
/// The storable domain of <see cref="PortableType.Double"/>.
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
/// </remarks>
public static class PortableDouble
{
    /// <summary>The code carried by every refusal of a value outside the storable domain.</summary>
    public const string RefusalCode = "GW-VALUE-DOUBLE-001";

    /// <summary>Whether every supported store returns <paramref name="value"/> bit-for-bit.</summary>
    public static bool IsStorable(double value) =>
        double.IsFinite(value) && !(value == 0d && double.IsNegative(value));

    /// <summary>The refusal text for a value outside the storable domain, carrying the code.</summary>
    public static string RefusalMessage(string column, double value) =>
        $"{RefusalCode}: {Explain(column, value)}";

    /// <summary>The refusal text without a code, for callers that carry the code separately.</summary>
    public static string Explain(string column, double value) =>
        $"Double column '{column}' cannot hold {Describe(value)}. NaN and the infinities are " +
        "refused outright by SQL Server and SQLite, and negative zero comes back as positive " +
        "zero from SQLite and MongoDB, so the value a reader gets would depend on the provider. " +
        "Use a finite value, or declare Decimal or Int64.";

    private static string Describe(double value) => value switch
    {
        _ when double.IsNaN(value) => "NaN",
        double.PositiveInfinity => "positive infinity",
        double.NegativeInfinity => "negative infinity",
        _ => "negative zero"
    };

    /// <summary>
    /// The shortest representation that parses back to the same binary64 value, so a DDL
    /// default renders and re-parses without losing a bit.
    /// </summary>
    public static string ToLiteral(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);
}
