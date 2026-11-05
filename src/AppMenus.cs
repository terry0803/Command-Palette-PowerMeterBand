using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerMeterBand;

/// <summary>
/// One menu item that acts on the current app.
/// </summary>
/// <param name="SkipFor">Apps (exe names) where the shortcut means something else or nothing, so the item is left out.</param>
internal sealed record MenuEntry(string Title, string Shortcut, int Glyph, Action<ActiveApp> Run, string Section = "", params string[] SkipFor)
{
    // For apps whose shortcuts the user can change (VS Code, Windows Terminal): the app's own name for the
    // command, and its default keys in the app's notation (null if it has none by default). See KeyBindings.
    public string? Command { get; init; }

    public string? DefaultKeys { get; init; }
}

/// <summary>
/// The 檔案 and 編輯 menus. Windows apps don't publish their menus the way Mac apps do, so these
/// press the shortcuts that mean the same thing in nearly every app. Shortcuts that some popular
/// app reads differently (redo, zoom, refresh) are left out rather than risk doing the wrong thing.
/// </summary>
internal sealed partial class ShortcutMenuPage : ListPage
{
    private readonly ForegroundAppTracker _tracker;
    private readonly MenuEntry[] _entries;
    private readonly Func<ActiveApp, bool> _accepts;

    // Accepts: whether these entries belong to an app. The Dock can ask a page it showed for one app for
    // its items again after the app in front has changed; it gets nothing rather than one app's
    // shortcuts aimed at another.
    public ShortcutMenuPage(string id, string title, ForegroundAppTracker tracker, MenuEntry[] entries, Func<ActiveApp, bool> accepts)
    {
        _tracker = tracker;
        _entries = entries;
        _accepts = accepts;
        Id = id;
        Name = "開啟";
        Title = title;
        Icon = Glyphs.None; // The Dock would otherwise show this icon before the menu's name.
    }

    public static MenuEntry[] FileMenu =>
    [
        new("新增", "Ctrl+N", 0xE710, app => Keys.SendTo(app, Keys.Control, 0x4E)), // Add
        new("開啟舊檔⋯", "Ctrl+O", 0xE8E5, app => Keys.SendTo(app, Keys.Control, 0x4F)), // OpenFile
        new("儲存", "Ctrl+S", 0xE74E, app => Keys.SendTo(app, Keys.Control, 0x53)), // Save
        new("列印⋯", "Ctrl+P", 0xE749, app => Keys.SendTo(app, Keys.Control, 0x50), SkipFor: ["Code.exe"]), // Print; VS Code's Ctrl+P is Go to File
        new("關閉視窗", "Alt+F4", 0xE8BB, app => AppWindows.Quit(AppWindows.StillOf(app, [app.Window]))), // ChromeClose; a close request, no keys needed
    ];

    public static MenuEntry[] EditMenu =>
    [
        new("復原", "Ctrl+Z", 0xE7A7, app => Keys.SendTo(app, Keys.Control, 0x5A)), // Undo
        new("剪下", "Ctrl+X", 0xE8C6, app => Keys.SendTo(app, Keys.Control, 0x58)), // Cut
        new("複製", "Ctrl+C", 0xE8C8, app => Keys.SendTo(app, Keys.Control, 0x43)), // Copy
        new("貼上", "Ctrl+V", 0xE77F, app => Keys.SendTo(app, Keys.Control, 0x56)), // Paste
        new("全選", "Ctrl+A", 0xE8B3, app => Keys.SendTo(app, Keys.Control, 0x41)), // SelectAll
        new("尋找⋯", "Ctrl+F", 0xE721, app => Keys.SendTo(app, Keys.Control, 0x46), SkipFor: ["OUTLOOK.EXE", "olk.exe", "mspaint.exe"]), // Search; Outlook's Ctrl+F forwards mail, Paint has no search
        new("剪貼簿記錄", "Win+V", 0xF0E3, app => Keys.SendTo(app, Keys.LeftWindows, 0x56), "系統"), // ClipboardList
        new("表情符號與符號", "Win+.", 0xE76E, app => Keys.SendTo(app, Keys.LeftWindows, 0xBE), "系統"), // Emoji2
        new("螢幕擷取", "Win+Shift+S", 0xE722, app => Keys.SendTo(app, Keys.LeftWindows, Keys.Shift, 0x53), "系統"), // Camera
    ];

