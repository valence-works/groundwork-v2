namespace Groundwork.Kernel;

internal static class Base62
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public const int Length = 11;

    public static string Encode(ulong value)
    {
        Span<char> buffer = stackalloc char[Length];
        for (var i = Length - 1; i >= 0; i--)
        {
            buffer[i] = Alphabet[(int)(value % 62)];
            value /= 62;
        }

        return new string(buffer);
    }
}
