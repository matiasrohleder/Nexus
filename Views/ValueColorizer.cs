using System;
using System.Collections.Generic;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using Nexus.Services;

namespace Nexus.Views;

/// <summary>
/// Highlighting for the value viewer, with the same VS Code "Dark+" palette as the query editor:
/// JSON keys light blue, strings orange, numbers green, true/false/null blue; XML tags blue,
/// attributes light blue, attribute values orange, comments green. A small forgiving lexer
/// (never throws, unterminated constructs run to the end) tokenizes the whole text once per edit.
/// </summary>
public sealed class ValueColorizer : DocumentColorizingTransformer
{
    private enum Kind { Key, String, Number, Keyword, Punct, Comment, TagName, AttrName, AttrValue, Meta }

    private readonly record struct Span(Kind Kind, int Start, int End);

    private static readonly Dictionary<Kind, IBrush> Brushes = new()
    {
        [Kind.Key] = Brush("#9CDCFE"),
        [Kind.String] = Brush("#CE9178"),
        [Kind.Number] = Brush("#B5CEA8"),
        [Kind.Keyword] = Brush("#569CD6"),
        [Kind.Punct] = Brush("#808080"),
        [Kind.Comment] = Brush("#6A9955"),
        [Kind.TagName] = Brush("#569CD6"),
        [Kind.AttrName] = Brush("#9CDCFE"),
        [Kind.AttrValue] = Brush("#CE9178"),
        [Kind.Meta] = Brush("#808080"),
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();

    private ValueKind _language = ValueKind.Text;
    private List<Span>? _spans;

    public ValueKind Language
    {
        get => _language;
        set { _language = value; _spans = null; }
    }

    /// <summary>Marks the cached tokens stale (call on every text change).</summary>
    public void Invalidate() => _spans = null;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_language == ValueKind.Text) return;
        _spans ??= _language == ValueKind.Json
            ? LexJson(CurrentContext.Document.Text)
            : LexXml(CurrentContext.Document.Text);

