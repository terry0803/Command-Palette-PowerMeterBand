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
            Item("強制結束⋯", "\uE711", DockCommands.AfterDismiss(() => Start("taskmgr.exe"))), // Cancel
            Item("睡眠", "\uE708", DockCommands.AfterDismiss(Sleep)), // QuietHours
            Item("重新啟動⋯", "\uE777", DockCommands.Confirm("重新啟動", "要重新啟動電腦嗎？", () => Start("shutdown", "/g /t 0"))), // UpdateRestore
            Item("關機⋯", "\uE7E8", DockCommands.Confirm("關機", "要關機嗎？", () => Start("shutdown", "/s /hybrid /t 0"))), // PowerButton
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

    private static void Start(string file, string arguments = "") =>
        Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });

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
    public static AnonymousCommand Confirm(string title, string question, Action action) => new(() => { })
    {
        Name = "執行",
        Result = CommandResult.Confirm(new ConfirmationArgs
        {
            Title = title,
            Description = question,
            PrimaryCommand = AfterDismiss(action, title),
            IsPrimaryCommandCritical = true,
        }),
    };
}
