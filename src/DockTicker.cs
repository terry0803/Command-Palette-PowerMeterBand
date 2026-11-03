namespace PowerMeterBand;

/// <summary>
/// One clock for every band, on the whole second, so the Dock's numbers all change at the
/// same moment instead of each band ticking on its own schedule.
/// </summary>
internal static class DockTicker
{
    private static readonly Lock Gate = new();
    private static readonly List<Action> Handlers = [];
    private static readonly Timer Timer = new(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    private static int _ticking;

    public static void Subscribe(Action handler)
    {
        lock (Gate)
        {
            Handlers.Add(handler);
            if (Handlers.Count == 1)
            {
                Timer.Change(1000 - DateTime.Now.Millisecond, 1000);
            }
        }

        handler(); // Show something right away rather than waiting for the next second.
    }

    public static void Unsubscribe(Action handler)
    {
        lock (Gate)
        {
            Handlers.Remove(handler);
            if (Handlers.Count == 0)
            {
                Timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    private static void Tick()
    {
        // If the last round is still running (a slow counter, say), skip this one rather than
        // stack up rounds that all wait on each other.
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            Action[] handlers;
            lock (Gate)
            {
                handlers = [.. Handlers];
            }

            // One band failing mustn't stop the others from updating.
            foreach (var handler in handlers)
            {
                try
                {
                    handler();
                }
                catch
                {
                }
            }
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }
}

/// <summary>
/// Runs an action on a pool thread, one run at a time. Asking never waits: requests that arrive
/// while it runs fold into one more run, which sees the latest state.
/// </summary>
internal sealed class CoalescedWork(Action work)
{
    private int _pending;

    public void Request()
    {
        if (Interlocked.Increment(ref _pending) == 1)
        {
            ThreadPool.QueueUserWorkItem(_ => Drain());
        }
    }

    private void Drain()
    {
        int handled;
        do
        {
            handled = Volatile.Read(ref _pending);
            try
            {
                work();
            }
            catch
            {
                // One failed run (the Dock going away, say) mustn't stop later ones.
            }
        }
        while (Interlocked.Add(ref _pending, -handled) > 0);
    }
}
