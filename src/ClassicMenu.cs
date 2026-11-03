using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerMeterBand;

/// <summary>
/// Programs with a classic Win32 menu bar (Registry Editor, MMC consoles, IDLE...) keep their menus
/// where other processes can read them, so the Dock can show the real thing, like a Mac's menu bar:
/// the same items, grayed out and checked exactly as the program says.
/// </summary>
internal static partial class ClassicMenu
{
    private const uint ByPosition = 0x400;
    private const uint MiimState = 0x1;
    private const uint MiimId = 0x2;
    private const uint MiimSubMenu = 0x4;
    private const uint MiimFType = 0x100;
    private const uint MftSeparator = 0x800;
    private const uint MfsDisabled = 0x3; // Grayed or disabled
    private const uint MfsChecked = 0x8;
    private const uint MimStyle = 0x10;
    private const uint MnsNotifyByPos = 0x08000000;
    private const uint WmCommand = 0x0111;
    private const uint WmMenuCommand = 0x0126;
    private const uint WmSysCommand = 0x0112;
    private const int ScKeyMenu = 0xF100;
    private const uint WmInitMenu = 0x0116;
    private const uint WmInitMenuPopup = 0x0117;
    private const uint AbortIfHung = 0x2;

    // The menu bar's titles, or none when the window has no classic menu bar.
    public static List<string> TopLevel(IntPtr window)
    {
        var titles = new List<string>();
        var menu = GetMenu(window);
        for (var i = 0; menu != IntPtr.Zero && i < GetMenuItemCount(menu); i++)
        {
            titles.Add(Label(Text(menu, i)).Title);
        }

        return titles;
    }

    // Some toolkits (Tk, for one) draw their menu items themselves, so there's no text to read.
    // Then the best the Dock can do is open that menu in the program itself, as Alt+its letter would.
    public static IListItem[] OpenInProgram(IntPtr window, uint owner, int topLevelIndex)
    {
        var mnemonic = Mnemonic(Text(GetMenu(window), topLevelIndex));
        return
        [
            new ListItem(DockCommands.AfterDismiss(() =>
            {
                if (Native.OwnerOf(window) == owner && IsWindowEnabled(window))
                {
                    AppWindows.Activate(window);
                    PostMessage(window, WmSysCommand, (IntPtr)ScKeyMenu, (IntPtr)mnemonic);
                }
            }))
            {
                Title = "在程式裡打開這個選單",
                Subtitle = "這個程式的選單項目讀不到",
                Icon = Glyphs.Of(0xE8A7), // OpenInNewWindow
            },
        ];
    }

    // The menu at a path of positions: the menu bar's [2] is its third menu, [2, 0] that menu's first submenu,
    // after the program has had the notices a user opening it would cause. Programs fill in and gray out
    // items when the menu bar activates (WM_INITMENU) and as each menu opens (WM_INITMENUPOPUP), and may
    // swap in a new menu while they're at it, so the path is followed again once they're done. Enabled
    // is false if any menu on the way is grayed out by then: nothing inside can run.
    // (A program that isn't responding holds up the Dock for a moment at most.)
    private static IntPtr OpenMenuAt(IntPtr window, int[] path, out bool enabled)
    {
        SendMessageTimeout(window, WmInitMenu, GetMenu(window), 0, AbortIfHung, 200, out _);

        // Each level is found afresh from the menu bar as it stands, since handling a notice can replace
        // any menu above it, the whole bar included. A menu that turns up only after its notice (a
        // replacement) gets one of its own.
        var notified = new HashSet<IntPtr>();
        for (var depth = 1; depth <= path.Length; depth++)
        {
            var prefix = path[..depth];
            var menu = MenuAt(window, prefix, out _);
            if (menu == IntPtr.Zero)
            {
                break;
            }

            if (notified.Add(menu))
            {
                SendMessageTimeout(window, WmInitMenuPopup, menu, path[depth - 1], AbortIfHung, 200, out _);
            }

            if (MenuAt(window, prefix, out _) is var after && after != IntPtr.Zero && notified.Add(after))
            {
                SendMessageTimeout(window, WmInitMenuPopup, after, path[depth - 1], AbortIfHung, 200, out _);
            }
        }

        // While the program has a dialog open, its window is disabled and its own menu bar does nothing.
        var opened = MenuAt(window, path, out enabled);
        enabled &= IsWindowEnabled(window);
        return opened;
    }

