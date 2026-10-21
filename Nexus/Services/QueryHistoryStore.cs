using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Nexus.Services;

public sealed class HistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Connection { get; set; } = "";
    public string Sql { get; set; } = "";
    public bool Success { get; set; } = true;
    public double DurationMs { get; set; }

    public string TimestampText => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
    public string SqlPreview
    {
        get
        {
            var s = Sql.Replace('\n', ' ').Replace('\r', ' ').Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Length > 120 ? s[..120] + " …" : s;
        }
    }
}

public sealed class Snippet
{
    public string Name { get; set; } = "";
    public string Sql { get; set; } = "";
}

/// <summary>
/// Persistent query history (last 500 runs) and named snippets at
/// ~/.config/Nexus/history.json / snippets.json.
/// </summary>
public sealed class QueryHistoryStore
{
    public static QueryHistoryStore Instance { get; } = new();

    private const int MaxHistory = 500;
    private static string HistoryFile => Path.Combine(AppPaths.ConfigDir, "history.json");
    private static string SnippetsFile => Path.Combine(AppPaths.ConfigDir, "snippets.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ObservableCollection<HistoryEntry> History { get; }
    public ObservableCollection<Snippet> Snippets { get; }

    private QueryHistoryStore()
    {
        History = new ObservableCollection<HistoryEntry>(LoadFile<HistoryEntry>(HistoryFile));
        Snippets = new ObservableCollection<Snippet>(LoadFile<Snippet>(SnippetsFile));
    }

    public void RecordRun(string connection, string sql, bool success, double durationMs)
    {
        History.Insert(0, new HistoryEntry
        {
            Connection = connection, Sql = sql, Success = success, DurationMs = durationMs
        });
        while (History.Count > MaxHistory)
            History.RemoveAt(History.Count - 1);
        SaveFile(HistoryFile, History);
    }

    public void AddSnippet(string name, string sql)
    {
        var existing = Snippets.Where(s => s.Name == name).ToList();
        foreach (var s in existing) Snippets.Remove(s);
        Snippets.Insert(0, new Snippet { Name = name, Sql = sql });
        SaveFile(SnippetsFile, Snippets);
    }

    public void RemoveSnippet(Snippet snippet)
    {
        Snippets.Remove(snippet);
        SaveFile(SnippetsFile, Snippets);
    }

    public void ClearHistory()
    {
        History.Clear();
        SaveFile(HistoryFile, History);
    }

    private static List<T> LoadFile<T>(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), JsonOptions) ?? new List<T>();
        }
        catch { }
        return new List<T>();
    }

    private static void SaveFile<T>(string path, IEnumerable<T> items)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }
}
