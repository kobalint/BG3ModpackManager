namespace BG3ModpackManager.Tests;

internal static class TestAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"ASSERT FAILED: {message}");
        }
    }

    public static void False(bool condition, string message)
    {
        if (condition)
        {
            throw new InvalidOperationException($"ASSERT FAILED (expected false): {message}");
        }
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"ASSERT FAILED: {message}. Expected={expected}, Actual={actual}");
        }
    }

    public static void EqualIgnoreCase(string? expected, string? actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"ASSERT FAILED: {message}. Expected='{expected}', Actual='{actual}'");
        }
    }

    public static void Contains(string substring, string? text, string message)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(substring, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"ASSERT FAILED: {message}. Expected '{text}' to contain '{substring}'.");
        }
    }
}