    // Follows the path as the menus stand now, without telling the program.
    private static IntPtr MenuAt(IntPtr window, int[] path, out bool enabled)
    {
        enabled = true;
        var menu = GetMenu(window);
        foreach (var position in path)
        {
            if (Info(menu, position) is not { SubMenu: not 0 } item)
            {
                return IntPtr.Zero;
            }

            enabled &= (item.State & MfsDisabled) == 0;
            menu = item.SubMenu;
        }

        return menu;
    }

    // One call for type, state, submenu and ID. (GetMenuState packs a submenu's item count into the
    // same bits as the flags, so a submenu of 8 to 15 items looked like a separator.)
    private static MenuItemInfo? Info(IntPtr menu, int position)
    {
        var info = new MenuItemInfo { Size = (uint)Marshal.SizeOf<MenuItemInfo>(), Mask = MiimFType | MiimState | MiimId | MiimSubMenu };
        return menu != IntPtr.Zero && GetMenuItemInfo(menu, (uint)position, true, ref info) ? info : null;
    }

    // Owner is the program the menu was found in: if the window has since closed and its handle gone to
    // another program, that program's menus aren't shown (or run) in its place.
    public static IListItem[] Items(IntPtr window, uint owner, int[] path)
    {
        if (Native.OwnerOf(window) != owner)
        {
            return [];
        }

        var menu = OpenMenuAt(window, path, out var enabled);
        if (menu == IntPtr.Zero)
        {
            return [];
        }

        var items = new List<IListItem>();
        for (var i = 0; i < GetMenuItemCount(menu); i++)
        {
            if (Info(menu, i) is not { } info || (info.Type & MftSeparator) != 0)
            {
                continue;
            }

            var (title, shortcut) = Label(Text(menu, i));
            if (title.Length == 0)
            {
                continue; // Owner-drawn items have no text to show.
            }

            // A grayed-out item can't be chosen, nor anything in a grayed-out menu, as in the program itself.
            var available = enabled && (info.State & MfsDisabled) == 0;
            ICommand command = !available ? new NoOpCommand()
                : info.SubMenu != IntPtr.Zero ? new ClassicMenuPage(window, owner, [.. path, i], title)
                : Choose(window, owner, path, i, info.Id, title);

            items.Add(new ListItem(command)
            {
                Title = title,
                Subtitle = available ? shortcut : "目前無法使用",
                Icon = (info.State & MfsChecked) != 0 ? Glyphs.Of(0xE73E) : Glyphs.None, // CheckMark
            });
        }

        return [.. items];
    }

    // Looks again when clicked: since the list was made, the window may have closed (and its handle gone
    // to another program), or the program may have grayed the item out or rebuilt the menu with other
    // commands (or a submenu) at the same place. Then nothing happens.
    private static ICommand Choose(IntPtr window, uint owner, int[] path, int position, uint id, string title) =>
        DockCommands.AfterDismiss(() =>
        {
            if (Native.OwnerOf(window) != owner)
            {
                return;
            }

            var menu = OpenMenuAt(window, path, out var enabled);
            if (!enabled || Info(menu, position) is not { } info || info.Id != id || (info.State & MfsDisabled) != 0
                || info.SubMenu != IntPtr.Zero || (info.Type & MftSeparator) != 0 || Label(Text(menu, position)).Title != title)
            {
                return;
            }

            AppWindows.Activate(window);

            // A menu bar styled MNS_NOTIFYBYPOS reports choices by position, with WM_MENUCOMMAND.
            if (NotifiesByPosition(window))
            {
                PostMessage(window, WmMenuCommand, position, menu);
            }
            else
            {
                PostMessage(window, WmCommand, (IntPtr)id, IntPtr.Zero);
            }
        });

