using Avalonia.Threading;
using System.Collections.Concurrent;
using System.Text;

namespace DumpToolbox;

/// <summary>
/// Accepts reports from worker threads without posting every report to Avalonia's UI queue.
/// Pending messages are delivered as one bounded batch on a UI timer.
/// </summary>
internal sealed class UiBatchedLogProgress : IProgress<string>, IDisposable
{
    private const int MaximumMessagesPerTick = 1_000;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly Action<IReadOnlyList<string>> _handler;
    private readonly DispatcherTimer _timer;
    private int _disposed;

    public UiBatchedLogProgress(Action<IReadOnlyList<string>> handler, TimeSpan? interval = null)
    {
        _handler = handler;
        _timer = new DispatcherTimer { Interval = interval ?? TimeSpan.FromMilliseconds(150) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    public void Report(string value)
    {
        if (Volatile.Read(ref _disposed) == 0)
            _pending.Enqueue(value);
    }

    public void Flush()
    {
        if (_pending.IsEmpty)
            return;

        var messages = new List<string>();
        while (messages.Count < MaximumMessagesPerTick && _pending.TryDequeue(out string? message))
            messages.Add(message);

        if (messages.Count > 0)
            _handler(messages);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        while (!_pending.IsEmpty)
            Flush();
    }

    private void Timer_Tick(object? sender, EventArgs e) => Flush();
}

/// <summary>
/// Keeps only the newest worker progress report and applies it periodically on the UI thread.
/// </summary>
internal sealed class UiLatestProgress<T> : IProgress<T>, IDisposable
{
    private readonly object _sync = new();
    private readonly Action<T> _handler;
    private readonly DispatcherTimer _timer;
    private T? _latest;
    private bool _hasValue;
    private int _disposed;

    public UiLatestProgress(Action<T> handler, TimeSpan? interval = null)
    {
        _handler = handler;
        _timer = new DispatcherTimer { Interval = interval ?? TimeSpan.FromMilliseconds(125) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    public void Report(T value)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (_sync)
        {
            _latest = value;
            _hasValue = true;
        }
    }

    public void Flush()
    {
        T? value;
        lock (_sync)
        {
            if (!_hasValue)
                return;
            value = _latest;
            _hasValue = false;
        }

        _handler(value!);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        Flush();
    }

    private void Timer_Tick(object? sender, EventArgs e) => Flush();
}

internal static class UiLogText
{
    private const int MaximumVisibleCharacters = 250_000;

    public static string AppendTimestamped(StringBuilder builder, IReadOnlyList<string> messages)
    {
        foreach (string message in messages)
        {
            if (builder.Length > 0)
                builder.AppendLine();
            builder.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").Append(message);
        }

        if (builder.Length > MaximumVisibleCharacters)
        {
            int excess = builder.Length - MaximumVisibleCharacters;
            int newline = builder.ToString().IndexOf('\n', excess);
            builder.Remove(0, newline >= 0 ? newline + 1 : excess);
        }

        return builder.ToString();
    }
}
