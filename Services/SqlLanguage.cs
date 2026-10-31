using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Services;

public enum TokenKind { Word, QuotedIdent, String, Number, Comment, Variable, Operator, Dot, Comma, Semicolon, OpenParen, CloseParen }

public readonly record struct Token(TokenKind Kind, int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// Language data and a small, forgiving lexer shared by the query editor's highlighter and its
/// completion engine. "sql" covers every SQL dialect (dialect extras come from the provider);
/// "mongo" lexes mongosh-style JavaScript; "redis" lexes raw commands. The lexer never fails:
/// unterminated strings/comments simply run to the end of the text.
/// </summary>
public static class SqlLanguage
{
    /// <summary>Common keywords (multi-word phrases complete as a unit: "OR" → "ORDER BY").</summary>
    public static readonly string[] Keywords =
    {
        "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET", "DISTINCT",
        "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "MERGE", "RETURNING",
        "CREATE TABLE", "CREATE VIEW", "CREATE INDEX", "CREATE UNIQUE INDEX", "CREATE SCHEMA", "CREATE DATABASE",
        "ALTER TABLE", "ADD COLUMN", "DROP COLUMN", "ALTER COLUMN", "RENAME TO", "DROP TABLE", "DROP VIEW",
        "DROP INDEX", "TRUNCATE TABLE", "IF EXISTS", "IF NOT EXISTS",
        "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "FULL OUTER JOIN", "LEFT OUTER JOIN",
        "CROSS JOIN", "NATURAL JOIN", "ON", "USING", "AS", "AND", "OR", "NOT", "NULL", "IS NULL", "IS NOT NULL",
        "IN", "NOT IN", "BETWEEN", "LIKE", "NOT LIKE", "EXISTS", "NOT EXISTS", "ANY", "ALL", "SOME",
        "UNION", "UNION ALL", "INTERSECT", "EXCEPT", "CASE", "WHEN", "THEN", "ELSE", "END", "ASC", "DESC",
        "WITH", "RECURSIVE", "OVER", "PARTITION BY", "ROWS BETWEEN", "UNBOUNDED PRECEDING", "CURRENT ROW",
        "PRIMARY KEY", "FOREIGN KEY", "REFERENCES", "UNIQUE", "CHECK", "DEFAULT", "CONSTRAINT", "NOT NULL",
        "INDEX", "VIEW", "TABLE", "COLUMN", "SCHEMA", "DATABASE", "CASCADE", "RESTRICT",
        "BEGIN", "COMMIT", "ROLLBACK", "TRANSACTION", "SAVEPOINT", "GRANT", "REVOKE", "TO",
        "EXPLAIN", "TRUE", "FALSE", "INTEGER", "INT", "BIGINT", "SMALLINT", "DECIMAL", "NUMERIC", "FLOAT",
        "DOUBLE PRECISION", "VARCHAR", "CHAR", "TEXT", "DATE", "TIME", "TIMESTAMP", "INTERVAL", "BOOLEAN"
    };

    public static readonly string[] Functions =
    {
        "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "NULLIF", "CAST", "UPPER", "LOWER", "TRIM", "LTRIM",
        "RTRIM", "LENGTH", "SUBSTRING", "REPLACE", "CONCAT", "ROUND", "FLOOR", "CEIL", "ABS", "MOD", "POWER",
        "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "EXTRACT", "ROW_NUMBER", "RANK", "DENSE_RANK",
        "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "NTILE", "GREATEST", "LEAST"
    };

    /// <summary>Words that are unsafe as bare identifiers in the common dialects (generated SQL quotes them).</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALL", "ALTER", "AND", "ANY", "ARRAY", "AS", "ASC", "AUTHORIZATION", "BETWEEN", "BINARY", "BOTH", "BY",
        "CASE", "CAST", "CHECK", "COLLATE", "COLUMN", "CONSTRAINT", "CREATE", "CROSS", "CURRENT_DATE",
        "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "DATABASE", "DEFAULT", "DELETE", "DESC", "DISTINCT",
        "DO", "DROP", "ELSE", "END", "EXCEPT", "EXISTS", "FALSE", "FETCH", "FOR", "FOREIGN", "FROM", "FULL",
        "GRANT", "GROUP", "HAVING", "IN", "INDEX", "INNER", "INSERT", "INTERSECT", "INTO", "IS", "JOIN", "KEY",
        "LEADING", "LEFT", "LIKE", "LIMIT", "NATURAL", "NOT", "NULL", "OFFSET", "ON", "ONLY", "OR", "ORDER",
        "OUTER", "PRIMARY", "REFERENCES", "RIGHT", "ROW", "ROWS", "SELECT", "SESSION_USER", "SET", "SOME",
        "TABLE", "THEN", "TO", "TOP", "TRAILING", "TRUE", "UNION", "UNIQUE", "UPDATE", "USER", "USING", "VALUES",
        "VIEW", "WHEN", "WHERE", "WINDOW", "WITH"
    };

    public static bool IsReserved(string word) => Reserved.Contains(word);

    /// <summary>Clause keywords after which a table name is expected.</summary>
    public static readonly HashSet<string> TableContext = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "JOIN", "UPDATE", "INTO", "TABLE", "APPLY"
    };

    /// <summary>Mongo shell: collection methods Nexus understands, plus common read helpers.</summary>
    public static readonly string[] MongoMethods =
    {
        "find", "aggregate", "countDocuments", "count", "insertOne", "insertMany", "deleteMany", "updateMany"
    };

    public static readonly string[] MongoOperators =
    {
        "$eq", "$ne", "$gt", "$gte", "$lt", "$lte", "$in", "$nin", "$and", "$or", "$not", "$nor", "$exists",
        "$regex", "$elemMatch", "$size", "$set", "$unset", "$inc", "$push", "$pull", "$match", "$group",
        "$project", "$sort", "$limit", "$skip", "$lookup", "$unwind", "$count", "$sum", "$avg", "$min", "$max"
    };

    public static readonly string[] RedisCommands =
    {
        "INSPECT", "GET", "SET", "DEL", "EXISTS", "EXPIRE", "TTL", "PERSIST", "TYPE", "KEYS", "SCAN", "RENAME",
        "INCR", "INCRBY", "DECR", "MGET", "MSET", "APPEND", "STRLEN", "GETSET", "SETEX", "SETNX",
        "HGET", "HSET", "HGETALL", "HDEL", "HKEYS", "HVALS", "HLEN", "HINCRBY", "HEXISTS",
        "LPUSH", "RPUSH", "LPOP", "RPOP", "LRANGE", "LLEN", "LINDEX", "LREM",
        "SADD", "SREM", "SMEMBERS", "SCARD", "SISMEMBER", "SINTER", "SUNION",
        "ZADD", "ZREM", "ZRANGE", "ZREVRANGE", "ZRANGEBYSCORE", "ZSCORE", "ZCARD", "ZINCRBY",
        "XADD", "XRANGE", "XLEN", "PUBLISH", "INFO", "DBSIZE", "PING", "SELECT", "FLUSHDB", "CONFIG", "CLIENT"
    };

    public static bool IsWordStart(char ch) => char.IsLetter(ch) || ch == '_';
    public static bool IsWordPart(char ch) => char.IsLetterOrDigit(ch) || ch is '_' or '$' or '#';

    /// <summary>Tokenizes <paramref name="text"/>; whitespace is skipped (gaps between tokens).</summary>
    public static List<Token> Tokenize(string text, string language)
    {
        var tokens = new List<Token>(text.Length / 4);
        bool sql = language == "sql", mongo = language == "mongo";
        int i = 0, n = text.Length;

        while (i < n)
        {
            char ch = text[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            int start = i;
            char next = i + 1 < n ? text[i + 1] : '\0';

            // Comments: "--" (SQL), "//" (Mongo), "/* */" (both), "#" lines don't exist in Redis/SQL here.
            if ((sql && ch == '-' && next == '-') || (mongo && ch == '/' && next == '/'))
            {
                while (i < n && text[i] != '\n') i++;
                tokens.Add(new Token(TokenKind.Comment, start, i - start));
                continue;
            }
            if (!(language == "redis") && ch == '/' && next == '*')
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? n : close + 2;
                tokens.Add(new Token(TokenKind.Comment, start, i - start));
                continue;
            }

            // Strings: '…' everywhere ('' escapes in SQL, \ escapes in Mongo/Redis); "…" is a string outside SQL.
            if (ch == '\'' || (!sql && (ch == '"' || ch == '`')))
            {
                i = ScanQuoted(text, i, ch, backslashEscapes: !sql);
                tokens.Add(new Token(TokenKind.String, start, i - start));
                continue;
            }

            if (sql)
            {
                // Quoted identifiers: "x" (ANSI), `x` (MySQL/ClickHouse), [x] (SQL Server).
                if (ch == '"' || ch == '`')
                {
                    i = ScanQuoted(text, i, ch, backslashEscapes: false);
                    tokens.Add(new Token(TokenKind.QuotedIdent, start, i - start));
                    continue;
                }
                if (ch == '[' && i + 1 < n && IsWordStart(next))
                {
                    int close = text.IndexOf(']', i + 1);
                    int eol = text.IndexOf('\n', i + 1);
                    if (close > 0 && (eol < 0 || close < eol))
                    {
                        i = close + 1;
                        tokens.Add(new Token(TokenKind.QuotedIdent, start, i - start));
                        continue;
                    }
                }
                // PostgreSQL dollar quoting: $$…$$ / $tag$…$tag$.
                if (ch == '$' && (next == '$' || IsWordStart(next)))
                {
                    int tagEnd = text.IndexOf('$', i + 1);
                    if (tagEnd > 0 && text.AsSpan(i + 1, tagEnd - i - 1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789") < 0)
                    {
                        var tag = text.Substring(i, tagEnd - i + 1);
                        int close = text.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
                        i = close < 0 ? n : close + tag.Length;
                        tokens.Add(new Token(TokenKind.String, start, i - start));
                        continue;
                    }
                }
                // Parameters / variables: @p, @@version, :name, $1, ?
                if ((ch == '@' || (ch == ':' && IsWordStart(next)) || (ch == '$' && char.IsDigit(next))))
                {
                    i++;
                    while (i < n && (IsWordPart(text[i]) || text[i] == '@')) i++;
                    tokens.Add(new Token(TokenKind.Variable, start, i - start));
                    continue;
                }
            }

            if (char.IsDigit(ch) || (ch == '.' && char.IsDigit(next)))
            {
                i++;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '.' ||
                                 ((text[i] == '+' || text[i] == '-') && (text[i - 1] == 'e' || text[i - 1] == 'E'))))
                    i++;
                tokens.Add(new Token(TokenKind.Number, start, i - start));
                continue;
            }

            if (IsWordStart(ch) || ch == '#' || (mongo && ch == '$'))
            {
                i++;
                while (i < n && IsWordPart(text[i])) i++;
                tokens.Add(new Token(TokenKind.Word, start, i - start));
                continue;
            }

            var kind = ch switch
            {
                '.' => TokenKind.Dot,
                ',' => TokenKind.Comma,
                ';' => TokenKind.Semicolon,
                '(' => TokenKind.OpenParen,
                ')' => TokenKind.CloseParen,
                _ => TokenKind.Operator
            };
            tokens.Add(new Token(kind, i, 1));
            i++;
        }
        return tokens;
    }

    private static int ScanQuoted(string text, int i, char quote, bool backslashEscapes)
    {
        int n = text.Length;
        i++;
        while (i < n)
        {
            char ch = text[i];
            if (backslashEscapes && ch == '\\') { i += 2; continue; }
            if (ch == quote)
            {
                if (!backslashEscapes && i + 1 < n && text[i + 1] == quote) { i += 2; continue; } // '' / "" escape
                return i + 1;
            }
            i++;
        }
        return n;
    }

    /// <summary>Index of the token containing or ending at <paramref name="offset"/>, or -1.</summary>
    public static int TokenAt(List<Token> tokens, int offset)
    {
        int lo = 0, hi = tokens.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var t = tokens[mid];
            if (offset < t.Start) hi = mid - 1;
            else if (offset > t.End) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    /// <summary>Index of the first token that ends after <paramref name="offset"/> (binary search).</summary>
    public static int FirstTokenEndingAfter(List<Token> tokens, int offset)
    {
        int lo = 0, hi = tokens.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (tokens[mid].End <= offset) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Splits multi-word keywords into their single words (for highlighting).</summary>
    public static HashSet<string> WordSet(IEnumerable<string> phrases) =>
        new(phrases.SelectMany(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries)), StringComparer.OrdinalIgnoreCase);

    /// <summary>Strips the quotes of a quoted identifier token's text.</summary>
    public static string Unquote(string ident) =>
        ident.Length >= 2 && ident[0] is '"' or '`' or '['
            ? ident[1..^1].Replace("\"\"", "\"").Replace("``", "`")
            : ident;
}
