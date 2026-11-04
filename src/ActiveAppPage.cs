using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerMeterBand;

/// <summary>
/// What clicking the app name offers, like a Mac's app menu: how much the app uses,
/// and opening, hiding or quitting it. Its windows live in <see cref="WindowMenuPage"/>.
/// </summary>
internal sealed partial class ActiveAppPage : ListPage
{
    // While the menu is open its usage line is measured again every 2 seconds. The extension isn't told when
    // the menu closes, so each refresh asks the Dock for the list again: once it stops asking for 5 seconds
    // (the menu is gone), measuring stops until the menu is opened again. (Checked: the Dock asks only
    // while the menu is shown, and not for app switches while it's closed.)
    private const int RefreshMs = 2000;
    private const long ClosedAfterMs = 5000;

    private readonly ForegroundAppTracker _tracker;
    private readonly Lock _lock = new();

    // The items shown for _itemsApp with these windows, kept so a refresh only changes the usage line's
    // text instead of handing the Dock a new list every time.
    private ActiveApp? _itemsApp;
    private List<IntPtr> _itemsWindows = [];
    private IListItem[] _items = [];
    private ListItem? _usageItem;

    private AppUsage.Sample _usage;
    private double? _cpuPercent;
    private Timer? _refresh;
    private int _refreshGeneration;
    private long _lastAsked;

    public ActiveAppPage(ForegroundAppTracker tracker)
    {
        _tracker = tracker;
        Id = "com.terry.powermeter.activeapp.page";
        Name = "開啟";

        // No icon, like the app name on a Mac: the Dock otherwise falls back to the page's icon.
        Icon = Glyphs.None;
        Title = tracker.Current.Name;
    }

    public void Refresh()
    {
        Title = _tracker.Current.Name;
        RaiseItemsChanged();
    }

    public override IListItem[] GetItems()
    {
        var app = _tracker.Current;
        if (app.IsDesktop)
        {
            return [Item("開啟檔案總管", 0xEC50, () => Start("explorer.exe"))]; // FileExplorer
        }

        // A Store app still starting: its frame's process hosts every Store app, so offer nothing yet.
        if (app.IsFrameOnly)
        {
            return [];
        }

        var windows = AppWindows.Of(app);
        lock (_lock)
        {
            var now = Environment.TickCount64;
            _lastAsked = now;

            // Opened afresh (not a refresh's own request): measure from now, CPU follows a second later.
            if (_refresh is null || _itemsApp != app)
            {
                _usage = AppUsage.Measure(app);
                _cpuPercent = null;

                // A disposed timer can still run a callback already queued, so each timer gets a number and
                // a callback from an older one only leaves: it must not stop (or update) the newer one.
                _refresh?.Dispose();
                var generation = ++_refreshGeneration;
                _refresh = new Timer(_ => RefreshUsage(app, generation), null, 1000, RefreshMs);
            }

            if (_itemsApp != app || !_itemsWindows.SequenceEqual(windows))
            {
                _itemsApp = app;
                _itemsWindows = windows;
                _usageItem = new ListItem(new NoOpCommand()) { Icon = Glyphs.Of(0xE9D9) }; // Diagnostic, the same icon as the built-in CPU band
                _items = Build(app, windows, _usageItem);
            }

            _usageItem!.Title = UsageTitle(windows.Count);
            return _items;
        }
    }

    // Runs on a pool thread, where an exception would take the whole extension (every Dock band) down:
    // a failed read, or the Dock gone while telling it, stops this refresh instead.
    private void RefreshUsage(ActiveApp app, int generation)
    {
        try
        {
            lock (_lock)
            {
                if (generation != _refreshGeneration)
                {
                    return;
                }

                if (_itemsApp != app || _tracker.Current != app || Environment.TickCount64 - _lastAsked > ClosedAfterMs)
                {
                    StopRefresh();
                    return;
                }

                var sample = AppUsage.Measure(app);
                _cpuPercent = AppUsage.CpuPercent(_usage, sample);
                _usage = sample;
                _usageItem!.Title = UsageTitle(_itemsWindows.Count);
            }

            // Also how the menu shows it's still open: the Dock asks for the list again only while it's shown.
            RaiseItemsChanged();
        }
        catch
        {
            lock (_lock)
            {
                if (generation == _refreshGeneration)
                {
                    StopRefresh();
                }
            }
        }
    }