    public override IListItem[] GetItems()
    {
        var app = _tracker.Current;
        if (!_accepts(app))
        {
            return [];
        }

        var exe = AppWindows.ExeName(app);
        var items = new List<IListItem>();
        List<KeyBindings.Binding>? bindings = null; // The user's key settings, read once for the whole menu.
        foreach (var entry in _entries.Where(entry => !entry.SkipFor.Contains(exe, StringComparer.OrdinalIgnoreCase)))
        {
            // Read from the user's own key settings each time the menu opens: an item whose keys were
            // changed shows (and presses) the new ones, and one with no working keys left is hidden.
            var (shortcut, run) = (entry.Shortcut, entry.Run);
            if (entry.Command is string command)
            {
                bindings ??= KeyBindings.UserBindings(app);
                if (KeyBindings.Resolve(bindings, command, entry.DefaultKeys) is not { } keys)
                {
                    continue;
                }

                if (keys.Text != entry.DefaultKeys)
                {
                    shortcut = keys.Display;
                    run = target => Keys.SendTo(target, keys.Chords);
                }
            }

            items.Add(new ListItem(DockCommands.AfterDismiss(() => run(app)))
            {
                Title = entry.Title,
                Subtitle = shortcut,
                Icon = Glyphs.Of(entry.Glyph),
                Section = entry.Section,
            });
        }

        return [.. items];
    }

    public void Refresh() => RaiseItemsChanged();
}

/// <summary>
/// The 視窗 menu, like a Mac's: the app's open windows to switch between, and arranging the current one.
/// </summary>
internal sealed partial class WindowMenuPage : ListPage
{
    private readonly ForegroundAppTracker _tracker;

    public WindowMenuPage(ForegroundAppTracker tracker)
    {
        _tracker = tracker;
        Id = "com.terry.powermeter.activeapp.window";
        Name = "開啟";
        Title = "視窗";
        Icon = Glyphs.None;
    }

    public override IListItem[] GetItems()
    {
        var app = _tracker.Current;
        if (app.IsDesktop)
        {
            return [];
        }

        var current = app.Window;
        var maximized = Native.IsZoomed(current);
        var topmost = AppWindows.IsTopmost(current);
        var items = new List<IListItem>
        {
            Item("貼齊左半邊", 0xE90C, Back(current, window => AppWindows.SnapHalf(window, left: true))), // DockLeft
            Item("貼齊右半邊", 0xE90D, Back(current, window => AppWindows.SnapHalf(window, left: false))), // DockRight
            Item(maximized ? "還原大小" : "最大化", maximized ? 0xE923 : 0xE922, Back(current, AppWindows.ToggleMaximize)), // ChromeRestore / ChromeMaximize
            Item(topmost ? "取消保持在最上層" : "保持在最上層", topmost ? 0xE77A : 0xE718, Back(current, window => AppWindows.SetTopmost(window, !topmost))), // Unpin / Pin
            Item("最小化", 0xE921, On(current, window => AppWindows.Hide([window]))), // ChromeMinimize
        };

        // Like the list at the bottom of a Mac's Window menu, with the current window checked.
        foreach (var window in AppWindows.Of(app))
        {
            var title = Native.WindowText(window) is { Length: > 0 } text ? text : app.Name;
            items.Add(Item(title, window == current ? 0xE73E : 0, On(window, AppWindows.Activate), app.Name)); // CheckMark
        }

        return [.. items];

        // Acts on the window only if it's still this app's when the item runs.
        Action On(IntPtr window, Action<IntPtr> act) => () =>
        {
            if (AppWindows.StillBelongsTo(window, app))
            {
                act(window);
            }
        };

        // Likewise, and hands the window the keyboard again: closing the menu often leaves it on the desktop.
        Action Back(IntPtr window, Action<IntPtr> act) => On(window, target =>
        {
            act(target);
            AppWindows.Activate(target);
        });
    }

    public void Refresh() => RaiseItemsChanged();

    private static ListItem Item(string title, int glyph, Action action, string section = "") =>
        new(DockCommands.AfterDismiss(action)) { Title = title, Icon = Glyphs.Of(glyph), Section = section };
}

internal static class Glyphs
{
    public static readonly IconInfo None = new(string.Empty);

    public static IconInfo Of(int codePoint) => codePoint == 0 ? None : new(char.ConvertFromUtf32(codePoint));
}

/// <summary>
/// Presses a shortcut in the current app.
/// </summary>
internal static class Keys
{
    public const ushort Shift = 0x10;
    public const ushort Control = 0x11;
    public const ushort Alt = 0x12;
    public const ushort LeftWindows = 0x5B;

    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x1;
    private const uint KeyEventKeyUp = 0x2;

    // The palette has just closed, so bring the app back first. Keys always go to whatever has
    // the foreground, so if the app doesn't get it, press nothing rather than hit another window.
    public static void SendTo(ActiveApp app, params ushort[] chord) => SendTo(app, null, [chord]);

    public static void SendTo(ActiveApp app, Func<bool>? when, params ushort[] chord) => SendTo(app, when, [chord]);

    // Chords pressed one after another (VS Code's "Ctrl+K Ctrl+S").
    public static void SendTo(ActiveApp app, ushort[][] chords) => SendTo(app, null, chords);

