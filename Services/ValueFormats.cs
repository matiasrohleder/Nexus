using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Nexus.Models;

namespace Nexus.Services;

/// <summary>What a cell value is, for display and the value viewer.</summary>
public enum ValueKind { Text, Json, Xml }

/// <summary>
/// Recognises, formats and flattens cell values: JSON/XML detection (by engine type name or by
/// content), pretty-printing/compacting for the value viewer, and the one-line form the grids show.
/// </summary>
public static class ValueFormats
{
    /// <summary>Grid cells render at most this many characters (the full value opens in the viewer).</summary>
    public const int DisplayMaxChars = 1000;

    /// <summary>Shown in place of line breaks so multi-line values stay on one grid line.</summary>
    public const string LineBreakMark = " ↵ ";

    /// <summary>Kind implied by an engine type name (json/jsonb/JSON/Object('json'), xml/XMLTYPE).</summary>
    public static ValueKind FromTypeName(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return ValueKind.Text;
        var t = typeName.ToLowerInvariant();
        if (t.Contains("json") && !t.Contains("jsonpath")) return ValueKind.Json;
        if (t.Contains("xml")) return ValueKind.Xml;
        return ValueKind.Text;
    }

    /// <summary>
    /// Content sniffing: a value that starts and ends like a JSON object/array or an XML element
    /// and actually parses. Plain scalars ("42", "\"x\"") stay text on purpose.
    /// </summary>
    public static ValueKind Detect(string? text)
    {
        if (string.IsNullOrEmpty(text)) return ValueKind.Text;
        var span = text.AsSpan().Trim();
        if (span.Length < 2) return ValueKind.Text;
        char first = span[0], last = span[^1];
        if ((first == '{' && last == '}') || (first == '[' && last == ']'))
            return IsJson(text) ? ValueKind.Json : ValueKind.Text;
        if (first == '<' && last == '>')
            return TryParseXml(text, out _) ? ValueKind.Xml : ValueKind.Text;
        return ValueKind.Text;
    }

    /// <summary>
    /// Kind of a whole column: the declared type wins; otherwise every sampled non-null value must
    /// be the same structured kind (one plain-text value means the column is just text).
    /// </summary>
    public static ValueKind DetectColumn(string? typeName, IEnumerable<string?> sample, int maxSamples = 100)
    {
        var declared = FromTypeName(typeName);
        if (declared != ValueKind.Text) return declared;

        ValueKind? kind = null;
        int checkedCount = 0;
        foreach (var value in sample)
        {
            if (value is null) continue;
            if (value.Length > 2_000_000) continue; // don't stall the UI on huge blobs
            var k = Detect(value);
            if (k == ValueKind.Text || (kind is not null && kind != k)) return ValueKind.Text;
            kind = k;
            if (++checkedCount >= maxSamples) break;
        }
        return kind ?? ValueKind.Text;
    }

