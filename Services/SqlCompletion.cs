using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nexus.Providers;

namespace Nexus.Services;

public enum CompletionKind { Keyword, Function, Schema, Table, View, Column, Alias, Method, Operator, Command }

/// <summary>One suggestion: <see cref="Label"/> is shown and filtered on, <see cref="InsertText"/> is written.</summary>
public sealed record CompletionItem(string Label, string InsertText, CompletionKind Kind, string? Detail, int Rank)
{
    /// <summary>Characters to move the caret back after inserting (e.g. into "find()").</summary>
    public int CaretBack { get; init; }
}

/// <summary>Suggestions for the word that starts at <see cref="ReplaceStart"/> and ends at the caret.</summary>
public sealed record CompletionResult(int ReplaceStart, string Prefix, List<CompletionItem> Items);

/// <summary>
/// Context-aware completion for the query editor. For SQL it tokenizes the current statement,
/// collects the tables it references (with aliases) and decides what fits at the caret: tables
/// after FROM/JOIN/UPDATE/INTO, columns of the referenced tables in SELECT/WHERE/ON/ORDER BY…,
/// a table's columns after "alias.", keywords and functions everywhere else. Mongo gets
/// collections/methods/operators, Redis gets command names.
/// </summary>
public static class SqlCompletion
{
    private sealed record TableRef(string? Container, string Table, string? Alias);

    // Ranks: lower sorts first. Keeps the most likely kind of item at the top for each context.
    private const int RankPrimary = 0, RankSecondary = 1, RankKeyword = 2, RankOther = 3;

