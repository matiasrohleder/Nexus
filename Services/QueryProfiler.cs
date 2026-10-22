using System.Collections.ObjectModel;
using Avalonia.Threading;
using Nexus.Models;

namespace Nexus.Services;

/// <summary>
/// Central profiler that records every query Nexus executes, plus queries observed on the
/// server via pg_stat_activity. It is a process-wide singleton so all editors and connections
/// feed the same timeline.
/// </summary>
public sealed class QueryProfiler
{
    public static QueryProfiler Instance { get; } = new();

    private const int MaxEntries = 5000;

    /// <summary>Newest-first list of profiled queries, safe to bind to a DataGrid.</summary>
    public ObservableCollection<QueryLogEntry> Entries { get; } = new();

    /// <summary>Off by default — profiling is opt-in from the Profiler panel.</summary>
    public bool IsCapturing { get; set; }

    /// <summary>Test hook: add entries synchronously (no UI dispatcher in headless self-test).</summary>
    public bool DirectDispatch { get; set; }

    private readonly object _gate = new();

    private QueryProfiler() { }

    public void Log(QueryLogEntry entry)
    {
        if (!IsCapturing) return;

        if (DirectDispatch)
        {
            lock (_gate) Add(entry);
            return;
        }

        // Marshal to the UI thread because queries execute on background threads.
        if (Dispatcher.UIThread.CheckAccess())
            Add(entry);
        else
            Dispatcher.UIThread.Post(() => Add(entry));
    }

    private void Add(QueryLogEntry entry)
    {
        Entries.Insert(0, entry);
        while (Entries.Count > MaxEntries)
            Entries.RemoveAt(Entries.Count - 1);
    }

    public void Clear()
    {
        if (Dispatcher.UIThread.CheckAccess())
            Entries.Clear();
        else
            Dispatcher.UIThread.Post(() => Entries.Clear());
    }
}
