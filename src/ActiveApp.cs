using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerMeterBand;

/// <summary>
/// The app in the foreground. <see cref="Window"/> is zero for the desktop.
/// </summary>
internal sealed record ActiveApp(string Name, uint ProcessId, IntPtr Window)
{
    public static readonly ActiveApp Desktop = new("桌面", 0, IntPtr.Zero);

    public bool IsDesktop => Window == IntPtr.Zero;

    // A Store app's frame whose app process hasn't attached yet: ProcessId is the frame host's,
    // which serves every Store app, so nothing may act on it. It's looked at again until the app shows up.
    public bool IsFrameOnly { get; init; }
}

/// <summary>
/// Dock band showing the foreground app's name followed by its menus, like the left side of a
/// Mac's menu bar: a classic program's own menu bar when it has one, otherwise 檔案 and 編輯,
/// then any app-specific menus (<see cref="AppMenuCatalog"/>), then 視窗.
/// Clicking the name opens <see cref="ActiveAppPage"/>.
/// </summary>
internal sealed partial class ActiveAppBand : WrappedDockItem, IDisposable
{
    // Long names stop growing here and get an ellipsis.
    private const double MaxLabelWidthDips = 180;
    private const double MenuLabelWidthDips = 90;
    private const string NameOnly = "name-only"; // Menu key when only the name shows (desktop aside)

    private readonly ForegroundAppTracker _tracker = new();
    private readonly DockTextListItem _item;
    private readonly ShortcutMenuPage _fileMenu;
    private readonly ShortcutMenuPage _editMenu;
    private readonly WindowMenuPage _windowMenu;
    private readonly DockTextListItem _fileItem;
    private readonly DockTextListItem _editItem;
    private readonly DockTextListItem _windowItem;

    // App-specific menu items, made once per app the first time it comes to the front.
    private readonly Dictionary<string, (IListItem[] Items, ShortcutMenuPage[] Pages)> _appMenus = [];
    private readonly List<Action> _shownMenus = []; // Refresh for each menu page now on the Dock
    private string? _menuKey;

    // Refreshes run on one worker at a time. The tracker's and the ticker's threads only ask for one,
    // so a Dock that's slow to answer never holds up foreground tracking or the other bands, and a
    // burst of app switches ends in a single refresh showing the latest app.
    private readonly CoalescedWork _refresh;
    private readonly CoalescedWork _retitle;

    public ActiveAppPage Page { get; }

    public ActiveAppBand()
        : base([], "com.terry.powermeter.activeapp", "目前的 App")
    {
        Icon = new IconInfo("\uECAA"); // AppIconDefault, only shown when picking bands
        Page = new ActiveAppPage(_tracker);
        _item = new DockTextListItem(Page, MaxLabelWidthDips);
        _fileMenu = new ShortcutMenuPage("com.terry.powermeter.activeapp.file", "檔案", _tracker, ShortcutMenuPage.FileMenu,
            app => UsesShared(app, set => set.FileMenu));
        _editMenu = new ShortcutMenuPage("com.terry.powermeter.activeapp.edit", "編輯", _tracker, ShortcutMenuPage.EditMenu,
            app => UsesShared(app, set => set.EditMenu));
        _windowMenu = new WindowMenuPage(_tracker);

        // No subtitles on menus: the Dock squeezes one in under the title of every item but the first.
        _fileItem = MenuItem(_fileMenu, "檔案");
        _editItem = MenuItem(_editMenu, "編輯");
        _windowItem = MenuItem(_windowMenu, "視窗");
        Items = [_item];

        _refresh = new CoalescedWork(Refresh);
        _retitle = new CoalescedWork(UpdateTitle);
        _tracker.Changed += _refresh.Request;
        _tracker.TitleChanged += _retitle.Request;
        _refresh.Request();

        // Programs build their menu bar a piece at a time as they start (and some change it later).
        DockTicker.Subscribe(RefreshClassicMenus);
    }

    // Cheap: a handful of menu strings, compared with what the Dock shows. Catches a menu bar that
    // appears late, changes, or goes away.
    private void RefreshClassicMenus()
    {
        var app = _tracker.Current;
        if (app.IsDesktop || _menuKey == NameOnly)
        {
            return;
        }

        var titles = ClassicMenu.TopLevel(app.Window);
        var showingClassic = _menuKey?.StartsWith("classic:", StringComparison.Ordinal) == true;
        if (titles.Count > 0 ? ClassicKey(app, titles) != _menuKey : showingClassic)
        {
            _refresh.Request();
        }
    }

