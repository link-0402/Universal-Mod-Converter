using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;

namespace UniversalModConverter.Session;

/// <summary>
/// Runs one long operation (scan, preview, apply, revert) at a time off the framework thread.
/// Completions and posted actions run on the framework thread when <see cref="Drain"/> is
/// called from the window's Draw, so they may touch UI state and Penumbra IPC freely.
/// </summary>
public sealed class BackgroundRunner
{
    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly Stopwatch _elapsed = new();

    /// <summary>Label of the running operation, e.g. "Planning…"; null when idle.</summary>
    public string? CurrentLabel { get; private set; }

    public bool IsBusy => CurrentLabel != null;

    public TimeSpan Elapsed => _elapsed.Elapsed;

    /// <summary>
    /// Starts <paramref name="work"/> unless another operation is running. Exactly one of
    /// <paramref name="onDone"/> or <paramref name="onError"/> runs on the framework thread.
    /// </summary>
    public bool TryRun<T>(string label, Func<T> work, Action<T> onDone, Action<Exception> onError)
    {
        if (IsBusy) return false;
        CurrentLabel = label;
        _elapsed.Restart();
        Task.Run(() =>
        {
            try
            {
                var result = work();
                _pending.Enqueue(() => { Finish(); onDone(result); });
            }
            catch (Exception ex)
            {
                _pending.Enqueue(() => { Finish(); onError(ex); });
            }
        });
        return true;
    }

    /// <summary>Queues <paramref name="action"/> for the framework thread. Thread-safe.</summary>
    public void Post(Action action) => _pending.Enqueue(action);

    /// <summary>Runs everything queued so far. Call once per frame from Draw.</summary>
    public void Drain()
    {
        while (_pending.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "[UMC] Background completion failed");
            }
        }
    }

    private void Finish()
    {
        CurrentLabel = null;
        _elapsed.Stop();
    }
}