    private static bool NotifiesByPosition(IntPtr window)
    {
        var info = new MenuInfo { Size = (uint)Marshal.SizeOf<MenuInfo>(), Mask = MimStyle };
        return GetMenuInfo(GetMenu(window), ref info) && (info.Style & MnsNotifyByPos) != 0;
    }

    // The letter after a single '&' ("&&" is an ampersand, not a mnemonic): "Tools && Options (&O)" -> 'o'.
    private static char Mnemonic(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != '&')
            {
                continue;
            }

            if (text[i + 1] != '&')
            {
                return char.ToLowerInvariant(text[i + 1]);
            }

            i++;
        }

        return '\0';
    }

    private static string Text(IntPtr menu, int position)
    {
        var text = new StringBuilder(256);
        return GetMenuString(menu, (uint)position, text, text.Capacity, ByPosition) > 0 ? text.ToString() : string.Empty;
    }

    // "開啟舊檔(&O)...\tCtrl+O" -> ("開啟舊檔...", "Ctrl+O").
    private static (string Title, string Shortcut) Label(string text)
    {
        var parts = text.Split('\t', 2);
        var title = MnemonicInParentheses().Replace(parts[0], string.Empty).Replace("&&", "\u0001").Replace("&", string.Empty).Replace("\u0001", "&");
        return (title.Trim(), parts.Length > 1 ? parts[1].Trim() : string.Empty);
    }

    [GeneratedRegex(@"\(&\w\)")]
    private static partial Regex MnemonicInParentheses();

    [DllImport("user32.dll")]
    private static extern IntPtr GetMenu(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSubMenu(IntPtr menu, int position);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll", EntryPoint = "GetMenuItemInfoW")]
    private static extern bool GetMenuItemInfo(IntPtr menu, uint item, bool byPosition, ref MenuItemInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetMenuInfo(IntPtr menu, ref MenuInfo info);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int max, uint flags);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint msg, IntPtr wParam, nint lParam, uint flags, uint timeout, out IntPtr result);

    // MENUITEMINFOW, laid out for 64-bit Windows.
    [StructLayout(LayoutKind.Sequential)]
    private struct MenuItemInfo
    {
        public uint Size;
        public uint Mask;
        public uint Type;
        public uint State;
        public uint Id;
        public IntPtr SubMenu;
        public IntPtr CheckedBitmap;
        public IntPtr UncheckedBitmap;
        public UIntPtr ItemData;
        public IntPtr TypeData;
        public uint TypeDataLength;
        public IntPtr ItemBitmap;
    }

    // MENUINFO, laid out for 64-bit Windows.
    [StructLayout(LayoutKind.Sequential)]
    private struct MenuInfo
    {
        public uint Size;
        public uint Mask;
        public uint Style;
        public uint MaxHeight;
        public IntPtr Background;
        public uint ContextHelpId;
        public UIntPtr MenuData;
    }
}

/// <summary>
/// One menu of a program's classic menu bar (or a submenu, by its path of positions), read fresh
/// each time it opens.
/// </summary>
internal sealed partial class ClassicMenuPage : ListPage
{
    private readonly IntPtr _window;
    private readonly uint _owner;
    private readonly int[] _path;

    // Owner is the process the window belonged to when its menu was found; the page shows nothing
    // once the handle belongs to anything else.
    public ClassicMenuPage(IntPtr window, uint owner, int[] path, string title)
    {
        _window = window;
        _owner = owner;
        _path = path;
        Id = $"com.terry.powermeter.activeapp.classic.{window}.{string.Join('.', path)}";
        Name = "開啟";
        Title = title;
        Icon = Glyphs.None; // The Dock would otherwise show this icon before the menu's name.
    }

    public override IListItem[] GetItems() =>
        Native.OwnerOf(_window) != _owner ? []
        : ClassicMenu.Items(_window, _owner, _path) is { Length: > 0 } items ? items
        : ClassicMenu.OpenInProgram(_window, _owner, _path[0]);
}