    // The process is part of it: a reused window handle with the same menu titles is still another program.
    private static string ClassicKey(ActiveApp app, List<string> titles) => $"classic:{app.Window}:{app.ProcessId}:{string.Join('|', titles)}";

    // Only ever runs on _refresh's worker, one at a time, so the menu state needs no lock.
    private void Refresh()
    {
        var app = _tracker.Current;
        _item.Title = app.Name;
        _retitle.Request(); // The window title has its own worker, so an older title can't land after a newer one.

        // Windows keeps keys and menu commands from here out of an app running as administrator, so that
        // shows just its name rather than menus that would do nothing. So does a Store app's frame until
        // the app inside is known, since anything sent now would go to the host of every Store app.
        var unreachable = !app.IsDesktop && (app.IsFrameOnly || AppWindows.IsOutOfReach(app));
        var classic = app.IsDesktop || unreachable ? [] : ClassicMenu.TopLevel(app.Window);
        var exe = app.IsDesktop ? string.Empty : AppWindows.ExeName(app);
        var set = classic.Count > 0 || app.IsDesktop || unreachable ? null : SetFor(app, exe);

        // Only rebuild the Dock's items when the set of menus actually changes.
        var key = app.IsDesktop ? "desktop"
            : unreachable ? NameOnly
            : classic.Count > 0 ? ClassicKey(app, classic)
            : set is not null ? $"app:{set.Id}"
            : "shared";
        if (key != _menuKey)
        {
            IListItem[] items;
            var shown = new List<Action>();
            if (app.IsDesktop || unreachable)
            {
                items = [_item]; // Just the name: "桌面" has nothing to edit either.
            }
            else if (classic.Count > 0)
            {
                // Classic menus are read fresh each time they open.
                var owner = Native.OwnerOf(app.Window);
                IListItem[] menus = [.. classic.Select((title, i) => MenuItem(new ClassicMenuPage(app.Window, owner, [i], title), title))];
                items = [_item, .. menus, _windowItem];
                shown.Add(_windowMenu.Refresh);
            }
            else if (set is not null)
            {
                var (menus, pages) = AppMenusFor(set);
                items = [_item, .. menus, _windowItem];
                shown.AddRange(pages.Select(page => (Action)page.Refresh));
                if (set.FileMenu is null)
                {
                    shown.Add(_fileMenu.Refresh);
                }

                if (set.EditMenu is null)
                {
                    shown.Add(_editMenu.Refresh);
                }

                shown.Add(_windowMenu.Refresh);
            }
            else
            {
                items = [_item, _fileItem, _editItem, _windowItem];
                shown.AddRange([_fileMenu.Refresh, _editMenu.Refresh, _windowMenu.Refresh]);
            }

            // Recorded only once the Dock has taken the new items: if handing them over fails (the Dock
            // restarting, say), the next refresh tries again instead of thinking they're already shown.
            Items = items;
            _shownMenus.Clear();
            _shownMenus.AddRange(shown);
            _menuKey = key;
        }

        // Only menus on the Dock right now can be open or about to open; the rest get a refresh when they're back.
        Page.Refresh();
        foreach (var refresh in _shownMenus)
        {
            refresh();
        }
    }

    // The app's menu set, as Refresh picks it (console windows by their class, whatever runs in them).
    private static AppMenuSet? SetFor(ActiveApp app, string exe) =>
        Native.ClassName(app.Window) == "ConsoleWindowClass" ? AppMenuCatalog.Console : AppMenuCatalog.For(exe);

    // Whether an app gets the shared 檔案 or 編輯: an ordinary app with no menu bar of its own, whose set
    // (if it has one) doesn't bring its own version of that menu.
    private static bool UsesShared(ActiveApp app, Func<AppMenuSet, MenuEntry[]?> menu)
    {
        if (app.IsDesktop || app.IsFrameOnly || ClassicMenu.TopLevel(app.Window).Count > 0)
        {
            return false;
        }

        return SetFor(app, AppWindows.ExeName(app)) is not { } set || menu(set) is null;
    }