    // When the extension closes, rather than waiting for the refresh to notice the menu has gone.
    public void StopRefreshing()
    {
        lock (_lock)
        {
            StopRefresh();
        }
    }

    // Called with the lock held, only for the current timer.
    private void StopRefresh()
    {
        _refresh?.Dispose();
        _refresh = null;
        _refreshGeneration++;
    }

    private string UsageTitle(int windowCount)
    {
        var cpu = _cpuPercent is double value ? $"CPU {value:0}%" : "CPU 計算中⋯";
        return $"記憶體 {AppUsage.FormatBytes(_usage.PrivateBytes)} · {cpu} · {windowCount} 個視窗";
    }

    private IListItem[] Build(ActiveApp app, List<IntPtr> windows, ListItem usage)
    {
        var items = new List<IListItem>
        {
            usage,
            Item("開新視窗", 0xE78B, () => AppWindows.OpenNew(app)), // NewWindow
            // Only while the app is still there: had it quit in the meantime, "others" would be everything left.
            Item("隱藏其他 App", 0xED1A, () =>
            {
                if (AppWindows.StillBelongsTo(app.Window, app) || AppWindows.StillOf(app, windows).Any())
                {
                    AppWindows.Hide(_tracker.OtherAppWindows(app));
                }
            }), // Hide
            Item($"隱藏 {app.Name}", 0xE921, () => AppWindows.Hide(AppWindows.StillOf(app, windows))), // ChromeMinimize
            Item($"結束 {app.Name}", 0xE711, () => AppWindows.Quit(AppWindows.StillOf(app, windows))), // Cancel
        };

        // Force-quitting Explorer would take the taskbar and desktop down with it. The process's start
        // time goes along, so a confirmation answered after the app has gone can't hit a new process
        // that got the same ID.
        if (!AppWindows.IsExplorer(app) && AppWindows.StartTime(app.ProcessId) is DateTime started)
        {
            items.Add(new ListItem(DockCommands.Confirm($"強制結束 {app.Name}", $"要強制結束 {app.Name} 嗎？沒有儲存的內容會遺失。", () => AppWindows.ForceQuit(app.ProcessId, started)))
            {
                Title = $"強制結束 {app.Name}",
                Subtitle = "沒有回應時使用",
                Icon = Glyphs.Of(0xE7BA), // Warning
            });
        }

        return [.. items];
    }

    private static ListItem Item(string title, int glyph, Action action) =>
        new(DockCommands.AfterDismiss(action)) { Title = title, Icon = Glyphs.Of(glyph) };

    private static void Start(string file) => Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
}

/// <summary>
/// Memory and CPU used by all of an app's processes (browsers and Electron apps run several).
/// </summary>
internal static class AppUsage
{
    // Memory is skipped for the second, CPU-only sample (a browser has dozens of processes to read).
    public static Sample Measure(ActiveApp app, bool memory = true)
    {
        // Taken before the processes are listed: one that starts while they are counts as new.
        var takenAt = DateTime.UtcNow;
        var cpu = new Dictionary<int, (DateTime Started, TimeSpan Time)>();
        if (Native.ProcessPath(app.ProcessId) is not string path)
        {
            return new(cpu, 0, Stopwatch.GetTimestamp(), takenAt);
        }

        // This copy of the app: its own process and the ones it started (a browser's tabs, an Electron app's
        // helpers), not another copy started separately (Excel with /x), whose windows aren't counted either.
        var family = Descendants(app.ProcessId);
        var bytes = 0L;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            using (process)
            {
                // Same program, in this sign-in session only: another signed-in user's copy isn't this app.
                if (process.Id != app.ProcessId
                    && (!family.Contains(process.Id) || SessionOf(process) != CurrentSession || Native.ProcessPath((uint)process.Id) != path))
                {
                    continue;
                }

                try
                {
                    cpu[process.Id] = (process.StartTime.ToUniversalTime(), process.TotalProcessorTime);
                }
                catch
                {
                }

                if (memory)
                {
                    bytes += Native.PrivateWorkingSet((uint)process.Id) ?? process.WorkingSet64;
                }
            }
        }