    // When: checked just before pressing, once the app is in front (e.g. that the right control has the focus).
    public static void SendTo(ActiveApp app, Func<bool>? when, ushort[][] chords)
    {
        // Injected keys add to whatever the user still holds (Shift left down would turn Ctrl+C into
        // Ctrl+Shift+C, and Shift with the Alt tap that activating may need can switch the input
        // language), so wait for modifiers to be let go first, and give up if they aren't.
        if (!ModifiersReleased())
        {
            return;
        }

        if (!HasForeground(app))
        {
            // Bring back only the app's own window, not whatever got its handle after it closed.
            if (!AppWindows.StillBelongsTo(app.Window, app))
            {
                return;
            }

            AppWindows.Activate(app.Window);
            for (var attempt = 0; attempt < 10 && !HasForeground(app); attempt++)
            {
                Thread.Sleep(30);
            }

            if (!HasForeground(app))
            {
                return;
            }

            // Apps put the caret back in their text box a moment after they're activated.
            Thread.Sleep(150);
        }

        // Pressed again in the meantime (unlikely, but the chord would change): leave it.
        if (!ModifiersReleased())
        {
            return;
        }

        // Focus can move during those waits; a paste must never land in some other window.
        if (HasForeground(app) && (when is null || when()))
        {
            for (var i = 0; i < chords.Length; i++)
            {
                // Between the parts of a sequence, focus may have moved (also while waiting for keys to be
                // let go): the rest must not go elsewhere, so the window is checked after that wait.
                if (i > 0)
                {
                    Thread.Sleep(30);
                    if (!ModifiersReleased() || !HasForeground(app) || (when is not null && !when()))
                    {
                        return;
                    }
                }

                Press(chords[i]);
            }
        }
    }

    // Whether the keyboard focus is in a list view (any toolkit's wrapper of the common control) whose window's
    // title starts with one of mainTitles: the app's main window, not a dialog or another window of it with a list
    // of its own, nor a text box.
    public static bool FocusIsListIn(params string[] mainTitles)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        var thread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        if (!GetGUIThreadInfo(thread, ref info) || info.Focus == IntPtr.Zero
            || !Native.ClassName(info.Focus).Contains("SysListView32", StringComparison.Ordinal))
        {
            return false;
        }

        var title = Native.WindowText(Native.RootOf(info.Focus));
        return mainTitles.Any(main => title.StartsWith(main, StringComparison.Ordinal));
    }

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int Left, Top, Right, Bottom;
    }

    private static readonly ushort[] Modifiers = [Shift, Control, Alt, LeftWindows, 0x5C /* right Windows */];

    // Waits up to a second for Shift, Ctrl, Alt and the Windows keys to be let go.
    public static bool ModifiersReleased()
    {
        for (var waited = 0; waited <= 1000; waited += 20)
        {
            if (!Modifiers.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    // The app's own window (or one of its dialogs), or the Store frame that still holds the app. Both the
    // process and the window must match: Windows reuses the handles of closed windows, and another window
    // of the same app (a second document, another Explorer window) isn't the one the menu was opened for.
    private static bool HasForeground(ActiveApp app)
    {
        var foreground = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(foreground, out var pid);
        return (pid == app.ProcessId && Native.RootOwner(foreground) == Native.RootOwner(app.Window))
            || (foreground == app.Window && Native.ChildProcessOf(foreground, pid) == app.ProcessId);
    }

    public static void Tap(ushort key) => Press([key]);

    // Arrows, Windows and media keys only behave as themselves when sent as extended keys.
    private static bool IsExtended(ushort key) =>
        key is LeftWindows or (>= 0x21 and <= 0x28) or 0x2D or 0x2E or (>= 0xA6 and <= 0xB7);

    // Presses the keys in order and releases them in reverse, like a person would.
    private static void Press(ushort[] chord)
    {
        var inputs = new Input[chord.Length * 2];
        for (var i = 0; i < chord.Length; i++)
        {
            var flags = IsExtended(chord[i]) ? KeyEventExtendedKey : 0;
            inputs[i] = new Input { Type = InputKeyboard, VirtualKey = chord[i], Flags = flags };
            inputs[inputs.Length - 1 - i] = new Input { Type = InputKeyboard, VirtualKey = chord[i], Flags = flags | KeyEventKeyUp };
        }

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    // INPUT with its KEYBDINPUT member, laid out for 64-bit Windows.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public ushort VirtualKey;

        [FieldOffset(10)]
        public ushort ScanCode;

        [FieldOffset(12)]
        public uint Flags;

        [FieldOffset(16)]
        public uint Time;

        [FieldOffset(24)]
        public IntPtr ExtraInfo;
    }
}
