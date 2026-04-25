namespace Indexed.Core;

/// <summary>
/// Stable reason strings stored in <c>file_skips</c> and surfaced through
/// status responses.
/// </summary>
public static class IndexSkipReason
{
    public const string ExplicitBinary = "explicit_binary";
    public const string Binary = "binary";
    public const string TooLarge = "too_large";
    public const string Missing = "missing";
    public const string Unreadable = "unreadable";
    public const string Directory = "directory";
}
