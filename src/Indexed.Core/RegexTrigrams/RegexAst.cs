using System.Collections.Generic;

namespace Indexed.Core.RegexTrigrams;

/// <summary>
/// Minimal regex AST covering the subset <see cref="RegexParser"/> recognizes:
/// literal, concatenation, alternation, repetition (<c>*</c>, <c>+</c>,
/// <c>?</c>, <c>{n,m}</c>), character classes, anchors, and the
/// unrestricted wildcard <c>.</c>.
/// </summary>
/// <remarks>
/// The analyzer in <see cref="TrigramAnalyzer"/> walks this tree to compute
/// required trigram sets. The AST intentionally omits features that don't
/// affect trigram candidates (backrefs, named groups, lookaround): those fall
/// back to <see cref="TrigramExpr.True"/>.
/// </remarks>
internal abstract class RegexNode
{
    public abstract T Accept<T>(IRegexNodeVisitor<T> visitor);
}

internal interface IRegexNodeVisitor<T>
{
    T VisitLiteral(LiteralNode n);
    T VisitCharClass(CharClassNode n);
    T VisitAnyChar(AnyCharNode n);
    T VisitConcat(ConcatNode n);
    T VisitAlt(AltNode n);
    T VisitRepeat(RepeatNode n);
    T VisitAnchor(AnchorNode n);
    T VisitOpaque(OpaqueNode n);
}

internal sealed class LiteralNode : RegexNode
{
    public string Text { get; }
    public LiteralNode(string text) { Text = text; }
    public override T Accept<T>(IRegexNodeVisitor<T> visitor) => visitor.VisitLiteral(this);
}

internal sealed class CharClassNode : RegexNode
{
    /// <summary>Enumerated characters the class matches, or <c>null</c> when too large (e.g. <c>\w</c>, <c>\s</c>).</summary>
    public IReadOnlyList<char>? Chars { get; }
    public bool Negated { get; }
    public CharClassNode(IReadOnlyList<char>? chars, bool negated = false) { Chars = chars; Negated = negated; }
    public override T Accept<T>(IRegexNodeVisitor<T> visitor) => visitor.VisitCharClass(this);
}

internal sealed class AnyCharNode : RegexNode
{
    // `.` — matches any char (no newline by default but trigram planner
    // doesn't care about the dotall distinction).
    public override T Accept<T>(IRegexNodeVisitor<T> visitor) => visitor.VisitAnyChar(this);
}

internal sealed class ConcatNode : RegexNode
{
    public IReadOnlyList<RegexNode> Children { get; }
    public ConcatNode(IReadOnlyList<RegexNode> children) { Children = children; }
    public override T Accept<T>(IRegexNodeVisitor<T> visitor) => visitor.VisitConcat(this);
}

internal sealed class AltNode : RegexNode
{
    public IReadOnlyList<RegexNode> Children { get; }
    public AltNode(IReadOnlyList<RegexNode> children) { Children = children; }
    public override T Accept<T>(IRegexNodeVisitor<T> visitor) => visitor.VisitAlt(this);
}

internal sealed class RepeatNode : RegexNode
{
    public RegexNode Inner { get; }
    public int Min { get; }
    public int Max { get; } // -1 = unbounded
    public RepeatNode(RegexNode inner, int min, int max) { Inner = inner; Min = min; Max = max; }
    public override T Accept<T>(IRegexNodeVisitor<T> visitor) => visitor.VisitRepeat(this);
}

internal enum AnchorKind { StartOfString, EndOfString, StartOfLine, EndOfLine, WordBoundary, NonWordBoundary }

internal sealed class AnchorNode : RegexNode
{
    public AnchorKind Kind { get; }
    public AnchorNode(AnchorKind kind) { Kind = kind; }
    public override T Accept<T>(IRegexNodeVisitor<T> visitor) => visitor.VisitAnchor(this);
}

/// <summary>
/// Escape hatch: features the parser recognizes but chooses not to analyze
/// (backrefs, lookaround, named groups, inline options). Treated as "matches
/// anything" for trigram-planning purposes, which keeps the superset
/// invariant.
/// </summary>
internal sealed class OpaqueNode : RegexNode
{
    public override T Accept<T>(IRegexNodeVisitor<T> visitor) => visitor.VisitOpaque(this);
}
