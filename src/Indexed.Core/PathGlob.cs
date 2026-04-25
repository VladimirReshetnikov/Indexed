using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Indexed.Core;

/// <summary>
/// Glob-to-regex translator used to filter candidate file paths in the query
/// executor. Supports the subset of gitignore/rsync glob syntax the search
/// API advertises: <c>*</c> (any chars but <c>/</c>), <c>**</c> (any path),
/// <c>?</c> (single char), <c>{a,b}</c> brace alternation, and literal
/// characters.
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
///   <item><description><c>**/*.{cs,vb}</c> matches either extension.</description></item>
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
        AppendGlobRegex(sb, glob);
        sb.Append('$');
        return sb.ToString();
    }

    private static void AppendGlobRegex(StringBuilder sb, string glob)
    {
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
            else if (c == '{' && TryReadBraceAlternation(glob, i, out var alternatives, out var end))
            {
                sb.Append("(?:");
                for (var alt = 0; alt < alternatives.Count; alt++)
                {
                    if (alt > 0)
                        sb.Append('|');
                    AppendGlobRegex(sb, alternatives[alt]);
                }
                sb.Append(')');
                i = end + 1;
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
    }

    private static bool TryReadBraceAlternation(
        string glob,
        int openBrace,
        out IReadOnlyList<string> alternatives,
        out int closeBrace)
    {
        var depth = 0;
        closeBrace = -1;
        var hasComma = false;
        for (var i = openBrace + 1; i < glob.Length; i++)
        {
            switch (glob[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    if (depth == 0)
                    {
                        closeBrace = i;
                        i = glob.Length;
                    }
                    else
                    {
                        depth--;
                    }
                    break;
                case ',' when depth == 0:
                    hasComma = true;
                    break;
            }
        }

        if (closeBrace < 0 || !hasComma)
        {
            alternatives = Array.Empty<string>();
            return false;
        }

        var result = new List<string>();
        var start = openBrace + 1;
        depth = 0;
        for (var i = start; i < closeBrace; i++)
        {
            switch (glob[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(glob[start..i]);
                    start = i + 1;
                    break;
            }
        }

        result.Add(glob[start..closeBrace]);
        alternatives = result;
        return true;
    }

    /// <summary>Return true when the path matches the glob.</summary>
    public static bool Matches(string glob, string path)
        => Compile(glob).IsMatch(path.Replace('\\', '/'));
}
