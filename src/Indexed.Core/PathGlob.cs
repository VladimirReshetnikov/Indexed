using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Indexed.Core;

/// <summary>
/// Glob-to-regex translator used to filter candidate file paths in the query
/// executor. Supports the subset of gitignore/rsync glob syntax the search
/// API advertises: <c>*</c> (any chars but <c>/</c>), <c>**</c> (any path),
/// <c>?</c> (single char), and literal characters.
/// </summary>
/// <remarks>
/// <para>
/// Translates globs into anchored <see cref="Regex"/> instances so matching
/// is a single pass with no allocations beyond the cached regex. Glob
/// semantics:
/// </para>
/// <list type="bullet">
///   <item><description><c>*.cs</c> matches <c>foo.cs</c> but not <c>src/foo.cs</c>.</description></item>
///   <item><description><c>**/*.cs</c> matches any <c>.cs</c> file at any depth.</description></item>
///   <item><description><c>src/**</c> matches everything under <c>src</c>.</description></item>
///   <item><description><c>foo?bar.txt</c> matches <c>fooXbar.txt</c> (exactly one char between).</description></item>
/// </list>
/// <para>
/// Comparison is case-insensitive on Windows, case-sensitive elsewhere —
/// matching the default NTFS and ext4 behaviors. The caller can override
/// via <see cref="Compile(string, bool)"/>.
/// </para>
/// </remarks>
public static class PathGlob
{
    /// <summary>
    /// Compile a glob into a <see cref="Regex"/>. Uses the platform default
    /// for case sensitivity.
    /// </summary>
    public static Regex Compile(string glob)
        => Compile(glob, ignoreCase: OperatingSystem.IsWindows());

    /// <summary>Compile a glob with explicit case sensitivity.</summary>
    public static Regex Compile(string glob, bool ignoreCase)
    {
        if (glob is null) throw new ArgumentNullException(nameof(glob));
        var pattern = GlobToRegex(glob);
        var opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;
        if (ignoreCase) opts |= RegexOptions.IgnoreCase;
        return new Regex(pattern, opts);
    }

    /// <summary>
    /// Translate a glob into an anchored regex pattern. Exposed for tests.
    /// </summary>
    internal static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        sb.Append('^');
        var i = 0;
        while (i < glob.Length)
        {
            var c = glob[i];
            if (c == '*')
            {
                // '**' = any path segment, '*' = any char except '/'
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    // Consume '**'
                    i += 2;
                    // Trailing '/' after '**' allows zero path segments.
                    if (i < glob.Length && glob[i] == '/')
                    {
                        sb.Append("(?:.*/)?");
                        i++;
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                    i++;
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else if ("+()^$.{}[]|\\".IndexOf(c) >= 0)
            {
                sb.Append('\\').Append(c);
                i++;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        sb.Append('$');
        return sb.ToString();
    }

    /// <summary>Return true when the path matches the glob.</summary>
    public static bool Matches(string glob, string path)
        => Compile(glob).IsMatch(path.Replace('\\', '/'));
}
