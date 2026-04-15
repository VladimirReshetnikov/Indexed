using System;
using System.Collections.Generic;
using System.Globalization;

namespace Indexed.Core.RegexTrigrams;

/// <summary>
/// Hand-rolled recursive-descent parser that converts a regex source string
/// into a <see cref="RegexNode"/> tree tailored for trigram analysis.
/// </summary>
/// <remarks>
/// <para>
/// Grammar (informal, PCRE-ish):
/// </para>
/// <code>
/// alt     := concat ( '|' concat )*
/// concat  := atom*
/// atom    := primary ( '*' | '+' | '?' | '{' N (',' M?)? '}' )?
/// primary := '(' alt ')' | charclass | anchor | escape | literal | '.'
/// </code>
/// <para>
/// Unsupported constructs (lookaround, backrefs, named/non-capturing groups,
/// inline flags) parse as <see cref="OpaqueNode"/>. This deliberately loses
/// precision rather than failing — the analyzer will fall back to full-scan
/// (<see cref="TrigramExpr.True"/>) for those subtrees, which keeps the
/// superset invariant.
/// </para>
/// </remarks>
internal sealed class RegexParser
{
    private readonly string _src;
    private int _pos;

    private RegexParser(string src) { _src = src; }

    public static RegexNode Parse(string pattern)
    {
        if (pattern is null) throw new ArgumentNullException(nameof(pattern));
        var p = new RegexParser(pattern);
        var node = p.ParseAlt();
        if (p._pos < p._src.Length)
        {
            // Unbalanced ')' or similar — give up and treat the whole pattern
            // as opaque rather than lying about trigram requirements.
            return new OpaqueNode();
        }
        return node;
    }

    private RegexNode ParseAlt()
    {
        var branches = new List<RegexNode> { ParseConcat() };
        while (Peek() == '|')
        {
            _pos++;
            branches.Add(ParseConcat());
        }
        return branches.Count == 1 ? branches[0] : new AltNode(branches);
    }

    private RegexNode ParseConcat()
    {
        var children = new List<RegexNode>();
        while (_pos < _src.Length)
        {
            var c = _src[_pos];
            if (c == '|' || c == ')') break;
            var atom = ParseAtom();
            if (atom is null) break;
            children.Add(atom);
        }
        return children.Count switch
        {
            0 => new LiteralNode(string.Empty),
            1 => children[0],
            _ => CompactLiterals(children),
        };
    }

    // Merge adjacent LiteralNode siblings so analysis sees longer exact strings.
    private static RegexNode CompactLiterals(List<RegexNode> xs)
    {
        var merged = new List<RegexNode>(xs.Count);
        var buf = new System.Text.StringBuilder();
        void Flush()
        {
            if (buf.Length > 0) { merged.Add(new LiteralNode(buf.ToString())); buf.Clear(); }
        }
        foreach (var n in xs)
        {
            if (n is LiteralNode lit) buf.Append(lit.Text);
            else { Flush(); merged.Add(n); }
        }
        Flush();
        return merged.Count == 1 ? merged[0] : new ConcatNode(merged);
    }

    private RegexNode? ParseAtom()
    {
        if (_pos >= _src.Length) return null;
        var primary = ParsePrimary();
        if (primary is null) return null;

        // Quantifier? Repetition greediness ('?' suffix on *, +, ?, {m,n}) is
        // irrelevant for trigram analysis; we consume and ignore it.
        if (_pos < _src.Length)
        {
            var c = _src[_pos];
            switch (c)
            {
                case '*':
                    _pos++; ConsumeLazyMarker();
                    return new RepeatNode(primary, 0, -1);
                case '+':
                    _pos++; ConsumeLazyMarker();
                    return new RepeatNode(primary, 1, -1);
                case '?':
                    _pos++; ConsumeLazyMarker();
                    return new RepeatNode(primary, 0, 1);
                case '{':
                    if (TryParseCounted(out var min, out var max))
                    {
                        ConsumeLazyMarker();
                        return new RepeatNode(primary, min, max);
                    }
                    // Fell through — literal '{'.
                    break;
            }
        }
        return primary;
    }

