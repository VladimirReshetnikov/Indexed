using System;
using System.Collections.Generic;
using System.Linq;

namespace Indexed.Core.RegexTrigrams;

/// <summary>
/// Russ Cox-style analyzer that walks a <see cref="RegexNode"/> tree and
/// returns a <see cref="TrigramExpr"/> — the set of trigrams any match of
/// the pattern must contain. Reference: Russ Cox,
/// <a href="https://swtch.com/~rsc/regexp/regexp4.html">"Regular Expression Matching with a Trigram Index" (2012)</a>.
/// </summary>
/// <remarks>
/// <para>
/// Each subtree is summarized by a <see cref="NodeInfo"/>: whether it can
/// match empty, its finite exact / prefix / suffix string sets (or the
/// infinite sentinel), and the <see cref="TrigramExpr"/> it already forces.
/// Combinators (concat, alt, repeat) propagate these summaries up. Crucially,
/// trigrams that straddle the boundary of a concat contribute to the parent's
/// <see cref="TrigramExpr"/> — that is the trick that lets <c>foo\s+bar</c>
/// require both "foo" and "bar" without fusing them into a single phrase.
/// </para>
/// <para>
/// Invariant guarded by the property test: every string that contains a match
/// for the source pattern satisfies the returned <see cref="TrigramExpr"/>.
/// A bug that over-constrains (returns a trigram the match doesn't actually
/// contain) is a correctness bug; a bug that under-constrains (returns less
/// than it could) is a performance bug. When in doubt the implementation
/// returns <see cref="TrigramExpr.True"/> — accept slower, never wrong.
/// </para>
/// </remarks>
public static class TrigramAnalyzer
{
    /// <summary>
    /// Parse the given pattern and return the trigram requirement. When the
    /// pattern is case-insensitive the caller is expected to have
    /// already lower-cased the extracted strings — FTS5's default trigram
    /// tokenizer matches case-insensitively and the analyzer cooperates by
    /// lower-casing every extracted trigram.
    /// </summary>
    /// <param name="pattern">Regex source.</param>
    /// <param name="caseSensitive">
    /// When <c>false</c>, characters whose case variants would break trigram
    /// extraction for a single-case source are treated conservatively —
    /// this analyzer always produces lowercase trigrams, which is the same
    /// normalization applied by FTS5's default trigram tokenizer.
    /// </param>
    public static TrigramExpr Analyze(string pattern, bool caseSensitive = false)
    {
        if (string.IsNullOrEmpty(pattern)) return TrigramExpr.True;
        RegexNode root;
        try
        {
            root = RegexParser.Parse(pattern);
        }
        catch
        {
            return TrigramExpr.True;
        }
        var info = new Visitor(caseSensitive).Walk(root);
        // After walking the top, we can optionally add trigrams derived from
        // the whole exact set (if bounded and non-empty) as a single OR —
        // typical literal patterns flow through this path.
        var exactTrigrams = TrigramsOfExact(info.Exact);
        return TrigramExpr.AndOf(new[] { info.Match, exactTrigrams });
    }

    private static TrigramExpr TrigramsOfExact(BoundedStringSet exact)
    {
        if (exact.IsInfinite) return TrigramExpr.True;
        if (exact.Count == 0) return TrigramExpr.True;

        // OR of AndOfWindows for each concrete string. If any string contributes
        // no trigrams (too short), we OR in True, which collapses to True and
        // drops this contribution — a weaker but valid outcome.
        var branches = exact.Select(s => TrigramExtraction.AndOfWindows(s)).ToArray();
        return TrigramExpr.OrOf(branches);
    }

    private sealed class NodeInfo
    {
        public bool CanMatchEmpty;
        public BoundedStringSet Exact = BoundedStringSet.Empty;
        public BoundedStringSet Prefix = BoundedStringSet.Empty;
        public BoundedStringSet Suffix = BoundedStringSet.Empty;
        public TrigramExpr Match = TrigramExpr.True;
    }

    private sealed class Visitor : IRegexNodeVisitor<NodeInfo>
    {
        private readonly bool _caseSensitive;

        public Visitor(bool caseSensitive) { _caseSensitive = caseSensitive; }

        public NodeInfo Walk(RegexNode n) => n.Accept(this);

        public NodeInfo VisitLiteral(LiteralNode n)
        {
            var text = _caseSensitive ? n.Text : n.Text.ToLowerInvariant();
            if (text.Length == 0)
            {
                return new NodeInfo
                {
                    CanMatchEmpty = true,
                    Exact = BoundedStringSet.Of(""),
                    Prefix = BoundedStringSet.Of(""),
                    Suffix = BoundedStringSet.Of(""),
                    Match = TrigramExpr.True,
                };
            }
            return new NodeInfo
            {
                CanMatchEmpty = false,
                Exact = BoundedStringSet.Of(text),
                Prefix = BoundedStringSet.Of(text),
                Suffix = BoundedStringSet.Of(text),
                Match = TrigramExtraction.AndOfWindows(text),
            };
        }