        return new(cpu, bytes, Stopwatch.GetTimestamp(), takenAt);
    }

    private static readonly Dictionary<(uint Root, DateTime? Started), HashSet<(int Pid, DateTime? Started)>> KnownMembers = [];
    private static readonly LinkedList<(uint Root, DateTime? Started)> KnownOrder = new();

    // A process and everything it started, through any depth, from one snapshot of all processes. A parent
    // ID can be reused after the parent exits, so a child must have started after its parent.
    private static HashSet<int> Descendants(uint root)
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return [(int)root];
        }

        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var ok = Process32First(snapshot, ref entry); ok; ok = Process32Next(snapshot, ref entry))
            {
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var family = new HashSet<int> { (int)root };
        var started = new Dictionary<int, DateTime?>();
        DateTime? Started(int pid)
        {
            if (!started.TryGetValue(pid, out var time))
            {
                started[pid] = time = AppWindows.StartTime((uint)pid);
            }

            return time;
        }

        // Members found before stay members while they run (same ID and start time), even once the process
        // in between that linked them to the app has exited and broken the chain.
        var key = (root, Started((int)root));
        lock (KnownMembers)
        {
            if (KnownMembers.TryGetValue(key, out var known))
            {
                foreach (var (pid, start) in known)
                {
                    if (parents.ContainsKey(pid) && Started(pid) == start)
                    {
                        family.Add(pid);
                    }
                }
            }
        }

        // Grow the family until no process joins: one whose parent is in it, and that started after it.
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var (pid, parent) in parents)
            {
                if (!family.Contains(pid) && family.Contains(parent) && Started(pid) >= Started(parent))
                {
                    family.Add(pid);
                    grew = true;
                }
            }
        }

        lock (KnownMembers)
        {
            // The last few apps looked at are remembered (going back to one keeps its family); older ones
            // and members that have exited are dropped, so this stays small.
            KnownMembers.Remove(key);
            KnownMembers[key] = [.. family.Select(pid => (pid, Started(pid)))];
            KnownOrder.Remove(key);
            KnownOrder.AddLast(key);
            while (KnownOrder.Count > 8)
            {
                KnownMembers.Remove(KnownOrder.First!.Value);
                KnownOrder.RemoveFirst();
            }
        }

        return family;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    // PROCESSENTRY32W.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    // The foreground app always runs in the user's own session, the same as the extension.
    private static readonly int CurrentSession = Process.GetCurrentProcess().SessionId;

    private static int SessionOf(Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch
        {
            return -1;
        }
    }

    // Share of the whole CPU, like Task Manager. Each process is compared with itself (its ID and start
    // time, since IDs get reused): a browser tab's process that exits mid-way must not drag the total
    // down. One missing from the first sample counts in full only if it started after it; one that was
    // already running but couldn't be read then has no baseline, so it's left out rather than counted
    // with its whole lifetime's CPU time.
    public static double CpuPercent(Sample start, Sample end)
    {
        var used = 0.0;
        foreach (var (pid, now) in end.Cpu)
        {
            TimeSpan before;
            if (start.Cpu.TryGetValue(pid, out var then) && then.Started == now.Started)
            {
                before = then.Time;
            }
            else if (now.Started >= start.TakenAt)
            {
                before = TimeSpan.Zero;
            }
            else
            {
                continue;
            }

            used += Math.Max(0, (now.Time - before).TotalSeconds);
        }

        return 100 * used / Stopwatch.GetElapsedTime(start.Timestamp, end.Timestamp).TotalSeconds / Environment.ProcessorCount;
    }

    public static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes >> 20} MB";

    // When each of the app's processes started and its CPU time so far, by process ID; TakenAt is UTC.
    public readonly record struct Sample(Dictionary<int, (DateTime Started, TimeSpan Time)> Cpu, long PrivateBytes, long Timestamp, DateTime TakenAt);
}