    private static readonly HashSet<string> ClauseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "JOIN", "WHERE", "ON", "BY", "HAVING", "SET", "VALUES", "UPDATE", "INTO",
        "RETURNING", "LIMIT", "TABLE", "USING", "WHEN", "THEN", "ELSE", "AND", "OR", "NOT", "CASE", "OVER"
    };

    /// <summary>
    /// Computes suggestions at <paramref name="caret"/>. Returns null when nothing fits (inside a
    /// string/comment, after a number…). May briefly await column introspection of referenced tables.
    /// </summary>
    public static async Task<CompletionResult?> CompleteAsync(string text, int caret, IDatabaseProvider provider,
        SchemaSnapshot? schema, bool explicitInvoke)
    {
        var language = provider.QueryLanguage;
        var tokens = SqlLanguage.Tokenize(text, language);
        if (IsInsideLiteral(text, tokens, caret)) return null;

        int start = caret;
        while (start > 0 && SqlLanguage.IsWordPart(text[start - 1])) start--;
        var prefix = text[start..caret];
        if (prefix.Length > 0 && char.IsDigit(prefix[0])) return null;

        var items = language switch
        {
            "mongo" => await CompleteMongoAsync(text, tokens, start, schema),
            "redis" => CompleteRedis(text, start),
            _ => await CompleteSqlAsync(text, tokens, start, caret, provider, schema, explicitInvoke)
        };
        if (items is null || items.Count == 0) return null;

        // Match the user's casing for keywords: typing "or" offers "order by".
        if (prefix.Length > 0 && prefix.All(ch => !char.IsLetter(ch) || char.IsLower(ch)))
            items = items.Select(i => i.Kind is CompletionKind.Keyword or CompletionKind.Function && IsUpperWord(i.InsertText)
                ? i with { Label = i.Label.ToLowerInvariant(), InsertText = i.InsertText.ToLowerInvariant() }
                : i).ToList();

        var ordered = items
            .GroupBy(i => i.Label, StringComparer.Ordinal).Select(g => g.OrderBy(i => i.Rank).First())
            .OrderBy(i => i.Rank).ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new CompletionResult(start, prefix, ordered);
    }

    private static bool IsUpperWord(string s) => s.Any(char.IsLetter) && !s.Any(char.IsLower);

    private static bool IsInsideLiteral(string text, List<Token> tokens, int caret)
    {
        foreach (var t in tokens)
        {
            if (t.Start >= caret) break;
            if (t.Kind is not (TokenKind.String or TokenKind.Comment or TokenKind.QuotedIdent)) continue;
            if (caret < t.End) return true;
            if (caret == t.End)
            {
                var s = text.Substring(t.Start, t.Length);
                if (t.Kind == TokenKind.Comment && !s.StartsWith("/*")) return true;          // line comment runs to EOL
                if (t.Kind == TokenKind.Comment && !s.EndsWith("*/")) return true;            // unterminated block
                if (t.Kind != TokenKind.Comment && (s.Length < 2 || s[^1] != OpenQuoteToClose(s[0]))) return true;
            }
        }
        return false;
    }

    private static char OpenQuoteToClose(char open) => open == '[' ? ']' : open;

    // ---- SQL -------------------------------------------------------------------------------

    private static async Task<List<CompletionItem>?> CompleteSqlAsync(string text, List<Token> all, int start, int caret,
        IDatabaseProvider provider, SchemaSnapshot? schema, bool explicitInvoke)
    {
        // Tokens of the statement around the caret (comments dropped).
        int stmtFrom = 0, stmtTo = all.Count;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Kind != TokenKind.Semicolon) continue;
            if (all[i].End <= start) stmtFrom = i + 1;
            else if (all[i].Start >= caret) { stmtTo = i; break; }
        }
        var stmt = all.Skip(stmtFrom).Take(stmtTo - stmtFrom).Where(t => t.Kind != TokenKind.Comment).ToList();
        var before = stmt.Where(t => t.End <= start).ToList();

        var keywordWords = SqlLanguage.WordSet(SqlLanguage.Keywords.Concat(provider.DialectKeywords));
        string Txt(Token t) => text.Substring(t.Start, t.Length);
        bool IsIdent(Token t) => t.Kind == TokenKind.QuotedIdent ||
                                 (t.Kind == TokenKind.Word && !keywordWords.Contains(Txt(t)) && !SqlLanguage.IsReserved(Txt(t)));
        string Name(Token t) => SqlLanguage.Unquote(Txt(t));

        var refs = ParseTableRefs(stmt, start, caret, Txt, IsIdent, Name);
        var items = new List<CompletionItem>();

        // ---- after a dot: alias./table. → columns; schema. → objects; schema.table. → columns
        if (before.Count >= 2 && before[^1].Kind == TokenKind.Dot && before[^1].End == start && IsIdentLike(before[^2]))
        {
            var q = Name(before[^2]);
            string? q0 = before.Count >= 4 && before[^3].Kind == TokenKind.Dot && IsIdentLike(before[^4]) ? Name(before[^4]) : null;
            if (schema is null) return null;

            // Alias first ("c."), then a schema/database ("public."), then a table name ("customers.").
            TableRef? target = q0 is not null
                ? new TableRef(q0, q, null)
                : refs.FirstOrDefault(r => Eq(r.Alias, q));
            if (target is null && q0 is null && schema.Objects.ContainsKey(q))
            {
                foreach (var o in schema.Objects[q])
                    items.Add(ObjectItem(provider, o, null, RankPrimary));
                return items;
            }
            target ??= refs.FirstOrDefault(r => r.Alias is null && Eq(r.Table, q))
                       ?? new TableRef(null, q, null); // "customers." without a FROM yet
            await AddColumnsAsync(items, provider, schema, new[] { target }, RankPrimary, qualify: false);
            return items;
        }

        var last = before.Count > 0 ? before[^1] : (Token?)null;
        var clause = before.LastOrDefault(t => t.Kind == TokenKind.Word && ClauseWords.Contains(Txt(t)));
        var clauseWord = before.Count > 0 && clause.Length > 0 ? Txt(clause).ToUpperInvariant() : "";
        var lastWord = last is { Kind: TokenKind.Word } lw ? Txt(lw).ToUpperInvariant() : "";

        // Naming an alias ("AS x"): nothing to suggest.
        if (lastWord == "AS" && !explicitInvoke) return null;

        bool tableMode = SqlLanguage.TableContext.Contains(lastWord)
                         || (last is { Kind: TokenKind.Comma } && clauseWord == "FROM");
        // "FROM customers |" → the user is writing an alias or the next clause: keywords only.
        bool afterTableName = !tableMode && last is { } lt && IsIdent(lt)
                              && clauseWord is "FROM" or "JOIN" or "UPDATE" or "INTO";

        if (tableMode)
        {
            if (schema is not null) AddObjects(items, provider, schema, RankPrimary);
            AddKeywords(items, provider, RankOther);
            return items;
        }

        if (afterTableName)
        {
            AddKeywords(items, provider, RankPrimary);
            return items;
        }

        bool columnMode = clauseWord is "SELECT" or "WHERE" or "ON" or "BY" or "HAVING" or "SET" or "RETURNING"
                              or "AND" or "OR" or "NOT" or "WHEN" or "THEN" or "ELSE" or "CASE" or "USING" or "OVER"
                          || (clauseWord == "INTO" && last is { Kind: TokenKind.OpenParen or TokenKind.Comma });

        if (schema is not null && refs.Count > 0)
            await AddColumnsAsync(items, provider, schema, refs, columnMode ? RankPrimary : RankSecondary, qualify: refs.Count > 1);
        if (columnMode)
            foreach (var r in refs)
                items.Add(new CompletionItem(r.Alias ?? r.Table, provider.Ident(r.Alias ?? r.Table), CompletionKind.Alias,
                    r.Alias is null ? "table" : $"alias of {r.Table}", RankSecondary));

        AddFunctions(items, provider, columnMode ? RankSecondary : RankOther);
        AddKeywords(items, provider, columnMode ? RankKeyword : RankPrimary);
        if (schema is not null && (!columnMode || refs.Count == 0)) AddObjects(items, provider, schema, RankOther);
        return items;
    }

    private static bool IsIdentLike(Token t) => t.Kind is TokenKind.Word or TokenKind.QuotedIdent;

    private static bool Eq(string? a, string? b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tables the statement references: FROM a [AS] x, b y / JOIN c ON … / UPDATE d / INSERT INTO e.
    /// The word being typed (between <paramref name="start"/> and <paramref name="caret"/>) is skipped.
    /// </summary>
    private static List<TableRef> ParseTableRefs(List<Token> stmt, int start, int caret, Func<Token, string> txt,
        Func<Token, bool> isIdent, Func<Token, string> name)
    {
        var refs = new List<TableRef>();
        var tokens = stmt.Where(t => !(t.Start >= start && t.End <= caret && t.Length > 0 && t.Start < caret)).ToList();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Word) continue;
            var word = txt(tokens[i]).ToUpperInvariant();
            if (word is not ("FROM" or "JOIN" or "UPDATE" or "INTO" or "APPLY")) continue;

            int j = i + 1;
            while (true)
            {
                var parts = new List<string>();
                while (j < tokens.Count && (tokens[j].Kind == TokenKind.QuotedIdent || isIdent(tokens[j])))
                {
                    parts.Add(name(tokens[j]));
                    if (j + 2 < tokens.Count && tokens[j + 1].Kind == TokenKind.Dot) j += 2;
                    else { j++; break; }
                }
                if (parts.Count == 0) break;

                string? alias = null;
                if (j < tokens.Count && tokens[j].Kind == TokenKind.Word && txt(tokens[j]).Equals("AS", StringComparison.OrdinalIgnoreCase))
                    j++;
                if (j < tokens.Count && isIdent(tokens[j]))
                    alias = name(tokens[j++]);

                // Keep the last two parts: [db.]schema.table → schema.table.
                var table = parts[^1];
                var container = parts.Count >= 2 ? parts[^2] : null;
                refs.Add(new TableRef(container, table, alias));

                if (word == "FROM" && j < tokens.Count && tokens[j].Kind == TokenKind.Comma) { j++; continue; }
                break;
            }
        }
        return refs;
    }

    private static async Task AddColumnsAsync(List<CompletionItem> items, IDatabaseProvider provider, SchemaSnapshot schema,
        IEnumerable<TableRef> refs, int rank, bool qualify)
    {
        var resolved = refs
            .Select(r => (r, hit: schema.Resolve(r.Container, r.Table)))
            .Where(x => x.hit is not null)
            .Select(x => (x.r, container: x.hit!.Value.container, table: x.hit.Value.obj.Name))
            .ToList();

        // Columns are usually cached already; otherwise wait briefly for introspection.
        var loads = resolved.Select(x => SchemaCache.EnsureColumnsAsync(schema, x.container, x.table)).ToArray();
        if (loads.Length > 0)
            await Task.WhenAny(Task.WhenAll(loads), Task.Delay(1500));

        foreach (var (r, container, table) in resolved)
        {
            var cols = schema.GetColumns(container, table);
            if (cols is null) continue;
            var owner = r.Alias ?? table;
            foreach (var col in cols)
                items.Add(new CompletionItem(col.Name, provider.Ident(col.Name), CompletionKind.Column,
                    qualify ? $"{owner} · {col.DataType}" : col.DataType, rank));
        }
    }

    private static CompletionItem ObjectItem(IDatabaseProvider provider, DbObjectInfo o, string? container, int rank)
    {
        var label = container is null ? o.Name : $"{container}.{o.Name}";
        var insert = container is null ? provider.Ident(o.Name) : $"{provider.Ident(container)}.{provider.Ident(o.Name)}";
        var kind = o.Kind == DbObjectKind.View ? CompletionKind.View : CompletionKind.Table;
        return new CompletionItem(label, insert, kind, o.Kind == DbObjectKind.View ? "view" : "table", rank);
    }

    private static void AddObjects(List<CompletionItem> items, IDatabaseProvider provider, SchemaSnapshot schema, int rank)
    {
        foreach (var (container, objects) in schema.Objects)
        {
            bool isDefault = Eq(container, schema.DefaultContainer);
            foreach (var o in objects.Take(isDefault ? 5000 : 500))
                items.Add(ObjectItem(provider, o, isDefault ? null : container, isDefault ? rank : rank + 1));
        }
        // SQLite has a single implicit container; don't offer "main." noise there.
        if (schema.Containers.Count > 1)
            foreach (var container in schema.Containers)
                items.Add(new CompletionItem(container, provider.Ident(container), CompletionKind.Schema,
                    provider.Capabilities.SupportsSchemas ? "schema" : "database", rank + 1));
    }

    private static void AddKeywords(List<CompletionItem> items, IDatabaseProvider provider, int rank)
    {
        foreach (var k in SqlLanguage.Keywords.Concat(provider.DialectKeywords))
            items.Add(new CompletionItem(k, k, CompletionKind.Keyword, null, rank));
    }

    private static void AddFunctions(List<CompletionItem> items, IDatabaseProvider provider, int rank)
    {
        foreach (var f in SqlLanguage.Functions.Concat(provider.DialectFunctions))
            items.Add(new CompletionItem(f, f + "()", CompletionKind.Function, "function", rank) { CaretBack = 1 });
    }

    // ---- MongoDB ---------------------------------------------------------------------------

    private static async Task<List<CompletionItem>?> CompleteMongoAsync(string text, List<Token> tokens, int start, SchemaSnapshot? schema)
    {
        var before = tokens.Where(t => t.End <= start && t.Kind != TokenKind.Comment).ToList();
        string Txt(Token t) => text.Substring(t.Start, t.Length);
        var items = new List<CompletionItem>();
        var collections = schema is not null && schema.DefaultContainer is not null &&
                          schema.Objects.TryGetValue(schema.DefaultContainer, out var objs) ? objs : new List<DbObjectInfo>();

        if (before.Count >= 2 && before[^1].Kind == TokenKind.Dot && before[^1].End == start && before[^2].Kind == TokenKind.Word)
        {
            // db.|  → collections;  db.coll.|  → methods
            if (Txt(before[^2]) == "db")
            {
                foreach (var c in collections)
                    items.Add(new CompletionItem(c.Name, c.Name, CompletionKind.Table, "collection", RankPrimary));
                return items;
            }
            if (before.Count >= 4 && before[^3].Kind == TokenKind.Dot && Txt(before[^4]) == "db")
            {
                foreach (var m in SqlLanguage.MongoMethods)
                    items.Add(new CompletionItem(m, m + "()", CompletionKind.Method, "method", RankPrimary) { CaretBack = 1 });
                return items;
            }
            return null;
        }

        // Inside the arguments: field names of the collection in use + query operators.
        int depth = before.Count(t => Txt(t) is "{" or "(") - before.Count(t => Txt(t) is "}" or ")");
        if (depth > 0)
        {
            var coll = MongoCollectionInUse(text, before);
            if (coll is not null && schema?.DefaultContainer is { } db)
            {
                await Task.WhenAny(SchemaCache.EnsureColumnsAsync(schema, db, coll), Task.Delay(1500));
                foreach (var col in schema.GetColumns(db, coll) ?? new List<ColumnInfo>())
                    items.Add(new CompletionItem(col.Name, col.Name, CompletionKind.Column, col.DataType, RankPrimary));
            }
            foreach (var op in SqlLanguage.MongoOperators)
                items.Add(new CompletionItem(op, op, CompletionKind.Operator, "operator", RankSecondary));
            return items;
        }

        items.Add(new CompletionItem("db", "db", CompletionKind.Keyword, "current database", RankPrimary));
        return items;
    }

    private static string? MongoCollectionInUse(string text, List<Token> before)
    {
        for (int i = before.Count - 3; i >= 0; i--)
            if (before[i].Kind == TokenKind.Word && text.Substring(before[i].Start, before[i].Length) == "db"
                && before[i + 1].Kind == TokenKind.Dot && before[i + 2].Kind == TokenKind.Word)
                return text.Substring(before[i + 2].Start, before[i + 2].Length);
        return null;
    }

    // ---- Redis -----------------------------------------------------------------------------

    private static List<CompletionItem>? CompleteRedis(string text, int start)
    {
        // Only the command position (first word on the line) has a fixed vocabulary.
        int lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        if (start > 0 && lineStart > start) lineStart = 0;
        if (text[lineStart..start].Trim().Length > 0) return null;
        return SqlLanguage.RedisCommands
            .Select(c => new CompletionItem(c, c, CompletionKind.Command, "command", RankPrimary)).ToList();
    }
}