        public NodeInfo VisitCharClass(CharClassNode n)
        {
            if (n.Chars is null || n.Negated)
            {
                // Negated or unbounded class — can't enumerate.
                return new NodeInfo
                {
                    CanMatchEmpty = false,
                    Exact = BoundedStringSet.Infinite,
                    Prefix = BoundedStringSet.Infinite,
                    Suffix = BoundedStringSet.Infinite,
                    Match = TrigramExpr.True,
                };
            }
            if (n.Chars.Count == 0)
            {
                // Unmatchable class; represent as empty exact set.
                return new NodeInfo { Match = TrigramExpr.True };
            }
            var chars = _caseSensitive
                ? n.Chars
                : n.Chars.Select(c => char.ToLowerInvariant(c)).ToList();
            var exact = BoundedStringSet.Of(chars.Select(c => c.ToString()));
            return new NodeInfo
            {
                CanMatchEmpty = false,
                Exact = exact,
                Prefix = exact,
                Suffix = exact,
                Match = TrigramExpr.True, // single char = no 3-gram constraint alone
            };
        }

        public NodeInfo VisitAnyChar(AnyCharNode n)
            => new()
            {
                CanMatchEmpty = false,
                Exact = BoundedStringSet.Infinite,
                Prefix = BoundedStringSet.Infinite,
                Suffix = BoundedStringSet.Infinite,
                Match = TrigramExpr.True,
            };

        public NodeInfo VisitAnchor(AnchorNode n)
            => new()
            {
                CanMatchEmpty = true,
                Exact = BoundedStringSet.Of(""),
                Prefix = BoundedStringSet.Of(""),
                Suffix = BoundedStringSet.Of(""),
                Match = TrigramExpr.True,
            };

        public NodeInfo VisitOpaque(OpaqueNode n)
            => new()
            {
                CanMatchEmpty = false,
                Exact = BoundedStringSet.Infinite,
                Prefix = BoundedStringSet.Infinite,
                Suffix = BoundedStringSet.Infinite,
                Match = TrigramExpr.True,
            };

        public NodeInfo VisitConcat(ConcatNode n)
        {
            var acc = new NodeInfo
            {
                CanMatchEmpty = true,
                Exact = BoundedStringSet.Of(""),
                Prefix = BoundedStringSet.Of(""),
                Suffix = BoundedStringSet.Of(""),
                Match = TrigramExpr.True,
            };
            foreach (var child in n.Children)
            {
                var rhs = Walk(child);
                acc = Concat(acc, rhs);
            }
            return acc;
        }

        public NodeInfo VisitAlt(AltNode n)
        {
            NodeInfo? acc = null;
            foreach (var child in n.Children)
            {
                var c = Walk(child);
                acc = acc is null ? c : Alt(acc, c);
            }
            return acc ?? new NodeInfo
            {
                CanMatchEmpty = false,
                Exact = BoundedStringSet.Empty,
                Prefix = BoundedStringSet.Empty,
                Suffix = BoundedStringSet.Empty,
                Match = TrigramExpr.True,
            };
        }

        public NodeInfo VisitRepeat(RepeatNode n)
        {
            var inner = Walk(n.Inner);
            // {0,0} — matches only empty string. Rare but valid.
            if (n.Min == 0 && n.Max == 0)
            {
                return new NodeInfo
                {
                    CanMatchEmpty = true,
                    Exact = BoundedStringSet.Of(""),
                    Prefix = BoundedStringSet.Of(""),
                    Suffix = BoundedStringSet.Of(""),
                    Match = TrigramExpr.True,
                };
            }
            // {m,n} with m >= 1: concat inner m times, then allow optional
            // extension up to n. The "required" trigrams are contributed by
            // the m guaranteed copies. For simplicity, we do the min-copies
            // concat exactly and then disable exact/prefix/suffix propagation
            // when more optional copies are allowed.
            if (n.Min >= 1)
            {
                var acc = inner;
                for (var i = 1; i < n.Min; i++)
                    acc = Concat(acc, inner);

                if (n.Max != n.Min)
                {
                    // More optional copies: lose exactness, keep match and
                    // prefix/suffix approximations.
                    return new NodeInfo
                    {
                        CanMatchEmpty = false,
                        Exact = BoundedStringSet.Infinite,
                        Prefix = acc.Prefix,
                        Suffix = BoundedStringSet.Infinite,
                        Match = acc.Match,
                    };
                }
                return acc;
            }

            // {0,_}: can match empty → no required trigrams overall.
            return new NodeInfo
            {
                CanMatchEmpty = true,
                Exact = BoundedStringSet.Infinite,
                Prefix = BoundedStringSet.Infinite,
                Suffix = BoundedStringSet.Infinite,
                Match = TrigramExpr.True,
            };
        }