    // 檔案 and 編輯 (the app's own when its shortcuts differ), then the app's extra menus.
    private (IListItem[] Items, ShortcutMenuPage[] Pages) AppMenusFor(AppMenuSet set)
    {
        if (_appMenus.TryGetValue(set.Id, out var cached))
        {
            return cached;
        }

        var pages = new List<ShortcutMenuPage>();
        var items = new List<IListItem>();
        if (set.FileMenu is { Length: 0 })
        {
            // Nothing safe to put there (a console's close ends what's running in it).
        }
        else if (set.FileMenu is { } fileMenu)
        {
            var page = new ShortcutMenuPage($"com.terry.powermeter.activeapp.{set.Id}.file", "檔案", _tracker, fileMenu, app => SetFor(app, AppWindows.ExeName(app))?.Id == set.Id);
            pages.Add(page);
            items.Add(MenuItem(page, "檔案"));
        }
        else
        {
            items.Add(_fileItem);
        }

        if (set.EditMenu is { Length: 0 })
        {
            // The app has nothing to edit (Media Player, say).
        }
        else if (set.EditMenu is { } editMenu)
        {
            var page = new ShortcutMenuPage($"com.terry.powermeter.activeapp.{set.Id}.edit", "編輯", _tracker, editMenu, app => SetFor(app, AppWindows.ExeName(app))?.Id == set.Id);
            pages.Add(page);
            items.Add(MenuItem(page, "編輯"));
        }
        else
        {
            items.Add(_editItem);
        }

        for (var i = 0; i < set.Menus.Length; i++)
        {
            var (title, entries) = set.Menus[i];
            var page = new ShortcutMenuPage($"com.terry.powermeter.activeapp.{set.Id}.{i}", title, _tracker, entries, app => SetFor(app, AppWindows.ExeName(app))?.Id == set.Id);
            pages.Add(page);
            items.Add(MenuItem(page, title));
        }

        return _appMenus[set.Id] = ([.. items], [.. pages]);
    }

    private static DockTextListItem MenuItem(ICommand page, string title) =>
        new(page, MenuLabelWidthDips) { Title = title };

    // The full window title shows when hovering the app name.
    private void UpdateTitle() => _item.Subtitle = _tracker.WindowTitle;

    public void Dispose()
    {
        DockTicker.Unsubscribe(RefreshClassicMenus);
        Page.StopRefreshing();
        _tracker.Dispose();
    }
}