/// <summary>
/// Things the menu does to an app's windows.
/// </summary>
internal static class AppWindows
{
    private const int SwRestore = 9;
    private const int SwMaximize = 3;

    // The app's visible top-level windows in z-order, plus the window we saw (a Store app's frame belongs to another process).
    public static List<IntPtr> Of(ActiveApp app)
    {
        var windows = new List<IntPtr>();
        Native.EnumWindows(
            (window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out var pid);
                if ((window == app.Window || pid == app.ProcessId) && Native.IsWindowVisible(window)
                    && Native.IsAppWindow(window) && !Native.IsCloaked(window))
                {
                    windows.Add(window);
                }

                return true;
            },
            IntPtr.Zero);
        return windows;
    }

    // Whether a window handle kept from earlier still means that app's window. Menu actions run a moment
    // after the menu was made (and confirmations much later), and Windows reuses the handles of closed
    // windows, so a handle alone could by now be another program's window.
    public static bool StillBelongsTo(IntPtr window, ActiveApp app)
    {
        var pid = Native.OwnerOf(window);
        return pid != 0
            && (pid == app.ProcessId || (window == app.Window && Native.ChildProcessOf(window, pid) == app.ProcessId));
    }

    public static IEnumerable<IntPtr> StillOf(ActiveApp app, IEnumerable<IntPtr> windows) =>
        windows.Where(window => StillBelongsTo(window, app));

    public static void Hide(IEnumerable<IntPtr> windows)
    {
        foreach (var window in windows)
        {
            Native.ShowWindowAsync(window, Native.SwMinimize);
        }
    }

    // Asks each window to close, so apps can still offer to save.
    public static void Quit(IEnumerable<IntPtr> windows)
    {
        foreach (var window in windows)
        {
            Native.PostMessage(window, Native.WmClose, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public static DateTime? StartTime(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.StartTime;
        }
        catch
        {
            return null;
        }
    }

    // Only the process that was asked about: a process ID gets reused once its process is gone. Its handle
    // is opened first and held, so the start time is checked and the kill done on that same process.
    public static void ForceQuit(uint pid, DateTime started)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            _ = process.SafeHandle;
            if (process.StartTime == started)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    public static string ExeName(ActiveApp app) => Path.GetFileName(Native.ProcessPath(app.ProcessId) ?? string.Empty);

    public static bool IsExplorer(ActiveApp app) => ExeName(app).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);

    // Store apps start through their AppUserModelID; everything else by running the program again.
    public static void OpenNew(ActiveApp app)
    {
        var target = Native.PackagedAppId(app.ProcessId) is string id ? $"shell:AppsFolder\\{id}" : Native.ProcessPath(app.ProcessId);
        if (target is not null)
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
    }

    // Only the foreground process may hand out the foreground, so join its input queue first.
    public static void Activate(IntPtr window)
    {
        if (Native.IsIconic(window))
        {
            Native.ShowWindowAsync(window, SwRestore);
        }

        Native.PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // Gives this thread the message queue AttachThreadInput needs.
        var foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        var thisThread = Native.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thisThread && Native.AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            Native.SetForegroundWindow(window);
            Native.BringWindowToTop(window);
        }
        finally
        {
            if (attached)
            {
                Native.AttachThreadInput(thisThread, foregroundThread, false);
            }
        }

        // That isn't always enough (e.g. after a Dock flyout hands the foreground to the desktop).
        // A process that sent the last input may set the foreground, so tap Alt and try again. Not while
        // the user holds a modifier: Alt with Shift held can switch the input language.
        if (Native.GetForegroundWindow() != window && Keys.ModifiersReleased())
        {
            Keys.Tap(Keys.Alt);
            Native.SetForegroundWindow(window);
        }
    }

    public static void ToggleMaximize(IntPtr window) =>
        Native.ShowWindowAsync(window, Native.IsZoomed(window) ? SwRestore : SwMaximize);

    public static bool IsTopmost(IntPtr window) => Native.HasExStyle(window, Native.WsExTopmost);

    public static void SetTopmost(IntPtr window, bool topmost) =>
        Native.SetWindowPos(window, topmost ? -1 : -2, 0, 0, 0, 0, Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate); // HWND_TOPMOST / HWND_NOTOPMOST

    // Fills half of the window's monitor, minus the taskbar and the Dock.
    public static void SnapHalf(IntPtr window, bool left)
    {
        // Physical pixels throughout; DwmGetWindowAttribute ignores DPI virtualization.
        var previous = Native.SetThreadDpiAwarenessContext(-4); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        try
        {
            if (Native.IsZoomed(window))
            {
                Native.ShowWindowAsync(window, SwRestore);
                Thread.Sleep(150);
            }

            var work = Native.WorkArea(window);

            // Windows 10/11 frames have invisible resize borders, so size the outer rectangle to line up the visible edges.
            var (outer, visible) = Native.WindowBounds(window);
            var leftBorder = visible.Left - outer.Left;
            var topBorder = visible.Top - outer.Top;
            var rightBorder = outer.Right - visible.Right;
            var bottomBorder = outer.Bottom - visible.Bottom;

            var half = (work.Right - work.Left) / 2;
            var x = left ? work.Left : work.Left + half;
            var width = left ? half : work.Right - work.Left - half;
            Native.SetWindowPos(
                window, 0, x - leftBorder, work.Top - topBorder, width + leftBorder + rightBorder,
                work.Bottom - work.Top + topBorder + bottomBorder, Native.SwpNoZOrder | Native.SwpNoActivate);
        }
        finally
        {
            Native.SetThreadDpiAwarenessContext(previous);
        }
    }
}

