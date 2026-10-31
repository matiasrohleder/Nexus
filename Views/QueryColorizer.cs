using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Nexus.Providers;
using Nexus.Services;

namespace Nexus.Views;

/// <summary>
/// Syntax + schema-aware highlighting for query tabs, with VS Code "Dark+" colors: keywords blue,
/// functions yellow, strings orange, numbers green, comments dim green, and (from the
/// <see cref="SchemaSnapshot"/>) tables/views teal and columns light blue. Tokenizes the whole
/// document once per edit with <see cref="SqlLanguage.Tokenize"/> so multi-line comments and
/// strings stay correct, then paints each visible line from the cached tokens.
/// </summary>
public sealed class QueryColorizer : DocumentColorizingTransformer
{
    private static readonly IBrush Keyword = Brush("#569CD6");
    private static readonly IBrush Function = Brush("#DCDCAA");
    private static readonly IBrush StringBrush = Brush("#CE9178");
    private static readonly IBrush Number = Brush("#B5CEA8");
    private static readonly IBrush Comment = Brush("#6A9955");
    private static readonly IBrush TableBrush = Brush("#4EC9B0");
    private static readonly IBrush ColumnBrush = Brush("#9CDCFE");
    private static readonly IBrush Variable = Brush("#C586C0");
    private static readonly IBrush Operator = Brush("#D4D4D4");

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();

    private string _language = "sql";
    private HashSet<string> _keywords = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _functions = new(StringComparer.OrdinalIgnoreCase);
    private List<Token>? _tokens;
    private string _text = "";

    /// <summary>Schema the names are checked against; null = no table/column coloring.</summary>
    public SchemaSnapshot? Schema { get; set; }

    /// <summary>Switches dialect/language (called when the tab's connection changes).</summary>
    public void Configure(IDatabaseProvider? provider)
    {
        _language = provider?.QueryLanguage ?? "sql";
        _keywords = _language switch
        {
            "mongo" => new HashSet<string>(new[] { "db", "true", "false", "null", "new", "ObjectId", "ISODate", "Date" }, StringComparer.Ordinal),
            "redis" => new HashSet<string>(SqlLanguage.RedisCommands, StringComparer.OrdinalIgnoreCase),
            _ => SqlLanguage.WordSet(SqlLanguage.Keywords.Concat(provider?.DialectKeywords ?? Array.Empty<string>()))
        };
        _functions = _language switch
        {
            "mongo" => new HashSet<string>(SqlLanguage.MongoMethods, StringComparer.Ordinal),
            "redis" => new HashSet<string>(),
            _ => new HashSet<string>(SqlLanguage.Functions.Concat(provider?.DialectFunctions ?? Array.Empty<string>()),
                StringComparer.OrdinalIgnoreCase)
        };
        _tokens = null;
    }

    /// <summary>Marks the cached tokens stale (call on every text change).</summary>
    public void Invalidate() => _tokens = null;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_tokens is null)
        {
            _text = CurrentContext.Document.Text;
            _tokens = SqlLanguage.Tokenize(_text, _language);
        }
        var tokens = _tokens;
        int lineStart = line.Offset, lineEnd = line.EndOffset;

        for (int i = SqlLanguage.FirstTokenEndingAfter(tokens, lineStart); i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Start >= lineEnd) break;
            var brush = BrushFor(tokens, i);
            if (brush is null) continue;

            int from = Math.Max(t.Start, lineStart), to = Math.Min(t.End, lineEnd);
            if (to <= from) continue;
            bool italic = t.Kind == TokenKind.Comment;
            ChangeLinePart(from, to, el =>
            {
                el.TextRunProperties.SetForegroundBrush(brush);
                if (italic)
                    el.TextRunProperties.SetTypeface(new Typeface(el.TextRunProperties.Typeface.FontFamily, FontStyle.Italic));
            });
        }
    }

    private IBrush? BrushFor(List<Token> tokens, int i)
    {
        var t = tokens[i];
        switch (t.Kind)
        {
            case TokenKind.Comment: return Comment;
            case TokenKind.String: return StringBrush;
            case TokenKind.Number: return Number;
            case TokenKind.Variable: return Variable;
            case TokenKind.Operator: return Operator;
            case TokenKind.QuotedIdent: return NameBrush(SqlLanguage.Unquote(Text(t))) ?? ColumnBrush;
            case TokenKind.Word: break;
            default: return null;
        }

        var word = Text(t);
        bool calledAsFunction = NextIs(tokens, i, TokenKind.OpenParen);
        bool afterDot = i > 0 && tokens[i - 1].Kind == TokenKind.Dot;

        if (_language == "mongo")
        {
            if (word.StartsWith('$')) return Keyword;
            if (_keywords.Contains(word)) return Keyword;
            if (afterDot && calledAsFunction) return Function;
            if (afterDot && i >= 2 && Text(tokens[i - 2]) == "db") return TableBrush;
            // { field: … } keys
            if (i + 1 < tokens.Count && Text(tokens[i + 1]) == ":") return ColumnBrush;
            return null;
        }

        if (_language == "redis")
        {
            bool firstOnLine = i == 0 || _text.IndexOf('\n', tokens[i - 1].End, t.Start - tokens[i - 1].End) >= 0;
            return firstOnLine && _keywords.Contains(word) ? Keyword : null;
        }

        if (calledAsFunction && _functions.Contains(word)) return Function;
        // After "x." it's a member: column or table, never a keyword ("t.name", "s.order").
        if (!afterDot && _keywords.Contains(word)) return Keyword;
        if (_functions.Contains(word)) return Function;
        return NameBrush(word);
    }

    private IBrush? NameBrush(string name)
    {
        var schema = Schema;
        if (schema is null) return null;
        if (schema.ObjectNames.Contains(name) || schema.Objects.ContainsKey(name)) return TableBrush;
        if (schema.ColumnNames.Contains(name)) return ColumnBrush;
        return null;
    }

    private bool NextIs(List<Token> tokens, int i, TokenKind kind) =>
        i + 1 < tokens.Count && tokens[i + 1].Kind == kind;

    private string Text(Token t) => _text.Substring(t.Start, t.Length);
}