        private static NodeInfo Concat(NodeInfo x, NodeInfo y)
        {
            var r = new NodeInfo();
            r.CanMatchEmpty = x.CanMatchEmpty && y.CanMatchEmpty;

            // exact: x.exact × y.exact
            r.Exact = BoundedStringSet.CrossConcat(x.Exact, y.Exact);

            // prefix: if x is exact and not empty-only, use x.exact × y.prefix ∪ (x.exact if x.canEmpty)
            // Simplification: if x.exact bounded, prefix = CrossConcat(x.exact, y.prefix) ∪ (y.prefix if x.canEmpty);
            // else prefix = x.prefix.
            r.Prefix = !x.Exact.IsInfinite
                ? (x.CanMatchEmpty
                    ? BoundedStringSet.Union(y.Prefix, BoundedStringSet.CrossConcat(x.Exact, y.Prefix))
                    : BoundedStringSet.CrossConcat(x.Exact, y.Prefix))
                : x.Prefix;

            r.Suffix = !y.Exact.IsInfinite
                ? (y.CanMatchEmpty
                    ? BoundedStringSet.Union(x.Suffix, BoundedStringSet.CrossConcat(x.Suffix, y.Exact))
                    : BoundedStringSet.CrossConcat(x.Suffix, y.Exact))
                : y.Suffix;

            // match: AND of child matches, plus any boundary trigrams.
            var boundary = BoundaryTrigrams(x.Suffix, y.Prefix);
            r.Match = TrigramExpr.AndOf(new[] { x.Match, y.Match, boundary });

            // If we have a bounded Exact now, use the trigrams of those exact
            // strings as an (equivalent, usually tighter) match constraint.
            if (!r.Exact.IsInfinite && r.Exact.Count > 0)
            {
                var ex = TrigramsOfExactSet(r.Exact);
                r.Match = TrigramExpr.AndOf(new[] { r.Match, ex });
            }

            return r;
        }

        private static NodeInfo Alt(NodeInfo x, NodeInfo y)
        {
            var r = new NodeInfo();
            r.CanMatchEmpty = x.CanMatchEmpty || y.CanMatchEmpty;
            r.Exact = BoundedStringSet.Union(x.Exact, y.Exact);
            r.Prefix = BoundedStringSet.Union(x.Prefix, y.Prefix);
            r.Suffix = BoundedStringSet.Union(x.Suffix, y.Suffix);
            r.Match = TrigramExpr.OrOf(new[] { x.Match, y.Match });
            return r;
        }

        // Trigrams that MUST appear due to the boundary between x·y. For each
        // (xs, yp) ∈ x.suffix × y.prefix, the straddling 3-grams are:
        //   xs[-2..] + yp[0]   (if xs.Length ≥ 2 and yp.Length ≥ 1)
        //   xs[-1]   + yp[..2] (if xs.Length ≥ 1 and yp.Length ≥ 2)
        // Take the OR of the AND over each pair, since one (xs, yp) pair
        // must apply to any concrete match.
        private static TrigramExpr BoundaryTrigrams(BoundedStringSet suffix, BoundedStringSet prefix)
        {
            if (suffix.IsInfinite || prefix.IsInfinite) return TrigramExpr.True;
            if (suffix.Count == 0 || prefix.Count == 0) return TrigramExpr.True;

            var branches = new List<TrigramExpr>();
            foreach (var xs in suffix)
            {
                foreach (var yp in prefix)
                {
                    var trigrams = new HashSet<string>(StringComparer.Ordinal);
                    if (xs.Length >= 2 && yp.Length >= 1)
                        trigrams.Add(xs.Substring(xs.Length - 2) + yp[0]);
                    if (xs.Length >= 1 && yp.Length >= 2)
                        trigrams.Add(xs[^1].ToString() + yp.Substring(0, 2));
                    if (trigrams.Count == 0) return TrigramExpr.True;
                    var andNode = TrigramExpr.AndOf(trigrams.Select(TrigramExpr.Of));
                    branches.Add(andNode);
                }
            }
            return TrigramExpr.OrOf(branches);
        }

        private static TrigramExpr TrigramsOfExactSet(BoundedStringSet set)
        {
            if (set.IsInfinite || set.Count == 0) return TrigramExpr.True;
            var branches = set.Select(s => TrigramExtraction.AndOfWindows(s)).ToArray();
            return TrigramExpr.OrOf(branches);
        }
    }
}