        int lineStart = line.Offset, lineEnd = line.EndOffset;
        for (int i = FirstEndingAfter(_spans, lineStart); i < _spans.Count; i++)
        {
            var s = _spans[i];
            if (s.Start >= lineEnd) break;
            int from = Math.Max(s.Start, lineStart), to = Math.Min(s.End, lineEnd);
            if (to <= from) continue;
            var brush = Brushes[s.Kind];
            bool italic = s.Kind == Kind.Comment;
            ChangeLinePart(from, to, el =>
            {
                el.TextRunProperties.SetForegroundBrush(brush);
                if (italic)
                    el.TextRunProperties.SetTypeface(new Typeface(el.TextRunProperties.Typeface.FontFamily, FontStyle.Italic));
            });
        }
    }

    private static int FirstEndingAfter(List<Span> spans, int offset)
    {
        int lo = 0, hi = spans.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (spans[mid].End <= offset) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // ---- JSON ---------------------------------------------------------------------------

    private static List<Span> LexJson(string t)
    {
        var spans = new List<Span>();
        int i = 0, n = t.Length;
        while (i < n)
        {
            char c = t[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            int start = i;
            if (c == '"')
            {
                i = SkipString(t, i, '"');
                // A string followed by ':' is an object key.
                int j = i;
                while (j < n && char.IsWhiteSpace(t[j])) j++;
                spans.Add(new Span(j < n && t[j] == ':' ? Kind.Key : Kind.String, start, i));
            }
            else if (c == '-' || char.IsDigit(c))
            {
                i++;
                while (i < n && (char.IsLetterOrDigit(t[i]) || t[i] is '.' or '+' or '-')) i++;
                spans.Add(new Span(Kind.Number, start, i));
            }
            else if (char.IsLetter(c))
            {
                while (i < n && char.IsLetter(t[i])) i++;
                spans.Add(new Span(Kind.Keyword, start, i));
            }
            else if (c == '/' && i + 1 < n && t[i + 1] == '/')
            {
                while (i < n && t[i] != '\n') i++;
                spans.Add(new Span(Kind.Comment, start, i));
            }
            else if (c == '/' && i + 1 < n && t[i + 1] == '*')
            {
                int end = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? n : end + 2;
                spans.Add(new Span(Kind.Comment, start, i));
            }
            else
            {
                i++;
                if (c is ',' or ':') spans.Add(new Span(Kind.Punct, start, i));
            }
        }
        return spans;
    }

    private static int SkipString(string t, int i, char quote)
    {
        i++;
        while (i < t.Length)
        {
            if (t[i] == '\\') { i += 2; continue; }
            if (t[i] == quote) return i + 1;
            if (quote == '"' && t[i] == '\n') return i; // JSON strings can't span lines
            i++;
        }
        return t.Length;
    }

    // ---- XML ----------------------------------------------------------------------------

    private static List<Span> LexXml(string t)
    {
        var spans = new List<Span>();
        int i = 0, n = t.Length;
        while (i < n)
        {
            int lt = t.IndexOf('<', i);
            if (lt < 0) break;
            i = lt;
            if (Starts(t, i, "<!--"))
            {
                i = End(t, i + 4, "-->");
                spans.Add(new Span(Kind.Comment, lt, i));
            }
            else if (Starts(t, i, "<![CDATA["))
            {
                i = End(t, i + 9, "]]>");
                spans.Add(new Span(Kind.Meta, lt, lt + 9));
                spans.Add(new Span(Kind.String, lt + 9, Math.Max(lt + 9, i - 3)));
                spans.Add(new Span(Kind.Meta, Math.Max(lt + 9, i - 3), i));
            }
            else if (Starts(t, i, "<?") || Starts(t, i, "<!"))
            {
                i = End(t, i + 2, ">");
                spans.Add(new Span(Kind.Meta, lt, i));
            }
            else
            {
                i = LexTag(t, i, spans);
            }
        }
        return spans;
    }

    /// <summary>Lexes "&lt;name attr="v" …&gt;" or "&lt;/name&gt;" starting at '&lt;'; returns the offset after it.</summary>
    private static int LexTag(string t, int i, List<Span> spans)
    {
        int n = t.Length, start = i;
        i++;
        if (i < n && t[i] == '/') i++;
        spans.Add(new Span(Kind.Punct, start, i));
        int nameStart = i;
        while (i < n && IsNameChar(t[i])) i++;
        if (i > nameStart) spans.Add(new Span(Kind.TagName, nameStart, i));

        while (i < n)
        {
            char c = t[i];
            if (c == '>') { spans.Add(new Span(Kind.Punct, i, i + 1)); return i + 1; }
            if (c == '/' && i + 1 < n && t[i + 1] == '>') { spans.Add(new Span(Kind.Punct, i, i + 2)); return i + 2; }
            if (c == '<') return i; // malformed: let the outer loop restart here
            if (c is '"' or '\'')
            {
                int s = i;
                i = SkipString(t, i, c);
                spans.Add(new Span(Kind.AttrValue, s, i));
            }
            else if (IsNameChar(c))
            {
                int s = i;
                while (i < n && IsNameChar(t[i])) i++;
                spans.Add(new Span(Kind.AttrName, s, i));
            }
            else i++;
        }
        return n;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':';
    private static bool Starts(string t, int i, string s) => string.CompareOrdinal(t, i, s, 0, s.Length) == 0;

    private static int End(string t, int from, string terminator)
    {
        int end = t.IndexOf(terminator, from, StringComparison.Ordinal);
        return end < 0 ? t.Length : end + terminator.Length;
    }

    // ---- folding ------------------------------------------------------------------------

    /// <summary>Recomputes fold markers: JSON objects/arrays spanning lines, XML elements.</summary>
    public static void UpdateFoldings(FoldingManager manager, TextDocument document, ValueKind language)
    {
        switch (language)
        {
            case ValueKind.Json:
                manager.UpdateFoldings(JsonFoldings(document), -1);
                break;
            case ValueKind.Xml:
                new XmlFoldingStrategy().UpdateFoldings(manager, document);
                break;
            default:
                manager.Clear();
                break;
        }
    }

    private static IEnumerable<NewFolding> JsonFoldings(TextDocument document)
    {
        var text = document.Text;
        var result = new List<NewFolding>();
        var stack = new Stack<int>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') { i = SkipString(text, i, '"') - 1; continue; }
            if (c is '{' or '[') stack.Push(i);
            else if ((c is '}' or ']') && stack.Count > 0)
            {
                int open = stack.Pop();
                if (document.GetLineByOffset(open).LineNumber != document.GetLineByOffset(i).LineNumber)
                    result.Add(new NewFolding(open, i + 1) { Name = text[open] == '{' ? "{…}" : "[…]" });
            }
        }
        result.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return result;
    }
}
