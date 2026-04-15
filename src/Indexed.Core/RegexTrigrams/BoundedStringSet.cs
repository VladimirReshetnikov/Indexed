using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Indexed.Core.RegexTrigrams;

/// <summary>
/// Fixed-cap set of strings used by <see cref="TrigramAnalyzer"/>. Any
/// operation that would exceed <see cref="MaxCount"/> or string length
/// <see cref="MaxStringLength"/> collapses the set to <see cref="Infinite"/> —
/// a sentinel meaning "too many possibilities to enumerate, treat as
/// unconstrained".
/// </summary>
/// <remarks>
/// <para>
/// The caps are small on purpose: the Russ Cox algorithm's effectiveness
/// collapses once exacts explode combinatorially, and keeping the set tiny
/// caps worst-case analysis cost for pathological patterns. The chosen
/// numbers match the spirit of Cox's <c>maxExact = 7</c>-ish ceilings while
/// being a little more generous for the patterns agents actually write.
/// </para>
/// </remarks>
internal sealed class BoundedStringSet : IEnumerable<string>
{
    /// <summary>Maximum number of strings before we collapse to infinite.</summary>
    public const int MaxCount = 64;

    /// <summary>Maximum per-string length before we collapse to infinite.</summary>
    public const int MaxStringLength = 32;

    /// <summary>Sentinel: "too many / unbounded".</summary>
    public static readonly BoundedStringSet Infinite = new(isInfinite: true);

    /// <summary>Empty set — contains no strings at all.</summary>
    public static readonly BoundedStringSet Empty = new(isInfinite: false);

    private readonly SortedSet<string>? _items;
    public bool IsInfinite { get; }

    private BoundedStringSet(bool isInfinite)
    {
        IsInfinite = isInfinite;
        _items = isInfinite ? null : new SortedSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Construct a finite set with the given strings.</summary>
    public static BoundedStringSet Of(params string[] items) => Of((IEnumerable<string>)items);

    /// <summary>Construct a finite set with the given strings; collapses when caps exceed.</summary>
    public static BoundedStringSet Of(IEnumerable<string> items)
    {
        var s = new BoundedStringSet(isInfinite: false);
        foreach (var x in items)
        {
            if (x.Length > MaxStringLength) return Infinite;
            s._items!.Add(x);
            if (s._items.Count > MaxCount) return Infinite;
        }
        return s;
    }

    /// <summary>Count of strings when finite; undefined when infinite.</summary>
    public int Count => _items?.Count ?? int.MaxValue;

    /// <summary>Union of two sets.</summary>
    public static BoundedStringSet Union(BoundedStringSet a, BoundedStringSet b)
    {
        if (a.IsInfinite || b.IsInfinite) return Infinite;
        var merged = new SortedSet<string>(a._items!, StringComparer.Ordinal);
        foreach (var x in b._items!) merged.Add(x);
        if (merged.Count > MaxCount) return Infinite;
        return Of(merged);
    }

    /// <summary>Cartesian cross-concat: {a·b | a ∈ x, b ∈ y}.</summary>
    public static BoundedStringSet CrossConcat(BoundedStringSet x, BoundedStringSet y)
    {
        if (x.IsInfinite || y.IsInfinite) return Infinite;
        if (x._items!.Count == 0 || y._items!.Count == 0) return Empty;
        if ((long)x._items.Count * y._items.Count > MaxCount) return Infinite;
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var a in x._items)
        {
            foreach (var b in y._items)
            {
                var joined = a + b;
                if (joined.Length > MaxStringLength) return Infinite;
                result.Add(joined);
                if (result.Count > MaxCount) return Infinite;
            }
        }
        return Of(result);
    }

    public IEnumerator<string> GetEnumerator()
        => _items?.GetEnumerator() ?? Enumerable.Empty<string>().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
