using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace MemorySmith.Core.Security;

/// <summary>
/// Timing-safe comparison utilities for secret material (API keys, tokens, hashes).
///
/// All methods ensure that every comparison takes constant time proportional to
/// the longer of the two inputs, preventing length-oracle attacks via timing side
/// channels. The raw <see cref="CryptographicOperations.FixedTimeEquals"/> API is
/// unsafe when called with a short-circuit length check — this wrapper eliminates
/// that pattern by padding to a common length internally.
/// </summary>
public static class SecurityCompare
{
    /// <summary>
    /// Compares two strings in constant time, without short-circuiting on length mismatch.
    /// </summary>
    /// <param name="actual">The supplied secret (e.g., from a request header). May be null.</param>
    /// <param name="expected">The expected secret. Must not be null.</param>
    /// <returns>True if the strings are equal; false otherwise.</returns>
    public static bool FixedTimeEquals(string? actual, string expected)
    {
        if (actual is null)
        {
            return false;
        }

        var actualBytes = Encoding.UTF8.GetBytes(actual);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        // Pad both to the longer length so timing leaks nothing about length.
        var maxLen = Math.Max(actualBytes.Length, expectedBytes.Length);
        var paddedActual = new byte[maxLen];
        var paddedExpected = new byte[maxLen];
        Buffer.BlockCopy(actualBytes, 0, paddedActual, 0, actualBytes.Length);
        Buffer.BlockCopy(expectedBytes, 0, paddedExpected, 0, expectedBytes.Length);

        return CryptographicOperations.FixedTimeEquals(paddedActual, paddedExpected);
    }

    /// <summary>
    /// Compares two strings in constant time, treating both as case-insensitive.
    /// </summary>
    /// <param name="actual">The supplied secret. May be null.</param>
    /// <param name="expected">The expected secret. Must not be null.</param>
    /// <returns>True if the strings are equal ignoring case; false otherwise.</returns>
    public static bool FixedTimeEqualsOrdinalIgnoreCase(string? actual, string expected)
    {
        if (actual is null)
        {
            return false;
        }

        return FixedTimeEquals(actual.ToUpperInvariant(), expected.ToUpperInvariant());
    }
}
