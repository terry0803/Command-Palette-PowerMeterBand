using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerMeterBand;

/// <summary>
/// Dock band with only a Windows logo, like the Apple menu on a Mac.
/// Clicking it opens <see cref="SystemMenuPage"/>.
/// </summary>
internal sealed partial class SystemMenuBand : WrappedDockItem
{
    public static readonly IconInfo WindowsIcon = new(
        IconHelpers.FromRelativePath("Assets\\windows-light.svg").Light,
        IconHelpers.FromRelativePath("Assets\\windows-dark.svg").Dark);

    public SystemMenuPage Page { get; } = new();

    public SystemMenuBand()
        : base([], "com.terry.powermeter.systemmenu", "系統選單")
    {
        Icon = WindowsIcon;
        Items = [new ListItem(Page) { Icon = WindowsIcon, Title = string.Empty, Subtitle = "系統選單" }];
    }
}

/// <summary>
/// The menu itself, in the same order as the Apple menu.
/// </summary>
internal sealed partial class SystemMenuPage : ListPage
{
    private readonly IListItem[] _items;

    public SystemMenuPage()
    {
        Id = "com.terry.powermeter.systemmenu.page";
        Name = "開啟";
        Title = "系統選單";
        Icon = SystemMenuBand.WindowsIcon;
        _items =
        [
            Item("關於這台電腦", "\uE946", new OpenUrlCommand("ms-settings:about") { Name = "開啟" }), // Info
            Item("系統設定", "\uE713", new OpenUrlCommand("ms-settings:") { Name = "開啟" }), // Setting
            Item("Microsoft Store", "\uE719", new OpenUrlCommand("ms-windows-store:") { Name = "開啟" }), // Shop
            Item("強制結束⋯", "\uE711", DockCommands.AfterDismiss(() => Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }))), // Cancel
            Item("睡眠", "\uE708", DockCommands.AfterDismiss(Sleep)), // QuietHours
            Item("重新啟動⋯", "\uE777", DockCommands.Confirm("重新啟動", "要重新啟動電腦嗎？", () => Shutdown("/g /t 0"))), // UpdateRestore
            Item("關機⋯", "\uE7E8", DockCommands.Confirm("關機", "要關機嗎？", () => Shutdown("/s /hybrid /t 0"))), // PowerButton
            Item("鎖定畫面", "\uE72E", DockCommands.AfterDismiss(() => LockWorkStation())), // Lock
            Item("登出⋯", "\uF3B1", DockCommands.Confirm("登出", "要登出嗎？", () => ExitWindowsEx(0, 0))), // Leave
        ];
    }

    public override IListItem[] GetItems() => _items;

    private static ListItem Item(string title, string glyph, ICommand command) =>
        new(command) { Title = title, Icon = new IconInfo(glyph) };

    // SetSuspendState can't enter Modern Standby (S0 low power idle); on those PCs it hibernates.
    // There, turning the display off starts Modern Standby, just like the power button.
    private static void Sleep()
    {
        var capabilities = new byte[128]; // SYSTEM_POWER_CAPABILITIES
        if (GetPwrCapabilities(capabilities) && capabilities[20] != 0) // AoAc: Modern Standby
        {
            PostMessage(HwndBroadcast, WmSysCommand, ScMonitorPower, MonitorOff);
        }
        else
        {
            SetSuspendState(false, true, false);
        }
    }

    // Hidden so no console flashes up; other programs would take it as the state to start their window in.
    private static void Shutdown(string arguments) =>
        Process.Start(new ProcessStartInfo("shutdown", arguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });

    private static readonly IntPtr HwndBroadcast = 0xFFFF;
    private const uint WmSysCommand = 0x0112;
    private static readonly IntPtr ScMonitorPower = 0xF170;
    private static readonly IntPtr MonitorOff = 2;

    [DllImport("powrprof.dll")]
    private static extern bool GetPwrCapabilities(byte[] capabilities);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("powrprof.dll")]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern bool ExitWindowsEx(uint flags, uint reason);
}

internal static class DockCommands
{
    // Lets the palette close before the action runs, so it isn't still open after waking or unlocking.
    public static AnonymousCommand AfterDismiss(Action action, string name = "執行") => new(() => Task.Delay(300).ContinueWith(_ => action()))
    {
        Name = name,
        Result = CommandResult.Dismiss(),
    };

    // For actions that are hard to undo (restart, shut down, sign out, force quit), like the "⋯" items on a Mac.
    public static ConfirmPage Confirm(string title, string question, Action action) => new(title, question, action);
}

/// <summary>
/// Asks before an action, inside the Dock's own menu. Command Palette's confirmation dialog is no use from a
/// Dock menu: it opens in the palette's hidden window, so nothing shows, no key reaches it, and that hidden
/// window keeps the keyboard. 取消 comes first, so a stray Enter or extra click doesn't go ahead.
/// </summary>
internal sealed partial class ConfirmPage : ListPage
{
    private readonly IListItem[] _items;

    public ConfirmPage(string title, string question, Action action)
    {
        Id = $"com.terry.powermeter.confirm.{title}";
        Name = "執行";
        Title = title;
        _items =
        [
            new ListItem(new AnonymousCommand(() => { }) { Name = "取消", Result = CommandResult.Dismiss() }) { Title = "取消", Icon = Glyphs.Of(0xE711) }, // Cancel
            new ListItem(DockCommands.AfterDismiss(action, title)) { Title = title, Subtitle = question, Icon = Glyphs.Of(0xE7BA) }, // Warning
        ];
    }

    public override IListItem[] GetItems() => _items;
}