internal static partial class Native
{
    public const long WsExTopmost = 0x8;
    public const uint SwpNoSize = 0x1;
    public const uint SwpNoMove = 0x2;
    public const uint SwpNoZOrder = 0x4;
    public const uint SwpNoActivate = 0x10;

    private const int DwmwaExtendedFrameBounds = 9;

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out Msg msg, IntPtr window, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    public static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    private static extern bool GetProcessMemoryInfo(IntPtr process, byte[] counters, uint size);

    public static bool HasExStyle(IntPtr window, long style) => (GetWindowLongPtr(window, GwlExStyle).ToInt64() & style) != 0;

    public static Rect WorkArea(IntPtr window)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */), ref info);
        return info.Work;
    }

    public static (Rect Outer, Rect Visible) WindowBounds(IntPtr window)
    {
        GetWindowRect(window, out var outer);
        return DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out Rect visible, Marshal.SizeOf<Rect>()) == 0
            ? (outer, visible)
            : (outer, outer);
    }

    // The AppUserModelID of a packaged app's process, or null for ordinary programs.
    public static string? PackagedAppId(uint pid) => WithProcess(pid, process =>
    {
        var length = 256u;
        var id = new System.Text.StringBuilder((int)length);
        return GetApplicationUserModelId(process, ref length, id) == 0 ? id.ToString() : null;
    });

    // "Memory (private working set)", the number Task Manager shows. Null when the process can't be read.
    public static long? PrivateWorkingSet(uint pid)
    {
        // Since Windows 8.1 GetProcessMemoryInfo needs only limited query rights; asking for VM_READ too fails
        // on processes that allow just the former, and the figure fell back to the full working set.
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            // PROCESS_MEMORY_COUNTERS_EX2: PrivateWorkingSetSize sits at byte 80 on x64.
            var counters = new byte[96];
            BitConverter.GetBytes(counters.Length).CopyTo(counters, 0);
            return GetProcessMemoryInfo(process, counters, (uint)counters.Length) ? BitConverter.ToInt64(counters, 80) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }
}