/// <summary>
/// Follows the foreground window and names the app it belongs to. It listens for
/// EVENT_SYSTEM_FOREGROUND on its own message-loop thread, plus a once-a-second check for events
/// some apps never send.
/// </summary>
internal sealed class ForegroundAppTracker : IDisposable
{
    // Windows that take the foreground without the user switching apps: the Dock itself,
    // Start, search, the taskbar, Alt+Tab, notification and quick settings flyouts.
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.CmdPal.UI.exe", "StartMenuExperienceHost.exe", "SearchHost.exe", "SearchApp.exe",
        "ShellExperienceHost.exe", "ShellHost.exe", "TextInputHost.exe", "LockApp.exe", "PowerToys.QuickAccess.exe",
    };

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "ForegroundStaging", "TaskListThumbnailWnd",
    };

    // The taskbar and its tray, where clicking an icon can pop up a program's own little window.
    private static readonly HashSet<string> TrayClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    private readonly Native.WinEventProc _onWinEvent; // Kept in fields so the callbacks aren't collected.
    private readonly Native.TimerProc _onRecheck;
    private readonly Native.TimerProc _onResync;
    private readonly Lock _lock = new();

    // Looking up a name (process path, version info, the shell's app name) is the slow part, so do it once per process.
    private readonly Dictionary<uint, (string Path, string Name)> _names = [];
    private uint _threadId;

    // Set once the tracker thread has a message queue, so Dispose's WM_QUIT can't be posted too early and lost.
    private readonly ManualResetEventSlim _ready = new();

    // Set after the first look at the foreground.
    private readonly ManualResetEventSlim _described = new();

    // Hooks on the current app's process; see WatchApp.
    private IntPtr _minimizeHook;
    private IntPtr _closeHook;
    private IntPtr _titleHook;

    // When the taskbar or its tray last took the foreground (Environment.TickCount64), and the popup it
    // opened (if any), which keeps being passed over while it has the foreground, for half a minute at most.
    private long _trayForegroundAt;
    private IntPtr _trayPopup;
    private long _trayPopupSince;

    // When the desktop took the foreground while the app in front was still on screen, or 0. That's
    // often a double-click on a desktop icon, so 桌面 waits for the app to open; see OnResync.
    private long _desktopSince;
    private uint _watchedPid;

    // The minimized window last passed over while it held the foreground, so it's looked at again only once.
    private IntPtr _iconicRechecked;

    public ForegroundAppTracker()
    {
        _onWinEvent = OnWinEvent;
        _onRecheck = OnRecheck;
        _onResync = OnResync;
        _thread = new Thread(Run) { IsBackground = true, Name = "Foreground app tracker" };
        _thread.Start();

        // So the band starts on the real app instead of showing 桌面 for a moment; a few milliseconds usually.
        _described.Wait(300);
    }

    private readonly Thread _thread;

    public event Action? Changed;

    public event Action? TitleChanged;

    public ActiveApp Current { get; private set; } = ActiveApp.Desktop;

    // The current window's title without the app's own name, or empty when there's nothing to add.
    public string WindowTitle { get; private set; } = string.Empty;

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();

        // A thread gets its message queue on its first message call; make it now, then let Dispose post to it.
        Native.PeekMessage(out _, IntPtr.Zero, 0, 0, 0 /* PM_NOREMOVE */);
        _ready.Set();

        var hook = Native.SetWinEventHook(
            Native.EventSystemForeground, Native.EventSystemForeground, IntPtr.Zero, _onWinEvent, 0, 0,
            Native.WinEventOutOfContext | Native.WinEventSkipOwnProcess);

        // Right after the Dock (re)starts, the foreground is often the Dock itself; use the app on top instead.
        var foreground = Native.GetForegroundWindow();
        Refresh(Describe(foreground, _names) is null ? TopAppWindow(_names) : foreground);
        _described.Set();
        var resync = Native.SetTimer(IntPtr.Zero, 0, 1000, _onResync);

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Native.DispatchMessage(ref msg);
        }

        Native.KillTimer(IntPtr.Zero, resync);
        Native.UnhookWinEvent(hook);
        WatchApp(ActiveApp.Desktop);
    }

    // A safety net, once a second: some apps (Electron ones, for one) take the foreground without the
    // event, or raise it without getting the foreground. Follow the foreground when it's plainly another
    // app; the desktop, the Dock and overlays are left to the events and their rules.
    private void OnResync(IntPtr window, uint message, nuint timerId, uint time)
    {
        var foreground = Native.GetForegroundWindow();
        bool missed;
        var desktopDue = false;
        lock (_lock)
        {
            // The usual case, the app shown still in front (or a tray popup being passed over): nothing to look up.
            if (_desktopSince == 0 && ((foreground == Current.Window && !Current.IsFrameOnly) || IsTrayPopup(foreground)))
            {
                return;
            }

            // Also catches a Store app attaching to the frame that was named for it.
            var app = IsTrayPopup(foreground) ? null : Describe(foreground, _names);
            missed = (foreground != Current.Window || Current.IsFrameOnly) && app is { IsDesktop: false } && app != Current;

            // The desktop has kept the foreground: show 桌面 once a second has passed with nothing starting
            // (Windows shows its app-starting cursor while a program it launched gets going), 20 at most.
            if (_desktopSince != 0)
            {
                var waited = Environment.TickCount64 - _desktopSince;
                if (app is not { IsDesktop: true })
                {
                    _desktopSince = 0;
                }
                else
                {
                    desktopDue = waited >= 1000 && (waited >= 20_000 || !Native.AppIsStarting());
                }
            }
        }

        if (missed)
        {
            Refresh(foreground);
        }
        else if (desktopDue)
        {
            Refresh(foreground, force: true, desktopDue: true);
        }
    }

    // Visible windows of every other app, for "hide others".
    // Another copy of the same program (Excel started with /x, say) isn't another app to hide.
    private static bool SameProgram(ActiveApp a, ActiveApp b) =>
        Native.ProcessPath(a.ProcessId) is string path && string.Equals(path, Native.ProcessPath(b.ProcessId), StringComparison.OrdinalIgnoreCase);

    public List<IntPtr> OtherAppWindows(ActiveApp app)
    {
        var windows = new List<IntPtr>();
        lock (_lock)
        {
            Native.EnumWindows(
                (window, _) =>
                {
                    if (window != app.Window && Native.IsWindowVisible(window) && !Native.IsIconic(window) && Native.IsAppWindow(window)
                        && !Native.IsCloaked(window) && Describe(window, _names) is { IsDesktop: false } other && other.ProcessId != app.ProcessId
                        && !SameProgram(other, app))
                    {
                        windows.Add(window);
                    }

                    return true;
                },
                IntPtr.Zero);
        }

        return windows;
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (eventType == Native.EventSystemForeground)
        {
            Refresh(window);
        }
        else if (eventType == Native.EventObjectNameChange)
        {
            // Child objects (tabs, buttons, text) rename themselves all the time; only the window's own title matters.
            if (window == Current.Window && objectId == 0 && childId == 0 && ReadTitle(Current) is var title && title != WindowTitle)
            {
                WindowTitle = title;
                TitleChanged?.Invoke();
            }
        }
        else if (window == Current.Window && objectId == 0 /* OBJID_WINDOW */)
        {
            // The foreground may not settle (or may become nothing, which raises no event), so look again shortly.
            Native.SetTimer(IntPtr.Zero, 0, 200, _onRecheck);
        }
    }

    private void OnRecheck(IntPtr window, uint message, nuint timerId, uint time)
    {
        Native.KillTimer(IntPtr.Zero, timerId);
        Refresh(Native.GetForegroundWindow(), force: true);
    }

    // Closing, hiding or minimizing the current app's last window can leave no foreground window at all,
    // which raises no foreground event. These hooks only cover that one process, so they stay cheap.
    private void WatchApp(ActiveApp app)
    {
        var pid = 0u;
        if (!app.IsDesktop)
        {
            Native.GetWindowThreadProcessId(app.Window, out pid); // A Store app's frame, not the app inside it.
        }

        if (pid == _watchedPid)
        {
            return;
        }

        Native.UnhookWinEvent(_minimizeHook);
        Native.UnhookWinEvent(_closeHook);
        Native.UnhookWinEvent(_titleHook);
        (_minimizeHook, _closeHook, _titleHook, _watchedPid) = (IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, pid);
        if (pid != 0)
        {
            const uint flags = Native.WinEventOutOfContext | Native.WinEventSkipOwnProcess;
            _minimizeHook = Native.SetWinEventHook(Native.EventSystemMinimizeStart, Native.EventSystemMinimizeStart, IntPtr.Zero, _onWinEvent, pid, 0, flags);
            _closeHook = Native.SetWinEventHook(Native.EventObjectDestroy, Native.EventObjectHide, IntPtr.Zero, _onWinEvent, pid, 0, flags);
            _titleHook = Native.SetWinEventHook(Native.EventObjectNameChange, Native.EventObjectNameChange, IntPtr.Zero, _onWinEvent, pid, 0, flags);
        }
    }

    // DesktopDue: the desktop has had the foreground long enough to show 桌面 (see OnResync).
    private void Refresh(IntPtr window, bool force = false, bool desktopDue = false)
    {
        lock (_lock)
        {
            if (window == Current.Window && window != IntPtr.Zero && !force && !Current.IsFrameOnly)
            {
                return;
            }

            // Like a Mac's menu bar extras, what a tray icon pops up doesn't change the app in front:
            // a popup window (see LooksLikePopup) taking the foreground just after the taskbar or tray did.
            // App windows opened from there (a title bar, or a good part of the screen) still switch.
            var now = Environment.TickCount64;
            if (TrayClasses.Contains(Native.ClassName(window)))
            {
                _trayForegroundAt = now;
            }

            ActiveApp? app;
            if (IsTrayPopup(window))
            {
                app = null;
            }
            else if (window != IntPtr.Zero && now - _trayForegroundAt < 1500 && Native.LooksLikePopup(window))
            {
                (_trayPopup, _trayPopupSince) = (window, now);
                app = null;
            }
            else
            {
                _trayPopup = IntPtr.Zero;
                app = Describe(window, _names);

                // A minimized window holding the foreground isn't the app in front: Windows hands the
                // foreground to one for a moment on unlocking, before the desktop takes it back. Look once
                // more shortly, in case it's being restored (a taskbar click), and switch then.
                if (app is { IsDesktop: false } && Native.IsIconic(window))
                {
                    app = null;
                    if (_iconicRechecked != window)
                    {
                        _iconicRechecked = window;
                        Native.SetTimer(IntPtr.Zero, 0, 200, _onRecheck);
                    }
                }
                else
                {
                    _iconicRechecked = IntPtr.Zero;
                }
            }

            // Cloaked counts as off screen: that's a window left on another virtual desktop.
            var currentOnScreen = !Current.IsDesktop && Native.IsWindowVisible(Current.Window) && !Native.IsIconic(Current.Window)
                && !Native.IsCloaked(Current.Window);

            // Closed, or hidden away (an app that keeps running in the tray): not just minimized.
            var currentGone = !Current.IsDesktop && (!Native.IsWindow(Current.Window) || !Native.IsWindowVisible(Current.Window));
            if (app is null)
            {
                // A shell surface or the Dock: keep the current app while its window is still on screen.
                if (currentOnScreen)
                {
                    return;
                }

                app = currentGone ? NextApp() : ActiveApp.Desktop;
            }
            else if (app.IsDesktop && currentGone)
            {
                // The app in front closed its window and Windows gave the foreground to the desktop instead of
                // the window underneath: show the app now on top, as a Mac would (桌面 only if there's none).
                app = NextApp();
            }
            else if (app.IsDesktop && currentOnScreen && !desktopDue)
            {
                // The app in front is still on screen, so this is either the user clicking the desktop or
                // the foreground falling to it: closing a Dock flyout (PowerToys 0.103), a tray icon's popup
                // or a menu hands it there rather than back to the app. Only a click, with the pointer over
                // the desktop, counts. Even then it's quite likely the start of a double-click that opens an
                // app, so keep showing the app in front until the new one opens rather than flash 桌面 while
                // it starts; 桌面 comes only if the desktop keeps the foreground (OnResync).
                if (Native.PointerOverDesktop() && _desktopSince == 0)
                {
                    _desktopSince = Environment.TickCount64;
                }

                return;
            }

            _desktopSince = 0;
            if (app == Current)
            {
                return;
            }

            Current = app;
        }

        WindowTitle = ReadTitle(Current);
        WatchApp(Current);
        Changed?.Invoke();
    }

    private bool IsTrayPopup(IntPtr window) =>
        window != IntPtr.Zero && window == _trayPopup && Environment.TickCount64 - _trayPopupSince < 30_000;

    // The app whose window is now on top, or the desktop when no app window is showing.
    private ActiveApp NextApp() =>
        TopAppWindow(_names) is var top && top != IntPtr.Zero && Describe(top, _names) is { } next ? next : ActiveApp.Desktop;

    private static string ReadTitle(ActiveApp app) =>
        app.IsDesktop ? string.Empty : WithoutAppName(Native.WindowText(app.Window), app.Name);

    // "Report.pptx - PowerPoint" -> "Report.pptx": the Dock already shows the app's name.
    internal static string WithoutAppName(string title, string appName)
    {
        title = title.Replace("\u200B", string.Empty).Trim(); // Edge writes "Microsoft\u200B Edge".
        if (title.Equals(appName, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        foreach (var separator in new[] { " - ", " — ", " – ", " | " })
        {
            var at = title.LastIndexOf(separator, StringComparison.Ordinal);
            if (at > 0 && Matches(title[(at + separator.Length)..]))
            {
                return title[..at].Trim();
            }

            at = title.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0 && Matches(title[..at]))
            {
                return title[(at + separator.Length)..].Trim();
            }
        }

        return title;

        // "PowerPoint" matches "Microsoft PowerPoint", and "Google Chrome" matches itself.
        bool Matches(string part) =>
            part.Trim() is { Length: > 1 } name
            && (appName.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(appName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Names the app that owns a window, or returns null for windows to ignore.
    /// </summary>
    private static ActiveApp? Describe(IntPtr window, Dictionary<uint, (string Path, string Name)> names)
    {
        if (window == IntPtr.Zero)
        {
            return null;
        }

        var windowClass = Native.ClassName(window);
        if (windowClass is "Progman" or "WorkerW")
        {
            return ActiveApp.Desktop;
        }

        // Overlays that take the foreground for a moment, like the Snipping Tool's capture screen and
        // Click to Do, are tool windows; keep showing the app underneath (which is what gets captured).
        // So are hidden windows: a tray icon hands the foreground to its program's hidden window before
        // showing a menu (the usual way to make the menu close properly), which isn't switching apps.
        // A program that shows its window just after taking the foreground is caught by OnResync.
        if (ShellClasses.Contains(windowClass) || Native.IsToolWindow(window) || !Native.IsWindowVisible(window))
        {
            return null;
        }

        Native.GetWindowThreadProcessId(window, out var pid);
        if (pid == Environment.ProcessId || Native.ProcessPath(pid) is not string path)
        {
            return null;
        }

        // Store apps draw inside an ApplicationFrameHost window; the app's own process owns a child CoreWindow.
        if (Path.GetFileName(path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (Native.ChildProcessOf(window, pid) is uint child && Native.ProcessPath(child) is string childPath)
            {
                (pid, path) = (child, childPath);
            }
            else
            {
                // The app hasn't attached yet (or is suspended); the frame's caption is its name until it does.
                return Native.WindowText(window) is { Length: > 0 } caption ? new ActiveApp(caption, pid, window) { IsFrameOnly = true } : null;
            }
        }

        var exe = Path.GetFileName(path);
        if (ShellProcesses.Contains(exe))
        {
            return null;
        }

        // Process IDs get reused, so the cached name only counts if the path still matches.
        if (!names.TryGetValue(pid, out var cached) || cached.Path != path)
        {
            var name = exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
                ? "檔案總管"
                : Native.PackagedAppName(pid) ?? AppMenuCatalog.For(exe)?.Name ?? FileDescription(path) ?? Path.GetFileNameWithoutExtension(path);
            // Process IDs are short-lived; starting over now and then keeps this small.
            if (names.Count >= 256)
            {
                names.Clear();
            }

            names[pid] = cached = (path, name);
        }

        return new ActiveApp(cached.Name, pid, window);
    }

    // The highest window in z-order that belongs to an app the user can see.
    private static IntPtr TopAppWindow(Dictionary<uint, (string Path, string Name)> names)
    {
        var top = IntPtr.Zero;
        Native.EnumWindows(
            (window, _) =>
            {
                if (Native.IsWindowVisible(window) && !Native.IsIconic(window) && Native.IsAppWindow(window)
                    && !Native.IsCloaked(window) && Describe(window, names) is { IsDesktop: false })
                {
                    top = window;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);
        return top;
    }

    private static string? FileDescription(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() is { Length: > 0 } description ? description : null;
        }
        catch
        {
            return null;
        }
    }

    // Waits (briefly) for the thread to be ready, so disposing right after creating still stops it.
    public void Dispose()
    {
        if (_ready.Wait(2000) && Native.PostThreadMessage(_threadId, Native.WmQuit, IntPtr.Zero, IntPtr.Zero))
        {
            // Lets it take down its hooks and timer before going on (not from the thread itself).
            if (Thread.CurrentThread != _thread)
            {
                _thread.Join(2000);
            }
        }
    }
}

internal static partial class Native
{
    public const uint EventSystemForeground = 0x0003;
    public const uint EventSystemMinimizeStart = 0x0016;
    public const uint EventObjectDestroy = 0x8001;
    public const uint EventObjectHide = 0x8003;
    public const uint EventObjectNameChange = 0x800C;
    public const uint WinEventOutOfContext = 0x0000;
    public const uint WinEventSkipOwnProcess = 0x0002;
    public const uint WmQuit = 0x0012;
    public const uint WmClose = 0x0010;
    public const uint WmSysCommand = 0x0112;
    public const int ScMinimize = 0xF020;
    public const int ScClose = 0xF060;
    public const int SwMinimize = 6;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private const uint GwOwner = 4;

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    public delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

    public delegate void TimerProc(IntPtr window, uint message, nuint timerId, uint time);

    [DllImport("user32.dll")]
    public static extern nuint SetTimer(IntPtr window, nuint timerId, uint milliseconds, TimerProc callback);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(IntPtr window, nuint timerId);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out Msg msg, IntPtr window, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Msg msg);

    // The window at the top of a window's owner chain (a dialog's main window), or the window itself.
    public static IntPtr RootOwner(IntPtr window) => GetAncestor(window, 3 /* GA_ROOTOWNER */);

    // The top-level window a control sits in (a dialog's own window, not the one that owns it).
    public static IntPtr RootOf(IntPtr window) => GetAncestor(window, 2 /* GA_ROOT */);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr window, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, nint name);

    private static readonly IntPtr AppStartingCursor = LoadCursor(IntPtr.Zero, 32650); // IDC_APPSTARTING

    // Windows' arrow-with-spinner cursor, shown while a program it launched (from the desktop, Start
    // or the taskbar) is starting up.
    public static bool AppIsStarting()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) && info.Cursor == AppStartingCursor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    // Whether the pointer is over the desktop itself (its wallpaper or icons), not a window, the
    // taskbar or the Dock.
    public static bool PointerOverDesktop() =>
        GetCursorPos(out var point) && ClassName(GetAncestor(WindowFromPoint(point), 2 /* GA_ROOT */)) is "Progman" or "WorkerW";

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    // The process a window belongs to, or 0 once the window is gone.
    public static uint OwnerOf(IntPtr window)
    {
        if (!IsWindow(window))
        {
            return 0;
        }

        GetWindowThreadProcessId(window, out var pid);
        return pid;
    }

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll")]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll")]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    public static string ClassName(IntPtr window)
    {
        var name = new StringBuilder(256);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }

    public static string WindowText(IntPtr window)
    {
        var text = new StringBuilder(256);
        return GetWindowText(window, text, text.Capacity) > 0 ? text.ToString() : string.Empty;
    }

    // Excludes tool windows and owned popups, which aren't the app's main windows.
    public static bool IsAppWindow(IntPtr window) =>
        GetWindow(window, GwOwner) == IntPtr.Zero && !IsToolWindow(window);

    // Floating palettes and overlays, which stay out of the taskbar and Alt+Tab.
    public static bool IsToolWindow(IntPtr window) => (GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExToolWindow) != 0;

    // What tray icons pop up, unlike app windows: no title bar, and a quarter of the screen at most.
    // (Neither the desktop nor an app window someone keeps on top looks like that.)
    public static bool LooksLikePopup(IntPtr window)
    {
        if (!IsWindowVisible(window) || (GetWindowLongPtr(window, -16 /* GWL_STYLE */).ToInt64() & WsCaption) == WsCaption
            || ClassName(window) is "Progman" or "WorkerW")
        {
            return false;
        }

        var (outer, _) = WindowBounds(window);
        var screen = WorkArea(window);
        long area = (long)(outer.Right - outer.Left) * (outer.Bottom - outer.Top);
        long screenArea = (long)(screen.Right - screen.Left) * (screen.Bottom - screen.Top);
        return area * 4 <= screenArea;
    }

    private const long WsCaption = 0x00C00000;

    // Windows on other virtual desktops and suspended Store apps are "visible" but cloaked.
    public static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static uint? ChildProcessOf(IntPtr frame, uint framePid)
    {
        uint? found = null;
        EnumChildWindows(
            frame,
            (child, _) =>
            {
                GetWindowThreadProcessId(child, out var pid);
                if (pid != framePid && ClassName(child) == "Windows.UI.Core.CoreWindow")
                {
                    found = pid;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);
        return found;
    }

    public static string? ProcessPath(uint pid) => WithProcess(pid, process =>
    {
        var size = 1024u;
        var path = new StringBuilder((int)size);
        return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
    });

    // Store and other packaged apps: the name Start shows, via the app's AppUserModelID.
    public static string? PackagedAppName(uint pid) => WithProcess(pid, process =>
    {
        var length = 256u;
        var id = new StringBuilder((int)length);
        if (GetApplicationUserModelId(process, ref length, id) != 0)
        {
            return null; // Not packaged.
        }

        try
        {
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName($"shell:AppsFolder\\{id}", IntPtr.Zero, ref iid, out var item);
            item.GetDisplayName(0, out var name); // SIGDN_NORMALDISPLAY
            try
            {
                return Marshal.PtrToStringUni(name);
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
            }
        }
        catch
        {
            return null;
        }
    });

    // Running as administrator (Registry Editor and Task Manager always do, for an admin account).
    public static bool IsElevated(uint pid) => WithProcess(pid, process =>
    {
        if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token))
        {
            return null;
        }

        try
        {
            return GetTokenInformation(token, 20 /* TokenElevation */, out var elevated, sizeof(int), out _) && elevated != 0 ? "elevated" : null;
        }
        finally
        {
            CloseHandle(token);
        }
    }) is not null;

    private static string? WithProcess(uint pid, Func<IntPtr, string?> read)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return read(process);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);

        void GetParent(out IShellItem parent);

        void GetDisplayName(uint form, out IntPtr name);

        void GetAttributes(uint mask, out uint attributes);

        void Compare(IShellItem other, uint hint, out int order);
    }
}