    private void ConsumeLazyMarker()
    {
        if (_pos < _src.Length && _src[_pos] == '?') _pos++;
    }

    private bool TryParseCounted(out int min, out int max)
    {
        var save = _pos;
        min = 0; max = -1;
        if (_src[_pos] != '{') return false;
        _pos++;
        if (!ReadInt(out min)) { _pos = save; return false; }
        if (_pos < _src.Length && _src[_pos] == ',')
        {
            _pos++;
            if (_pos < _src.Length && _src[_pos] == '}') { _pos++; max = -1; return true; }
            if (!ReadInt(out max)) { _pos = save; return false; }
        }
        else
        {
            max = min;
        }
        if (_pos >= _src.Length || _src[_pos] != '}') { _pos = save; return false; }
        _pos++;
        return true;
    }

    private bool ReadInt(out int value)
    {
        var start = _pos;
        while (_pos < _src.Length && char.IsDigit(_src[_pos])) _pos++;
        if (_pos == start) { value = 0; return false; }
        return int.TryParse(_src.AsSpan(start, _pos - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private RegexNode? ParsePrimary()
    {
        if (_pos >= _src.Length) return null;
        var c = _src[_pos];

        switch (c)
        {
            case '(':
                return ParseGroup();
            case '[':
                return ParseCharClass();
            case '.':
                _pos++;
                return new AnyCharNode();
            case '^':
                _pos++;
                return new AnchorNode(AnchorKind.StartOfLine);
            case '$':
                _pos++;
                return new AnchorNode(AnchorKind.EndOfLine);
            case '\\':
                return ParseEscape();
            case '*': case '+': case '?': case '{': case ')': case '|':
                // Quantifier without preceding atom, or unbalanced close; bail.
                return null;
            default:
                _pos++;
                return new LiteralNode(c.ToString());
        }
    }

    private RegexNode ParseGroup()
    {
        // Consume '('
        _pos++;

        // Detect (?:...), (?=...), (?!...), (?<=...), (?<!...), (?P<name>...),
        // (?<name>...), (?flags:...), etc. For trigram purposes the simplest
        // correct stance is: capturing groups and non-capturing groups contribute
        // their inner pattern; everything else is opaque.
        bool isCapturing = true;
        bool opaque = false;

        if (_pos < _src.Length && _src[_pos] == '?')
        {
            if (_pos + 1 < _src.Length && _src[_pos + 1] == ':')
            {
                _pos += 2; isCapturing = false;
            }
            else
            {
                opaque = true;
            }
        }

        if (opaque)
        {
            // Skip to the matching ')' without interpreting.
            SkipToMatchingParen();
            return new OpaqueNode();
        }

        var inner = ParseAlt();
        if (_pos < _src.Length && _src[_pos] == ')')
            _pos++;
        _ = isCapturing;
        return inner;
    }

    private void SkipToMatchingParen()
    {
        var depth = 1;
        while (_pos < _src.Length && depth > 0)
        {
            var c = _src[_pos++];
            if (c == '\\' && _pos < _src.Length) { _pos++; continue; }
            if (c == '(') depth++;
            else if (c == ')') depth--;
        }
    }

    private RegexNode ParseCharClass()
    {
        // '['
        _pos++;
        var negated = false;
        if (_pos < _src.Length && _src[_pos] == '^')
        {
            negated = true;
            _pos++;
        }

        var chars = new List<char>();
        var tooLarge = false;

        // A leading ']' is literal.
        if (_pos < _src.Length && _src[_pos] == ']')
        {
            chars.Add(']');
            _pos++;
        }

        while (_pos < _src.Length && _src[_pos] != ']')
        {
            char lo;
            if (_src[_pos] == '\\')
            {
                var esc = ReadEscapeChar(out var classMembers);
                if (classMembers is not null)
                {
                    tooLarge = true;
                    continue;
                }
                lo = esc;
            }
            else
            {
                lo = _src[_pos++];
            }

            if (_pos + 1 < _src.Length && _src[_pos] == '-' && _src[_pos + 1] != ']')
            {
                _pos++; // '-'
                char hi;
                if (_src[_pos] == '\\')
                {
                    var esc = ReadEscapeChar(out _);
                    hi = esc;
                }
                else
                {
                    hi = _src[_pos++];
                }
                if (hi < lo) (lo, hi) = (hi, lo);
                // Cap the enumerated size at 128 chars — anything bigger
                // contributes no trigram constraint.
                if (hi - lo > 128) { tooLarge = true; continue; }
                for (var ch = lo; ch <= hi; ch++) chars.Add(ch);
            }
            else
            {
                chars.Add(lo);
            }
        }

        if (_pos < _src.Length && _src[_pos] == ']') _pos++;

        return tooLarge
            ? new CharClassNode(null, negated)
            : new CharClassNode(chars, negated);
    }

    // Read a single char from an escape sequence inside a character class.
    // Returns whether the sequence describes a large predefined class (\w, \s,
    // \d, \W, \S, \D) in the out parameter.
    private char ReadEscapeChar(out IReadOnlyList<char>? predefined)
    {
        predefined = null;
        _pos++; // consume '\'
        if (_pos >= _src.Length) return '\\';
        var c = _src[_pos++];
        switch (c)
        {
            case 'n': return '\n';
            case 'r': return '\r';
            case 't': return '\t';
            case 'f': return '\f';
            case 'v': return '\v';
            case '0': return '\0';
            case 'w': case 'W': case 's': case 'S': case 'd': case 'D':
                predefined = Array.Empty<char>();
                return ' ';
            case 'x':
                if (_pos + 1 < _src.Length
                    && byte.TryParse(_src.AsSpan(_pos, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                {
                    _pos += 2;
                    return (char)hex;
                }
                return 'x';
            default:
                return c;
        }
    }

    private RegexNode ParseEscape()
    {
        _pos++; // '\'
        if (_pos >= _src.Length) return new LiteralNode("\\");
        var c = _src[_pos++];
        switch (c)
        {
            case 'd': return new CharClassNode(EnumDigits(), negated: false);
            case 'D': return new CharClassNode(null, negated: true);
            case 'w': return new CharClassNode(null, negated: false);
            case 'W': return new CharClassNode(null, negated: true);
            case 's': return new CharClassNode(null, negated: false);
            case 'S': return new CharClassNode(null, negated: true);
            case 'b': return new AnchorNode(AnchorKind.WordBoundary);
            case 'B': return new AnchorNode(AnchorKind.NonWordBoundary);
            case 'A': return new AnchorNode(AnchorKind.StartOfString);
            case 'Z': case 'z': return new AnchorNode(AnchorKind.EndOfString);
            case 'n': return new LiteralNode("\n");
            case 'r': return new LiteralNode("\r");
            case 't': return new LiteralNode("\t");
            case 'f': return new LiteralNode("\f");
            case 'v': return new LiteralNode("\v");
            case '0': return new LiteralNode("\0");
            case 'x':
                if (_pos + 1 < _src.Length
                    && byte.TryParse(_src.AsSpan(_pos, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                {
                    _pos += 2;
                    return new LiteralNode(((char)hex).ToString());
                }
                return new LiteralNode("x");
            default:
                return new LiteralNode(c.ToString());
        }
    }

    private static IReadOnlyList<char> EnumDigits()
    {
        var d = new char[10];
        for (var i = 0; i < 10; i++) d[i] = (char)('0' + i);
        return d;
    }

    private char Peek()
    {
        return _pos < _src.Length ? _src[_pos] : '\0';
    }
}