    public static bool IsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text, JsonOptions);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 512
    };

    /// <summary>Pretty-prints JSON (2-space indent) or XML; returns the input unchanged when it doesn't parse.</summary>
    public static string Pretty(string text, ValueKind kind) => kind switch
    {
        ValueKind.Json => TryFormatJson(text, indented: true, out var json, out _) ? json : text,
        ValueKind.Xml => TryFormatXml(text, indented: true, out var xml, out _) ? xml : text,
        _ => text
    };

    /// <summary>Formats JSON indented or compact. Numbers keep their exact text (no double round-trip).</summary>
    public static bool TryFormatJson(string text, bool indented, out string result, out string? error)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, JsonOptions);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                   {
                       Indented = indented,
                       // Keep non-ASCII and HTML-sensitive characters readable instead of \uXXXX.
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                       MaxDepth = 512
                   }))
                doc.WriteTo(writer);
            result = Encoding.UTF8.GetString(stream.ToArray());
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            result = text;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Formats XML indented or compact. Accepts fragments (several top-level elements).</summary>
    public static bool TryFormatXml(string text, bool indented, out string result, out string? error)
    {
        try
        {
            var options = indented ? SaveOptions.None : SaveOptions.DisableFormatting;
            if (TryParseXmlDocument(text, out var doc, out var docError))
            {
                // XDocument.ToString() drops the declaration; keep it, then every top-level node.
                var parts = doc.Nodes().Select(n => n.ToString(options));
                if (doc.Declaration is not null) parts = parts.Prepend(doc.Declaration.ToString());
                result = string.Join(indented ? "\n" : "", parts);
                error = null;
                return true;
            }

            // Fragment (e.g. PostgreSQL xml "content" values): wrap, format, unwrap.
            var wrapper = XElement.Parse("<nexus-fragment>" + text + "</nexus-fragment>", LoadOptions.PreserveWhitespace);
            if (!wrapper.Elements().Any()) { result = text; error = docError; return false; }
            var parsed = XElement.Parse(wrapper.ToString(), LoadOptions.None);
            result = string.Join(indented ? "\n" : "", parsed.Nodes().Select(n => n.ToString(options)));
            error = null;
            return true;
        }
        catch (XmlException ex)
        {
            result = text;
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseXml(string text, out XNode? node)
    {
        node = null;
        if (TryParseXmlDocument(text, out var doc, out _)) { node = doc; return true; }
        try
        {
            var wrapper = XElement.Parse("<nexus-fragment>" + text + "</nexus-fragment>");
            node = wrapper;
            return wrapper.Elements().Any();
        }
        catch (XmlException) { return false; }
    }

    private static bool TryParseXmlDocument(string text, out XDocument doc, out string? error)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(text.Trim()), settings);
            doc = XDocument.Load(reader, LoadOptions.None);
            error = null;
            return true;
        }
        catch (XmlException ex)
        {
            doc = new XDocument();
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Validation message for text that should be <paramref name="kind"/>; null when valid.</summary>
    public static string? Validate(string text, ValueKind kind) => kind switch
    {
        ValueKind.Json => TryFormatJson(text, false, out _, out var e) ? null : e,
        ValueKind.Xml => TryFormatXml(text, false, out _, out var e) ? null : e,
        _ => null
    };

    /// <summary>
    /// One-line form for grid cells: each line break (plus the indentation after it) becomes
    /// " ↵ ", tabs a space, and the text is cut at <see cref="DisplayMaxChars"/> (the cell trims to
    /// its width with "…" anyway; rendering megabytes of text per cell is what made huge values slow).
    /// </summary>
    public static string ToDisplay(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        bool cut = text.Length > DisplayMaxChars;
        var head = cut ? text[..DisplayMaxChars] : text;
        if (head.AsSpan().IndexOfAny('\r', '\n', '\t') < 0)
            return cut ? head + "…" : head;

        var sb = new StringBuilder(head.Length + 16);
        for (int i = 0; i < head.Length; i++)
        {
            char c = head[i];
            if (c is '\r' or '\n')
            {
                sb.Append(LineBreakMark);
                i = SkipLineBreakAndIndent(head, i) - 1;
            }
            else sb.Append(c == '\t' ? ' ' : c);
        }
        if (cut) sb.Append('…');
        return sb.ToString();
    }

    /// <summary>From a line-break character, the index after the break run and following indentation.</summary>
    private static int SkipLineBreakAndIndent(string text, int i)
    {
        while (i < text.Length && text[i] is '\r' or '\n') i++;
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        return i;
    }

    /// <summary>Character count of <see cref="ToDisplay"/> (up to <paramref name="cap"/>) without building it (column sizing).</summary>
    public static int DisplayLength(string? text, int cap)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int len = 0;
        for (int i = 0; i < text.Length && len < cap; )
        {
            if (text[i] is '\r' or '\n')
            {
                len += LineBreakMark.Length;
                i = SkipLineBreakAndIndent(text, i);
            }
            else { len++; i++; }
        }
        return Math.Min(len, cap);
    }

    /// <summary>True when a value deserves the viewer on double-click: structured, multi-line or long.</summary>
    public static bool IsWorthOpening(string? text, ValueKind columnKind) =>
        text is not null && (columnKind != ValueKind.Text || text.Length > 60 || text.AsSpan().IndexOfAny('\r', '\n') >= 0);

    public static string Label(ValueKind kind) => kind switch
    {
        ValueKind.Json => "JSON",
        ValueKind.Xml => "XML",
        _ => "Text"
    };

    /// <summary>Hover text for a cell: null for short one-liners (fully visible anyway).</summary>
    public static string? Tooltip(string? text)
    {
        if (text is null || text == CellText.NullText) return null;
        if (text.Length <= 40 && text.AsSpan().IndexOfAny('\r', '\n') < 0) return null;
        const int max = 1500;
        return text.Length > max ? text[..max] + "…" : text;
    }
}
