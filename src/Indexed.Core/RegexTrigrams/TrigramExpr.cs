using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Indexed.Core.RegexTrigrams;

/// <summary>
/// Boolean expression over trigram literals used to narrow FTS5 candidate
/// sets. A <see cref="TrigramExpr"/> represents the trigrams that a string
/// MUST contain to possibly match the source pattern — callers AND candidates
/// from this expression with application-level regex re-verification.
/// </summary>
/// <remarks>
/// <para>
/// The grammar has three ordinary nodes (<see cref="Literal"/>,
/// <see cref="And"/>, <see cref="Or"/>) plus a <see cref="True"/> sentinel
/// meaning "no constraint available — fall back to full scan". The
/// <see cref="True"/> shape lets combinators compose uniformly:
/// <c>And(True, x) = x</c>, <c>Or(True, _) = True</c>, and
/// <see cref="TrigramExpr.ToFts5MatchExpression"/> returns <c>null</c> for it
/// so the caller knows to skip the MATCH lookup.
/// </para>
/// <para>
/// Correctness invariant: every string containing a match for the source
/// pattern must evaluate this expression to true. The Russ Cox analyzer in
/// <see cref="TrigramAnalyzer"/> enforces the invariant; tests in
/// <c>RegexTrigramsTests</c> pin it with randomized property checks.
/// </para>
/// </remarks>
public abstract class TrigramExpr
{
    /// <summary>Sentinel meaning "no trigrams extractable — scan the world".</summary>
    public static readonly TrigramExpr True = new TrueExpr();

    /// <summary>Construct a leaf requiring the given trigram.</summary>
    public static TrigramExpr Of(string trigram)
    {
        if (trigram is null) throw new ArgumentNullException(nameof(trigram));
        if (trigram.Length != 3)
            throw new ArgumentException($"trigram must be 3 chars, got '{trigram}' ({trigram.Length})", nameof(trigram));
        return new Literal(trigram);
    }

    /// <summary>Conjunction; simplifies trivial cases.</summary>
    public static TrigramExpr AndOf(IEnumerable<TrigramExpr> children)
    {
        var flat = new List<TrigramExpr>();
        foreach (var c in children)
        {
            if (c is TrueExpr) continue;
            if (c is And a) flat.AddRange(a.Children);
            else flat.Add(c);
        }
        flat = DedupeStable(flat);
        return flat.Count switch
        {
            0 => True,
            1 => flat[0],
            _ => new And(flat),
        };
    }

    /// <summary>Disjunction; simplifies trivial cases.</summary>
    public static TrigramExpr OrOf(IEnumerable<TrigramExpr> children)
    {
        var flat = new List<TrigramExpr>();
        foreach (var c in children)
        {
            if (c is TrueExpr)
                return True; // (anything OR True) = True
            if (c is Or o) flat.AddRange(o.Children);
            else flat.Add(c);
        }
        flat = DedupeStable(flat);
        return flat.Count switch
        {
            0 => True,
            1 => flat[0],
            _ => new Or(flat),
        };
    }

    /// <summary>
    /// Emit an FTS5 <c>MATCH</c> expression string, or <c>null</c> when the
    /// tree is <see cref="True"/>.
    /// </summary>
    public string? ToFts5MatchExpression()
    {
        if (this is TrueExpr) return null;
        var sb = new StringBuilder();
        WriteFts5(this, sb);
        return sb.ToString();
    }

    private static void WriteFts5(TrigramExpr node, StringBuilder sb)
    {
        switch (node)
        {
            case TrueExpr:
                // Should be filtered at top; emit a no-op literal for safety.
                sb.Append("\"\"");
                break;
            case Literal lit:
                sb.Append('"');
                sb.Append(EscapeFts5Phrase(lit.Trigram));
                sb.Append('"');
                break;
            case And a:
                sb.Append('(');
                for (var i = 0; i < a.Children.Count; i++)
                {
                    if (i > 0) sb.Append(" AND ");
                    WriteFts5(a.Children[i], sb);
                }
                sb.Append(')');
                break;
            case Or o:
                sb.Append('(');
                for (var i = 0; i < o.Children.Count; i++)
                {
                    if (i > 0) sb.Append(" OR ");
                    WriteFts5(o.Children[i], sb);
                }
                sb.Append(')');
                break;
        }
    }

    // FTS5 phrase literals are doubled-quoted. We never embed a literal " since
    // trigrams are derived from source bytes and then lowercased ASCII, but
    // defensively handle it anyway.
    private static string EscapeFts5Phrase(string s) => s.Replace("\"", "\"\"");

    private static List<TrigramExpr> DedupeStable(List<TrigramExpr> xs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TrigramExpr>(xs.Count);
        foreach (var x in xs)
        {
            var key = x.CanonicalKey();
            if (seen.Add(key)) result.Add(x);
        }
        return result;
    }

    internal abstract string CanonicalKey();

    // ----- nodes -----

    internal sealed class TrueExpr : TrigramExpr
    {
        internal override string CanonicalKey() => "T";
    }

    /// <summary>Leaf requiring one lowercase 3-character trigram.</summary>
    public sealed class Literal : TrigramExpr
    {
        public string Trigram { get; }
        internal Literal(string trigram) { Trigram = trigram; }
        internal override string CanonicalKey() => "L:" + Trigram;
    }

    /// <summary>All children must match.</summary>
    public sealed class And : TrigramExpr
    {
        public IReadOnlyList<TrigramExpr> Children { get; }
        internal And(IReadOnlyList<TrigramExpr> children) { Children = children; }
        internal override string CanonicalKey()
            => "A(" + string.Join(',', Children.Select(c => c.CanonicalKey()).OrderBy(s => s, StringComparer.Ordinal)) + ")";
    }

    /// <summary>Any child matches.</summary>
    public sealed class Or : TrigramExpr
    {
        public IReadOnlyList<TrigramExpr> Children { get; }
        internal Or(IReadOnlyList<TrigramExpr> children) { Children = children; }
        internal override string CanonicalKey()
            => "O(" + string.Join(',', Children.Select(c => c.CanonicalKey()).OrderBy(s => s, StringComparer.Ordinal)) + ")";
    }
}

/// <summary>
/// Helpers for converting strings into trigram sets and expressions.
/// </summary>
public static class TrigramExtraction
{
    /// <summary>
    /// Return the set of 3-character windows of <paramref name="s"/> after
    /// lowercasing. Strings shorter than 3 chars contribute nothing (the
    /// caller must treat this as "no constraint").
    /// </summary>
    public static HashSet<string> WindowsOf(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (s.Length < 3) return set;
        var lower = s.ToLowerInvariant();
        for (var i = 0; i + 3 <= lower.Length; i++)
            set.Add(lower.Substring(i, 3));
        return set;
    }

    /// <summary>
    /// Build <c>AND(t₁, …, tₙ)</c> from every trigram window of <paramref name="s"/>.
    /// Returns <see cref="TrigramExpr.True"/> when the string is too short.
    /// </summary>
    public static TrigramExpr AndOfWindows(string s)
    {
        var windows = WindowsOf(s);
        if (windows.Count == 0) return TrigramExpr.True;
        return TrigramExpr.AndOf(windows.Select(TrigramExpr.Of));
    }
}
