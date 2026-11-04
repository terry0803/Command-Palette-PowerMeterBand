using System.Diagnostics;

namespace PowerMeterBand;

/// <summary>
/// Menus for one app, shown between 編輯 and 視窗 the way a Mac app adds its own menus.
/// </summary>
/// <param name="FileMenu">Replaces the shared 檔案 menu where its shortcuts mean something else in this app.</param>
/// <param name="EditMenu">Replaces the shared 編輯 menu likewise (a terminal's Ctrl+C interrupts, for one).</param>
internal sealed record AppMenuSet(
    string Id,
    string[] Exes,
    (string Title, MenuEntry[] Entries)[] Menus,
    MenuEntry[]? FileMenu = null,
    MenuEntry[]? EditMenu = null);

/// <summary>
/// App-specific menus for popular apps, built only from each app's documented shortcuts,
/// so every item does what it says. Apps not listed keep 檔案, 編輯 and 視窗.
/// </summary>
internal static class AppMenuCatalog
{
    private const ushort Ctrl = Keys.Control;
    private const ushort Shift = Keys.Shift;
    private const ushort Alt = Keys.Alt;
    private const ushort Tab = 0x09;
    private const ushort Enter = 0x0D;
    private const ushort Left = 0x25;
    private const ushort Up = 0x26;
    private const ushort Right = 0x27;
    private const ushort Down = 0x28;
    private const ushort PageUp = 0x21;
    private const ushort PageDown = 0x22;
    private const ushort Comma = 0xBC;
    private const ushort Period = 0xBE;
    private const ushort Backslash = 0xDC;
    private const ushort Slash = 0xBF;
    private const ushort BracketLeft = 0xDB;
    private const ushort BracketRight = 0xDD;
    private const ushort Insert = 0x2D;
    private const ushort NumpadPlus = 0x6B;
    private const ushort NumpadMinus = 0x6D;
    private const ushort F2 = 0x71;
    private const ushort F3 = 0x72;
    private const ushort F5 = 0x74;
    private const ushort F8 = 0x77;
    private const ushort F10 = 0x79;
    private const ushort F11 = 0x7A;
    private const ushort F12 = 0x7B;
    private const ushort Delete = 0x2E;
    private const ushort Plus = 0xBB; // VK_OEM_PLUS, the =/+ key
    private const ushort Minus = 0xBD;
    private const ushort Semicolon = 0xBA;
    private const ushort Backtick = 0xC0;
    private const ushort Quote = 0xDE;
    private const ushort NextTrack = 0xB0;
    private const ushort PreviousTrack = 0xB1;
    private const ushort PlayPause = 0xB3;

    private static readonly AppMenuSet[] Sets =
    [
        // Chromium browsers share their shortcuts; Samsung Internet for PC is Chromium-based too.
        new("browser", ["chrome.exe", "msedge.exe", "samsunginternet.exe"],
        [
            ("檢視",
            [
                Key("放大", "Ctrl+=", 0xE8A3, Ctrl, Plus),
                Key("縮小", "Ctrl+-", 0xE71F, Ctrl, Minus),
                Key("實際大小", "Ctrl+0", 0xE71E, Ctrl, '0'),
                Key("全螢幕", "F11", 0xE740, F11),
                Key("重新整理", "F5", 0xE72C, F5),
            ]),
            ("歷史記錄",
            [
                Key("上一頁", "Alt+←", 0xE72B, Alt, Left),
                Key("下一頁", "Alt+→", 0xE72A, Alt, Right),
                Key("顯示所有歷史記錄", "Ctrl+H", 0xE81C, Ctrl, 'H'),
                Key("下載", "Ctrl+J", 0xE896, Ctrl, 'J'),
                Key("重新開啟關閉的分頁", "Ctrl+Shift+T", 0xE7A7, Ctrl, Shift, 'T'),
            ]),
            ("書籤",
            [
                Key("將此頁加入書籤", "Ctrl+D", 0xE734, Ctrl, 'D'),
                Key("書籤管理員", "Ctrl+Shift+O", 0xE728, Ctrl, Shift, 'O'),
                Key("顯示或隱藏書籤列", "Ctrl+Shift+B", 0xE8A0, Ctrl, Shift, 'B'),
            ]),
            ("分頁",
            [
                Key("新分頁", "Ctrl+T", 0xE710, Ctrl, 'T'),
                Key("關閉分頁", "Ctrl+W", 0xE711, Ctrl, 'W'),
                Key("下一個分頁", "Ctrl+Tab", 0xE72A, Ctrl, Tab),
                Key("上一個分頁", "Ctrl+Shift+Tab", 0xE72B, Ctrl, Shift, Tab),
                Key("新增無痕視窗", "Ctrl+Shift+N", 0xE727, Ctrl, Shift, 'N'),
            ]),
        ]),

        new("explorer", ["explorer.exe"],
        [
            ("檢視",
            [
                Key("超大圖示", "Ctrl+Shift+1", 0xE8B9, Ctrl, Shift, '1'),
                Key("大圖示", "Ctrl+Shift+2", 0xE8B9, Ctrl, Shift, '2'),
                Key("中圖示", "Ctrl+Shift+3", 0xE8B9, Ctrl, Shift, '3'),
                Key("小圖示", "Ctrl+Shift+4", 0xE8B9, Ctrl, Shift, '4'),
                Key("清單", "Ctrl+Shift+5", 0xE8FD, Ctrl, Shift, '5'),
                Key("詳細資料", "Ctrl+Shift+6", 0xE8FD, Ctrl, Shift, '6'),
                Key("並排", "Ctrl+Shift+7", 0xE8A9, Ctrl, Shift, '7'),
                Key("內容", "Ctrl+Shift+8", 0xE8A9, Ctrl, Shift, '8'),
                Key("預覽窗格", "Alt+P", 0xE8FF, Alt, 'P'),
                Key("詳細資料窗格", "Alt+Shift+P", 0xE946, Alt, Shift, 'P'),
                Key("重新整理", "F5", 0xE72C, F5),
            ]),
            ("移動",
            [
                Key("上一頁", "Alt+←", 0xE72B, Alt, Left),
                Key("下一頁", "Alt+→", 0xE72A, Alt, Right),
                Key("上一層", "Alt+↑", 0xE74A, Alt, Up),
                Folder("桌面", "shell:Desktop", 0xE7F4),
                Folder("下載", "shell:Downloads", 0xE896),
                Folder("文件", "shell:Personal", 0xE8A5),
                Folder("本機", "shell:MyComputerFolder", 0xE7F8),
                Folder("資源回收筒", "shell:RecycleBinFolder", 0xE74D),
            ]),
        ],
        FileMenu:
        [
            Key("新增視窗", "Ctrl+N", 0xE78B, Ctrl, 'N'),
            Key("新增分頁", "Ctrl+T", 0xE710, Ctrl, 'T'),
            Key("新增資料夾", "Ctrl+Shift+N", 0xE8F4, Ctrl, Shift, 'N'),
            Key("內容", "Alt+Enter", 0xE946, Alt, Enter),
            CloseWindow(),
        ]),

        new("vscode", ["code.exe"],
        [
            ("檢視",
            [
                Bound("workbench.action.showCommands", "命令選擇區", "ctrl+shift+p", 0xE756),
                Bound("workbench.view.explorer", "檔案總管", "ctrl+shift+e", 0xEC50),
                Bound("workbench.action.findInFiles", "搜尋", "ctrl+shift+f", 0xE721),
                Bound("workbench.view.scm", "原始檔控制", "ctrl+shift+g", 0xE8D4),
                Bound("workbench.view.extensions", "延伸模組", "ctrl+shift+x", 0xEA86),
                Bound("workbench.action.toggleSidebarVisibility", "顯示或隱藏側邊欄", "ctrl+b", 0xE8A0),
                Bound("workbench.action.terminal.toggleTerminal", "顯示或隱藏終端機", "ctrl+`", 0xE756),
                Bound("workbench.action.toggleFullScreen", "全螢幕", "f11", 0xE740),
                Bound("workbench.action.zoomIn", "放大", "ctrl+=", 0xE8A3),
                Bound("workbench.action.zoomOut", "縮小", "ctrl+-", 0xE71F),
            ]),
            ("移動",
            [
                Bound("workbench.action.quickOpen", "前往檔案⋯", "ctrl+p", 0xE8A5),
                Bound("workbench.action.gotoLine", "前往行⋯", "ctrl+g", 0xE8D2),
                Bound("workbench.action.gotoSymbol", "前往符號⋯", "ctrl+shift+o", 0xE943),
                Bound("editor.action.revealDefinition", "前往定義", "f12", 0xE8AD),
                Bound("workbench.action.navigateBack", "上一個位置", "alt+left", 0xE72B),
                Bound("workbench.action.navigateForward", "下一個位置", "alt+right", 0xE72A),
            ]),
        ],
        // Its own 檔案 and 編輯, so these follow the user's key settings too (no 列印: Ctrl+P is Go to File).
        FileMenu:
        [
            Bound("workbench.action.files.newUntitledFile", "新增", "ctrl+n", 0xE710),
            Bound("workbench.action.files.openFile", "開啟舊檔⋯", "ctrl+o", 0xE8E5),
            Bound("workbench.action.files.save", "儲存", "ctrl+s", 0xE74E),
            CloseWindow(),
        ],
        EditMenu:
        [
            Bound("undo", "復原", "ctrl+z", 0xE7A7),
            Bound("editor.action.clipboardCutAction", "剪下", "ctrl+x", 0xE8C6),
            Bound("editor.action.clipboardCopyAction", "複製", "ctrl+c", 0xE8C8),
            Bound("editor.action.clipboardPasteAction", "貼上", "ctrl+v", 0xE77F),
            Bound("editor.action.selectAll", "全選", "ctrl+a", 0xE8B3),
            Bound("actions.find", "尋找⋯", "ctrl+f", 0xE721),
            .. ShortcutMenuPage.EditMenu.Where(entry => entry.Section == "系統"),
        ]),

        new("word", ["winword.exe"],
        [
            ("插入",
            [
                Key("分頁符號", "Ctrl+Enter", 0xE8A5, Ctrl, Enter),
                Key("超連結⋯", "Ctrl+K", 0xE71B, Ctrl, 'K'),
            ]),
            ("格式", TextFormat(alignment: true)),
        ]),

        new("excel", ["excel.exe"],
        [
            ("插入",
            [
                Key("超連結⋯", "Ctrl+K", 0xE71B, Ctrl, 'K'),
                Key("今天的日期", "Ctrl+;", 0xE787, Ctrl, Semicolon),
                Key("現在的時間", "Ctrl+Shift+;", 0xE823, Ctrl, Shift, Semicolon),
            ]),

            // Excel's Ctrl+E is Flash Fill and Ctrl+L makes a table, so no alignment here.
            ("格式", TextFormat(alignment: false)),
            ("資料",
            [
                Key("篩選", "Ctrl+Shift+L", 0xE71C, Ctrl, Shift, 'L'),
            ]),
        ]),

        new("powerpoint", ["powerpnt.exe"],
        [
            ("插入",
            [
                Key("新增投影片", "Ctrl+M", 0xE710, Ctrl, 'M'),
                Key("超連結⋯", "Ctrl+K", 0xE71B, Ctrl, 'K'),
            ]),
            ("格式", TextFormat(alignment: true)),
            ("投影片放映",
            [
                Key("從頭開始", "F5", 0xE768, F5),
                Key("從目前投影片開始", "Shift+F5", 0xE768, Shift, F5),
            ]),
        ]),

        // Spotify's Ctrl+S is shuffle and Ctrl+P its settings, so it gets its own 檔案 menu.
        new("spotify", ["spotify.exe"],
        [
            ("播放",
            [
                Key("播放／暫停", "播放鍵", 0xE768, PlayPause),
                Key("下一首", "下一首鍵", 0xE893, NextTrack),
                Key("上一首", "上一首鍵", 0xE892, PreviousTrack),
                Key("隨機播放", "Ctrl+S", 0xE8B1, Ctrl, 'S'),
                Key("重複播放", "Ctrl+R", 0xE8EE, Ctrl, 'R'),
                Key("調高音量", "Ctrl+↑", 0xE995, Ctrl, Up),
                Key("調低音量", "Ctrl+↓", 0xE993, Ctrl, Down),
            ]),
        ],
        FileMenu:
        [
            Key("新增播放清單", "Ctrl+N", 0xE710, Ctrl, 'N'),
            CloseWindow(),
        ]),

        // Discord's Ctrl+P opens pinned messages, so it gets its own 檔案 menu.
        new("discord", ["discord.exe"],
        [
            ("移動",
            [
                Key("快速切換⋯", "Ctrl+K", 0xE721, Ctrl, 'K'),
                Key("上一個頻道", "Alt+↑", 0xE74A, Alt, Up),
                Key("下一個頻道", "Alt+↓", 0xE74B, Alt, Down),
                Key("上一個伺服器", "Ctrl+Alt+↑", 0xE74A, Ctrl, Alt, Up),
                Key("下一個伺服器", "Ctrl+Alt+↓", 0xE74B, Ctrl, Alt, Down),
            ]),
            ("通話",
            [
                Key("麥克風靜音", "Ctrl+Shift+M", 0xE720, Ctrl, Shift, 'M'),
                Key("拒聽", "Ctrl+Shift+D", 0xE74F, Ctrl, Shift, 'D'),
            ]),
        ],
        FileMenu:
        [
            Key("上傳檔案⋯", "Ctrl+Shift+U", 0xE898, Ctrl, Shift, 'U'),
            CloseWindow(),
        ]),

        // OneNote: Ctrl+N adds a page and Ctrl+S syncs, so 檔案 says so.
        new("onenote", ["onenote.exe"],
        [
            ("檢視",
            [
                Key("全頁檢視", "F11", 0xE740, F11),
                Key("停駐到桌面", "Ctrl+Alt+D", 0xE8A0, Ctrl, Alt, 'D'),
            ]),
            ("移動",
            [
                Key("搜尋所有筆記本", "Ctrl+E", 0xE721, Ctrl, 'E'),
                Key("上一頁", "Ctrl+PgUp", 0xE74A, Ctrl, PageUp),
                Key("下一頁", "Ctrl+PgDn", 0xE74B, Ctrl, PageDown),
            ]),
            ("格式",
            [
                .. TextFormat(alignment: false),
                Key("待辦事項標籤", "Ctrl+1", 0xE73A, Ctrl, '1'),
                Key("醒目提示", "Ctrl+Alt+H", 0xE7E6, Ctrl, Alt, 'H'),
                Key("插入日期", "Alt+Shift+D", 0xE787, Alt, Shift, 'D'),
            ]),
        ],
        FileMenu:
        [
            Key("新增頁面", "Ctrl+N", 0xE710, Ctrl, 'N'),
            Key("新增節", "Ctrl+T", 0xE710, Ctrl, 'T'),
            Key("開啟筆記本⋯", "Ctrl+O", 0xE8E5, Ctrl, 'O'),
            Key("同步此筆記本", "Ctrl+S", 0xE895, Ctrl, 'S'),
            Key("列印⋯", "Ctrl+P", 0xE749, Ctrl, 'P'),
            CloseWindow(),
        ]),

        // Outlook (both versions): Ctrl+F forwards, so the shared 編輯 leaves 尋找 out for it.
        new("outlook", ["outlook.exe"], OutlookMenus(search: true),
        FileMenu:
        [
            Key("新郵件", "Ctrl+Shift+M", 0xE715, Ctrl, Shift, 'M'),
            Key("新約會", "Ctrl+Shift+A", 0xE787, Ctrl, Shift, 'A'),
            Key("列印⋯", "Ctrl+P", 0xE749, Ctrl, 'P'),
            CloseWindow(),
        ]),
        new("newoutlook", ["olk.exe"], OutlookMenus(search: false),
        FileMenu:
        [
            Key("新增郵件或事件", "Ctrl+N", 0xE715, Ctrl, 'N'),
            CloseWindow(),
        ]),

        // Windows Terminal passes plain Ctrl shortcuts to the shell (Ctrl+C interrupts, Ctrl+Z suspends),
        // so 檔案 and 編輯 use its own Ctrl+Shift ones.
        new("terminal", ["windowsterminal.exe"],
        [
            ("檢視",
            [
                Bound("Terminal.ToggleCommandPalette", "命令選擇區", "ctrl+shift+p", 0xE756),
                Bound("Terminal.OpenSettingsUI", "設定", "ctrl+,", 0xE713),
                Bound("Terminal.IncreaseFontSize", "放大字型", "ctrl+plus", 0xE8A3),
                Bound("Terminal.DecreaseFontSize", "縮小字型", "ctrl+minus", 0xE71F),
                Bound("Terminal.ResetFontSize", "重設字型大小", "ctrl+0", 0xE71E),
                Bound("Terminal.ToggleFullscreen", "全螢幕", "f11", 0xE740),
            ]),
        ],
        FileMenu:
        [
            Bound("Terminal.OpenNewTab", "新索引標籤", "ctrl+shift+t", 0xE710),
            Bound("Terminal.OpenNewWindow", "新視窗", "ctrl+shift+n", 0xE78B),
            Bound("Terminal.DuplicatePaneAuto", "分割窗格", null, 0xE8A9), // No default keys: shown only if the user gave it some.
            CloseWindow(),
        ],
        EditMenu:
        [
            Bound("Terminal.CopyToClipboard", "複製", "ctrl+shift+c", 0xE8C8),
            Bound("Terminal.PasteFromClipboard", "貼上", "ctrl+shift+v", 0xE77F),
            Bound("Terminal.SelectAll", "全選", "ctrl+shift+a", 0xE8B3),
            Bound("Terminal.FindText", "尋找⋯", "ctrl+shift+f", 0xE721),
        ]),

        // GitHub Desktop's Ctrl+P pushes, so it gets its own 檔案 menu.
        new("githubdesktop", ["githubdesktop.exe"],
        [
            ("儲存庫",
            [
                Key("推送", "Ctrl+P", 0xE898, Ctrl, 'P'),
                Key("拉取", "Ctrl+Shift+P", 0xE896, Ctrl, Shift, 'P'),
                Key("擷取", "Ctrl+Shift+T", 0xE72C, Ctrl, Shift, 'T'),
                Key("在 GitHub 上檢視", "Ctrl+Shift+G", 0xE774, Ctrl, Shift, 'G'),
                Key("在檔案總管中顯示", "Ctrl+Shift+F", 0xEC50, Ctrl, Shift, 'F'),
            ]),
            ("分支",
            [
                Key("新增分支⋯", "Ctrl+Shift+N", 0xE710, Ctrl, Shift, 'N'),
                Key("從預設分支更新", "Ctrl+Shift+U", 0xE72C, Ctrl, Shift, 'U'),
                Key("建立或顯示提取要求", "Ctrl+R", 0xE8D4, Ctrl, 'R'),
            ]),
            ("檢視",
            [
                Key("變更", "Ctrl+1", 0xE70F, Ctrl, '1'),
                Key("歷程記錄", "Ctrl+2", 0xE81C, Ctrl, '2'),
                Key("分支清單", "Ctrl+B", 0xE8FD, Ctrl, 'B'),
            ]),
        ],
        FileMenu:
        [
            Key("新增儲存庫⋯", "Ctrl+N", 0xE710, Ctrl, 'N'),
            Key("加入本機儲存庫⋯", "Ctrl+O", 0xE8E5, Ctrl, 'O'),
            CloseWindow(),
        ]),

        // Postman's Ctrl+O imports rather than opens.
        new("postman", ["postman.exe"],
        [
            ("請求",
            [
                Key("傳送請求", "Ctrl+Enter", 0xE724, Ctrl, Enter),
            ]),
            ("檢視",
            [
                Key("搜尋 Postman", "Ctrl+K", 0xE721, Ctrl, 'K'),
                Key("顯示或隱藏側邊欄", "Ctrl+\\", 0xE8A0, Ctrl, Backslash),
                Key("主控台", "Ctrl+Alt+C", 0xE756, Ctrl, Alt, 'C'),
            ]),
        ],
        FileMenu:
        [
            Key("新增⋯", "Ctrl+N", 0xE710, Ctrl, 'N'),
            Key("新分頁", "Ctrl+T", 0xE710, Ctrl, 'T'),
            Key("匯入⋯", "Ctrl+O", 0xE8B5, Ctrl, 'O'),
            Key("儲存", "Ctrl+S", 0xE74E, Ctrl, 'S'),
            Key("另存新檔⋯", "Ctrl+Shift+S", 0xE792, Ctrl, Shift, 'S'),
            Key("關閉分頁", "Ctrl+W", 0xE711, Ctrl, 'W'),
            CloseWindow(),
        ]),

        // Obsidian's Ctrl+P is the command palette and Ctrl+O the quick switcher.
        new("obsidian", ["obsidian.exe"],
        [
            ("檢視",
            [
                Key("命令面板", "Ctrl+P", 0xE756, Ctrl, 'P'),
                Key("切換閱讀或編輯", "Ctrl+E", 0xE70F, Ctrl, 'E'),
                Key("關聯圖", "Ctrl+G", 0xE8A9, Ctrl, 'G'),
            ]),
            ("移動",
            [
                Key("全庫搜尋", "Ctrl+Shift+F", 0xE721, Ctrl, Shift, 'F'),
                Key("上一頁", "Ctrl+Alt+←", 0xE72B, Ctrl, Alt, Left),
                Key("下一頁", "Ctrl+Alt+→", 0xE72A, Ctrl, Alt, Right),
            ]),
            ("格式",
            [
                Key("粗體", "Ctrl+B", 0xE8DD, Ctrl, 'B'),
                Key("斜體", "Ctrl+I", 0xE8DB, Ctrl, 'I'),
                Key("切換核取方塊", "Ctrl+L", 0xE73A, Ctrl, 'L'),
            ]),
        ],
        FileMenu:
        [
            Key("新筆記", "Ctrl+N", 0xE710, Ctrl, 'N'),
            Key("快速切換⋯", "Ctrl+O", 0xE8E5, Ctrl, 'O'),
            Key("新分頁", "Ctrl+T", 0xE710, Ctrl, 'T'),
            Key("重新開啟關閉的分頁", "Ctrl+Shift+T", 0xE7A7, Ctrl, Shift, 'T'),
            CloseWindow(),
        ]),

        // Windows 11 Notepad: Ctrl+N opens a tab, and F5 inserts the time.
        new("notepad", ["notepad.exe"],
        [
            ("檢視",
            [
                Key("放大", "Ctrl+=", 0xE8A3, Ctrl, Plus),
                Key("縮小", "Ctrl+-", 0xE71F, Ctrl, Minus),
                Key("重設縮放", "Ctrl+0", 0xE71E, Ctrl, '0'),
            ]),
        ],
        FileMenu:
        [
            Key("新索引標籤", "Ctrl+N", 0xE710, Ctrl, 'N'),
            Key("新視窗", "Ctrl+Shift+N", 0xE78B, Ctrl, Shift, 'N'),
            Key("開啟舊檔⋯", "Ctrl+O", 0xE8E5, Ctrl, 'O'),
            Key("儲存", "Ctrl+S", 0xE74E, Ctrl, 'S'),
            Key("另存新檔⋯", "Ctrl+Shift+S", 0xE792, Ctrl, Shift, 'S'),
            Key("全部儲存", "Ctrl+Alt+S", 0xE74E, Ctrl, Alt, 'S'),
            Key("列印⋯", "Ctrl+P", 0xE749, Ctrl, 'P'),
            Key("關閉索引標籤", "Ctrl+W", 0xE711, Ctrl, 'W'),
            CloseWindow(),
        ],
        EditMenu:
        [
            .. ShortcutMenuPage.EditMenu,
            Key("取代⋯", "Ctrl+H", 0xE8AB, Ctrl, 'H'),
            Key("移至行⋯", "Ctrl+G", 0xE8D2, Ctrl, 'G'),
            Key("插入時間／日期", "F5", 0xE787, F5),
        ]),

        // LINE, from its own Settings > 快捷鍵 page, minus 記事本 (Ctrl+N), which that page still lists but
        // LINE has since removed. Its Ctrl+O sends a file to the open chat and Ctrl+P takes a screenshot,
        // so 檔案 is LINE's own and says exactly that.
        new("line", ["line.exe"],
        [
            ("移動",
            [
                Key("好友", "Ctrl+1", 0xE77B, Ctrl, '1'),
                Key("聊天", "Ctrl+2", 0xE8BD, Ctrl, '2'),
                Key("加入好友", "Ctrl+3", 0xE8FA, Ctrl, '3'),
                Key("搜尋好友和聊天⋯", "Ctrl+Shift+F", 0xE721, Ctrl, Shift, 'F'),
                Key("上一個聊天室", "Ctrl+Tab", 0xE72B, Ctrl, Tab),
                Key("聊天室最下方", "Ctrl+↓", 0xE74B, Ctrl, Down),
            ]),
            ("聊天",
            [
                Key("開啟貼圖視窗", "Ctrl+E", 0xE76E, Ctrl, 'E'),
                Key("開啟 Keep 筆記", "Ctrl+K", 0xE734, Ctrl, 'K'),
                Key("排列聊天室", "Ctrl+Alt+T", 0xE8A9, Ctrl, Alt, 'T'),
                Key("取消排列聊天室", "Ctrl+Alt+Shift+T", 0xE8A9, Ctrl, Alt, Shift, 'T'),
            ]),
            ("格式",
            [
                Key("粗體", "Ctrl+B", 0xE8DD, Ctrl, 'B'),
                Key("斜體", "Ctrl+I", 0xE8DB, Ctrl, 'I'),
                Key("刪除線", "Ctrl+Shift+X", 0xEDE0, Ctrl, Shift, 'X'),
                Key("紅字框", "Ctrl+Shift+C", 0xE8D2, Ctrl, Shift, 'C'),
                Key("段落框", "Ctrl+Shift+D", 0xE8E9, Ctrl, Shift, 'D'),
            ]),
            ("通話",
            [
                Key("開啟或關閉麥克風", "Ctrl+Shift+A", 0xE720, Ctrl, Shift, 'A'),
                Key("開啟或關閉相機", "Ctrl+Shift+V", 0xE714, Ctrl, Shift, 'V'),
            ]),
        ],
        FileMenu:
        [
            Key("傳送檔案到目前聊天⋯", "Ctrl+O", 0xE898, Ctrl, 'O'),
            Key("截取畫面", "Ctrl+Shift+P", 0xE722, Ctrl, Shift, 'P'),
            Key("啟用鎖定模式", "Ctrl+L", 0xE72E, Ctrl, 'L'),
            Key("關閉視窗", "Ctrl+W", 0xE8BB, Ctrl, 'W'),
        ]),

        // Teams: Ctrl+S in an event form sends the invitation, so 檔案 sticks to a new chat.
        new("teams", ["ms-teams.exe"],
        [
            ("移動",
            [
                Key("搜尋", "Ctrl+E", 0xE721, Ctrl, 'E'),
                Key("跳至聊天或頻道⋯", "Ctrl+G", 0xE8BD, Ctrl, 'G'),
                Key("快捷鍵清單", "Ctrl+.", 0xE765, Ctrl, Period),
            ]),
            ("會議",
            [
                Key("麥克風靜音", "Ctrl+Shift+M", 0xE720, Ctrl, Shift, 'M'),
                Key("開關視訊", "Ctrl+Shift+O", 0xE714, Ctrl, Shift, 'O'),
                Key("舉手或放下", "Ctrl+Shift+K", 0xE768, Ctrl, Shift, 'K'),
                Key("結束通話", "Ctrl+Shift+H", 0xE778, Ctrl, Shift, 'H'),
            ]),
        ],
        FileMenu:
        [
            Key("新聊天", "Ctrl+N", 0xE710, Ctrl, 'N'),
            CloseWindow(),
        ]),

        // Photos: no new or open, and Ctrl+S saves a copy.
        new("photos", ["photos.exe"],
        [
            ("影像",
            [
                Key("旋轉", "Ctrl+R", 0xE7AD, Ctrl, 'R'),
                Key("分享", "Alt+↑", 0xE72D, Alt, Up),
            ]),
            ("檢視",
            [
                Key("全螢幕", "F11", 0xE740, F11),
                Key("放大", "Ctrl+=", 0xE8A3, Ctrl, Plus),
                Key("縮小", "Ctrl+-", 0xE71F, Ctrl, Minus),
                Key("符合視窗", "Ctrl+0", 0xE9A6, Ctrl, '0'),
                Key("檔案資訊", "Alt+Enter", 0xE946, Alt, Enter),
            ]),
        ],
        FileMenu:
        [
            Key("另存新檔⋯", "Ctrl+S", 0xE792, Ctrl, 'S'),
            Key("列印⋯", "Ctrl+P", 0xE749, Ctrl, 'P'),
            CloseWindow(),
        ],
        EditMenu:
        [
            Key("複製", "Ctrl+C", 0xE8C8, Ctrl, 'C'),
        ]),

        // Media Player: Ctrl+P plays or pauses and Ctrl+F skips ahead, so the shared 檔案 and 編輯
        // would play music instead of printing or finding. It has no 編輯 at all.
        new("mediaplayer", ["microsoft.media.player.exe"],
        [
            ("播放",
            [
                Key("播放／暫停", "Ctrl+P", 0xE768, Ctrl, 'P'),
                Key("下一首", "Ctrl+F", 0xE893, Ctrl, 'F'),
                Key("上一首", "Ctrl+B", 0xE892, Ctrl, 'B'),
                Key("隨機播放", "Ctrl+H", 0xE8B1, Ctrl, 'H'),
                Key("重複播放", "Ctrl+T", 0xE8EE, Ctrl, 'T'),
            ]),
            ("檢視",
            [
                Key("全螢幕", "F11", 0xE740, F11),
                Key("迷你播放程式", "Ctrl+M", 0xE944, Ctrl, 'M'),
            ]),
        ],
        FileMenu:
        [
            Key("開啟檔案⋯", "Ctrl+O", 0xE8E5, Ctrl, 'O'),
            Key("開啟資料夾⋯", "Ctrl+Shift+O", 0xE8B7, Ctrl, Shift, 'O'),
            Key("開啟網址⋯", "Ctrl+U", 0xE71B, Ctrl, 'U'),
            CloseWindow(),
        ],
        EditMenu: []),

        new("paint", ["mspaint.exe"],
        [
            ("影像",
            [
                Key("影像內容⋯", "Ctrl+E", 0xE946, Ctrl, 'E'),
            ]),
            ("檢視",
            [
                Key("放大", "Ctrl+PgUp", 0xE8A3, Ctrl, PageUp),
                Key("縮小", "Ctrl+PgDn", 0xE71F, Ctrl, PageDown),
                Key("100%", "Ctrl+0", 0xE71E, Ctrl, '0'),
                Key("符合視窗", "Ctrl+1", 0xE9A6, Ctrl, '1'),
                Key("尺規", "Ctrl+R", 0xE7F4, Ctrl, 'R'),
                Key("格線", "Ctrl+G", 0xE80A, Ctrl, 'G'),
                Key("全螢幕", "F11", 0xE740, F11),
            ]),
        ]),

        // Claude: shortcuts read from the installed app's own menus and keyboard-shortcut screen.
        new("claude", ["claude.exe"],
        [
            ("會話",
            [
                Key("搜尋對話⋯", "Ctrl+Shift+K", 0xE721, Ctrl, Shift, 'K'),
                Key("上一個對話", "Ctrl+Shift+Tab", 0xE72B, Ctrl, Shift, Tab),
                Key("下一個對話", "Ctrl+Tab", 0xE72A, Ctrl, Tab),
                Key("切換延伸思考", "Ctrl+Shift+E", 0xEA80, Ctrl, Shift, 'E'),
                Key("選擇模型⋯", "Ctrl+Shift+.", 0xE8AB, Ctrl, Shift, Period),
            ]),
            ("檢視",
            [
                Key("命令面板", "Ctrl+K", 0xE756, Ctrl, 'K'),
                Key("顯示或隱藏側邊欄", "Ctrl+B", 0xE8A0, Ctrl, 'B'),
                Key("放大", "Ctrl+=", 0xE8A3, Ctrl, Plus),
                Key("縮小", "Ctrl+-", 0xE71F, Ctrl, Minus),
                Key("實際大小", "Ctrl+0", 0xE71E, Ctrl, '0'),
                Key("全螢幕", "F11", 0xE740, F11),
            ]),
            ("移動",
            [
                Key("上一頁", "Alt+←", 0xE72B, Alt, Left),
                Key("下一頁", "Alt+→", 0xE72A, Alt, Right),
                Key("設定", "Ctrl+,", 0xE713, Ctrl, Comma),
                Key("鍵盤快速鍵", "Ctrl+/", 0xE765, Ctrl, Slash),
            ]),
        ],
        FileMenu:
        [
            Key("新對話", "Ctrl+N", 0xE710, Ctrl, 'N'),
            Key("上傳檔案⋯", "Ctrl+U", 0xE898, Ctrl, 'U'),
            CloseWindow(),
        ]),

        // ChatGPT (with Codex): Ctrl+P searches files and Ctrl+O opens a Codex folder.
        new("chatgpt", ["chatgpt.exe"],
        [
            ("會話",
            [
                Key("搜尋⋯", "Ctrl+K", 0xE721, Ctrl, 'K'),
                Key("選擇模型⋯", "Ctrl+Shift+M", 0xE8AB, Ctrl, Shift, 'M'),
                Key("重新命名對話", "Ctrl+Alt+R", 0xE8AC, Ctrl, Alt, 'R'),
                Key("釘選或取消釘選", "Ctrl+Alt+P", 0xE718, Ctrl, Alt, 'P'),
                Key("複製最後一個程式碼區塊", "Ctrl+Shift+;", 0xE8C8, Ctrl, Shift, Semicolon),
            ]),
            ("檢視",
            [
                Key("顯示或隱藏側邊欄", "Ctrl+B", 0xE8A0, Ctrl, 'B'),
                Key("放大", "Ctrl+=", 0xE8A3, Ctrl, Plus),
                Key("縮小", "Ctrl+-", 0xE71F, Ctrl, Minus),
                Key("實際大小", "Ctrl+0", 0xE71E, Ctrl, '0'),
                Key("全螢幕", "F11", 0xE740, F11),
            ]),
            ("移動",
            [
                Key("上一頁", "Ctrl+[", 0xE72B, Ctrl, BracketLeft),
                Key("下一頁", "Ctrl+]", 0xE72A, Ctrl, BracketRight),
                Key("Chat", "Alt+1", 0xE8BD, Alt, '1'),
                Key("Work", "Alt+2", 0xE821, Alt, '2'),
                Key("Codex", "Alt+3", 0xE943, Alt, '3'),
                Key("設定", "Ctrl+,", 0xE713, Ctrl, Comma),
                Key("鍵盤快速鍵", "Ctrl+/", 0xE765, Ctrl, Slash),
            ]),
        ],
        FileMenu:
        [
            Key("新對話", "Ctrl+N", 0xE710, Ctrl, 'N'),
            Key("新暫時對話", "Ctrl+Shift+N", 0xE710, Ctrl, Shift, 'N'),
            Key("開啟 Codex 資料夾⋯", "Ctrl+O", 0xE8B7, Ctrl, 'O'),
            CloseWindow(),
        ]),

        // Snipping Tool: Ctrl+N starts a capture straight away, and only undo, redo and copy are bound.
        new("snippingtool", ["snippingtool.exe"],
        [
            ("截圖",
            [
                Key("剪取畫面", "Win+Shift+S", 0xE722, Keys.LeftWindows, Shift, 'S'),
                Key("錄製畫面", "Win+Shift+R", 0xE714, Keys.LeftWindows, Shift, 'R'),
                Key("快速標記", "Ctrl+E", 0xE70F, Ctrl, 'E'),
            ]),
        ],
        FileMenu:
        [
            Key("新增截圖", "Ctrl+N", 0xE710, Ctrl, 'N'),
            Key("開啟檔案⋯", "Ctrl+O", 0xE8E5, Ctrl, 'O'),
            Key("另存新檔⋯", "Ctrl+S", 0xE792, Ctrl, 'S'),
            CloseWindow(),
        ],
        EditMenu:
        [
            Key("復原", "Ctrl+Z", 0xE7A7, Ctrl, 'Z'),
            Key("重做", "Ctrl+Y", 0xE7A6, Ctrl, 'Y'),
            Key("複製", "Ctrl+C", 0xE8C8, Ctrl, 'C'),
        ]),

        // VirtualBox Manager (not a running VM's window, which is VirtualBoxVM.exe): Ctrl+O clones a VM,
        // Ctrl+A adds one, Ctrl+S opens its settings and Ctrl+P the cloud tools, so it has no shared
        // 檔案 or 編輯. None of its shortcuts start, pause or stop a VM.
        new("virtualbox", ["virtualbox.exe"],
        [
            ("機器",
            [
                Key("新增虛擬機器⋯", "Ctrl+N", 0xE710, Ctrl, 'N'),
                Key("加入現有虛擬機器⋯", "Ctrl+A", 0xE8F4, Ctrl, 'A'),
                Key("設定⋯", "Ctrl+S", 0xE713, Ctrl, 'S'),
                Key("再製⋯", "Ctrl+O", 0xE8C8, Ctrl, 'O'),
                Key("搜尋", "Ctrl+F", 0xE721, Ctrl, 'F'),
                Key("顯示日誌", "Ctrl+L", 0xE81C, Ctrl, 'L'),
            ]),
            ("工具",
            [
                Key("媒體", "Ctrl+D", 0xEDA2, Ctrl, 'D'),
                Key("網路", "Ctrl+H", 0xE968, Ctrl, 'H'),
                Key("擴充功能", "Ctrl+T", 0xEA86, Ctrl, 'T'),
                Key("雲端", "Ctrl+P", 0xE753, Ctrl, 'P'),
            ]),
        ],
        FileMenu:
        [
            Key("喜好設定⋯", "Ctrl+G", 0xE713, Ctrl, 'G'),
            Key("匯入應用裝置⋯", "Ctrl+I", 0xE8B5, Ctrl, 'I'),
            Key("匯出應用裝置⋯", "Ctrl+E", 0xEDE1, Ctrl, 'E'),
            CloseWindow(),
        ],
        EditMenu: []),

        // Phone Link: Ctrl+P starts mirroring the phone's screen.
        new("phonelink", ["phoneexperiencehost.exe"],
        [
            ("移動",
            [
                Key("訊息", "Ctrl+1", 0xE8BD, Ctrl, '1'),
                Key("相片", "Ctrl+2", 0xE91B, Ctrl, '2'),
                Key("應用程式", "Ctrl+3", 0xE71D, Ctrl, '3'),
                Key("通訊記錄", "Ctrl+4", 0xE717, Ctrl, '4'),
                Key("設定", "Ctrl+,", 0xE713, Ctrl, Comma),
                Key("鍵盤快速鍵", "Ctrl+.", 0xE765, Ctrl, Period),
            ]),
            ("手機",
            [
                Key("重新整理", "F5", 0xE72C, F5),
                Key("開啟手機螢幕", "Ctrl+P", 0xE8EA, Ctrl, 'P'),
                Key("勿打擾", "Ctrl+Shift+D", 0xE708, Ctrl, Shift, 'D'),
                Key("音訊播放程式", "Ctrl+Shift+A", 0xE8D6, Ctrl, Shift, 'A'),
            ]),
            ("通話",
            [
                Key("靜音或取消靜音", "Ctrl+Shift+M", 0xE720, Ctrl, Shift, 'M'),
                Key("結束通話", "Ctrl+Shift+H", 0xE778, Ctrl, Shift, 'H'),
            ]),
        ],
        FileMenu:
        [
            Key("新訊息", "Ctrl+N", 0xE710, Ctrl, 'N'),
            CloseWindow(),
        ]),

        // Docker Desktop's dashboard, from the installed version's own shortcut table. Its Ctrl+Q quits
        // Docker Desktop outright, stopping the engine and every running container, so it's left out;
        // closing the window only hides the dashboard. Ctrl+N, Ctrl+O and Ctrl+P do nothing here.
        new("docker", ["docker desktop.exe"],
        [
            ("檢視",
            [
                Key("搜尋⋯", "Ctrl+K", 0xE721, Ctrl, 'K'),
                Key("顯示或隱藏終端機", "Ctrl+Shift+`", 0xE756, Ctrl, Shift, Backtick),
                Key("放大", "Ctrl+=", 0xE8A3, Ctrl, Plus),
                Key("縮小", "Ctrl+-", 0xE71F, Ctrl, Minus),
                Key("實際大小", "Ctrl+0", 0xE71E, Ctrl, '0'),
            ]),
        ],
        FileMenu:
        [
            Key("設定⋯", "Ctrl+,", 0xE713, Ctrl, Comma),
            CloseWindow(),
        ]),

        // Microsoft Store has no shortcuts of its own (and closing it only minimizes it), so its menu
        // opens the Store's own links instead.
        new("store", ["winstore.app.exe"],
        [
            ("移動",
            [
                Link("首頁", "ms-windows-store://home", 0xE80F),
                Link("媒體櫃與更新", "ms-windows-store://downloadsandupdates", 0xE896),
                Link("設定", "ms-windows-store://settings", 0xE713),
            ]),
        ],
        FileMenu: []),

        // Calculator: Ctrl+N, Ctrl+O and Ctrl+S put e^x, cosh and sinh into the result and Ctrl+P adds it
        // to memory, so there's no shared 檔案, and 編輯 is just copy and paste.
        new("calculator", ["calculatorapp.exe"],
        [
            ("記憶",
            [
                Key("存入記憶", "Ctrl+M", 0xE74E, Ctrl, 'M'),
                Key("讀取記憶", "Ctrl+R", 0xE8E5, Ctrl, 'R'),
                Key("記憶加上目前數值", "Ctrl+P", 0xE710, Ctrl, 'P'),
                Key("記憶減去目前數值", "Ctrl+Q", 0xE738, Ctrl, 'Q'),
                Key("清除所有記憶", "Ctrl+L", 0xE74D, Ctrl, 'L'),
            ]),
            ("檢視",
            [
                Key("歷程記錄", "Ctrl+H", 0xE81C, Ctrl, 'H'),
                Key("清除歷程記錄", "Ctrl+Shift+D", 0xE74D, Ctrl, Shift, 'D'),
                Key("迷你模式（保持在最上層）", "Alt+↑", 0xE718, Alt, Up),
                Key("回到完整檢視", "Alt+↓", 0xE740, Alt, Down),
            ]),
        ],
        FileMenu: [CloseWindow()],
        EditMenu:
        [
            Key("複製", "Ctrl+C", 0xE8C8, Ctrl, 'C'),
            Key("貼上", "Ctrl+V", 0xE77F, Ctrl, 'V'),
        ]),

        // Samsung Notes, from its own shortcut list (F1). Ctrl+N adds a page while a note is open, printing
        // is Ctrl+Shift+P (Ctrl+P does nothing), and notes save themselves.
        new("samsungnotes", ["samsungnotes.exe"],
        [
            ("格式",
            [
                .. TextFormat(alignment: true),
                Key("刪除線", "Ctrl+/", 0xEDE0, Ctrl, Slash),
                Key("編號清單", "Ctrl+Shift+7", 0xE8FD, Ctrl, Shift, '7'),
                Key("項目符號清單", "Ctrl+Shift+8", 0xE8FD, Ctrl, Shift, '8'),
                Key("核取清單", "Ctrl+Shift+C", 0xE9D5, Ctrl, Shift, 'C'),
                Key("增加縮排", "Ctrl+M", 0xE291, Ctrl, 'M'),
                Key("減少縮排", "Ctrl+Shift+M", 0xE290, Ctrl, Shift, 'M'),
                Key("放大字型", "Ctrl+]", 0xE8E8, Ctrl, BracketRight),
                Key("縮小字型", "Ctrl+[", 0xE8E7, Ctrl, BracketLeft),
            ]),
            ("檢視",
            [
                Key("放大", "Ctrl+=", 0xE8A3, Ctrl, Plus),
                Key("縮小", "Ctrl+-", 0xE71F, Ctrl, Minus),
                Key("鎖定縮放", "Ctrl+Alt+L", 0xE72E, Ctrl, Alt, 'L'),
                Key("切換唯讀模式", "Alt+Enter", 0xE890, Alt, Enter),
                Key("全螢幕", "Alt+F", 0xE740, Alt, 'F'),
            ]),
            ("工具",
            [
                Key("建立文字方塊", "Ctrl+Shift+T", 0xE70F, Ctrl, Shift, 'T'),
                Key("筆設定", "Ctrl+T", 0xE713, Ctrl, 'T'),
                Key("頁面設定", "Alt+P", 0xE713, Alt, 'P'),
                Key("錄音面板", "Alt+R", 0xE720, Alt, 'R'),
                Key("播放或暫停錄音", "Ctrl+'", 0xE768, Ctrl, Quote),
                Key("倒轉 10 秒", "Ctrl+,", 0xE892, Ctrl, Comma),
                Key("快轉 10 秒", "Ctrl+.", 0xE893, Ctrl, Period),
            ]),
        ],
        FileMenu:
        [
            Key("新增筆記或頁面", "Ctrl+N", 0xE710, Ctrl, 'N'),
            Key("列印⋯", "Ctrl+Shift+P", 0xE749, Ctrl, Shift, 'P'),
            CloseWindow(),
        ]),

        // Samsung Gallery (媒體瀏覽器), from the app's own key bindings: Ctrl+N makes a movie from the
        // selected items and Ctrl+V copies files into the open album. Deleting asks first.
        new("gallery", ["galleryapp.exe"],
        [
            ("相片",
            [
                Key("選取或編輯", "Ctrl+E", 0xE70F, Ctrl, 'E'),
                Key("詳細資料", "Ctrl+Shift+M", 0xE946, Ctrl, Shift, 'M'),
                Key("更多選項", "Ctrl+M", 0xE712, Ctrl, 'M'),
                Key("用選取的項目製作影片", "Ctrl+N", 0xE714, Ctrl, 'N'),
                Key("刪除⋯", "Ctrl+D", 0xE74D, Ctrl, 'D'),
            ]),
            ("檢視",
            [
                Key("上一張", "←", 0xE72B, Left),
                Key("下一張", "→", 0xE72A, Right),
                Key("全螢幕", "F11", 0xE740, F11),
            ]),
        ],
        FileMenu:
        [
            Key("列印⋯", "Ctrl+P", 0xE749, Ctrl, 'P'),
            CloseWindow(),
        ],
        EditMenu:
        [
            Key("複製", "Ctrl+C", 0xE8C8, Ctrl, 'C'),
            Key("貼上到這個相簿", "Ctrl+V", 0xE77F, Ctrl, 'V'),
            Key("全選", "Ctrl+A", 0xE8B3, Ctrl, 'A'),
        ]),

        // Settings-style apps have nothing to open, save or print, so 檔案 only closes the window.
        // Their search boxes still take 編輯's copy, paste and find.
        new("settingsapps",
        [
            "systemsettings.exe", "samsungsettingshost.exe", "ivedevicecareclient.exe", "nearbydevices.exe",
            "intelgraphicssoftware.exe", "quickshare.exe", "smartthings.exe", "samsungpass.exe", "feedbackhub.exe",
            "sechealthui.exe", "samsungflow.exe", "phone.exe", "samsungcamerashare.exe", "samsungstorageshare.exe",
            "stickynotesstub.exe", // Sticky Notes 4's package holds only this launcher and upgrade screen.
        ],
        [],
        FileMenu: [CloseWindow()]),

        // Bulk Crap Uninstaller, with the hotkeys from its source (MainWindow.SetupHotkeys). The ones on an
        // app act on what's selected in its list, and BCU takes them only while that list has the focus, so
        // they're sent only then: Delete in its search box or a file dialog would do something else.
        // Uninstalling asks first. Ctrl+B exports the selected apps' uninstall registry keys to a .reg file.
        new("bcuninstaller", ["bcuninstaller.exe"],
        [
            ("解除安裝",
            [
                ListKey("解除安裝⋯", "Delete", 0xE74D, Delete),
                ListKey("靜默解除安裝⋯", "Shift+Delete", 0xE74D, Shift, Delete),
                ListKey("手動解除安裝⋯", "Ctrl+Shift+Delete", 0xE74D, Ctrl, Shift, Delete),
                ListKey("內容", "Alt+Enter", 0xE946, Alt, Enter),
                ListKey("備份登錄資料⋯", "Ctrl+B", 0xE777, Ctrl, 'B'),
                ListKey("在登錄編輯程式開啟", "Ctrl+R", 0xE8A7, Ctrl, 'R'),
            ]),
            ("檢視",
            [
                Key("重新整理清單", "F5", 0xE72C, F5),
                Key("搜尋⋯", "Ctrl+F", 0xE721, Ctrl, 'F'),
                Key("設定⋯", "Ctrl+P", 0xE713, Ctrl, 'P'),
            ]),
        ],
        FileMenu:
        [
            Key("載入清單⋯", "Ctrl+O", 0xE8E5, Ctrl, 'O'),
            CloseWindow(),
        ],
        EditMenu:
        [
            ListKey("複製完整資訊", "Ctrl+C", 0xE8C8, Ctrl, 'C'),
            Key("搜尋⋯", "Ctrl+F", 0xE721, Ctrl, 'F'),
        ]),

        // PowerToys Settings and Samsung Multi Control also go back a page with Alt+←.
        new("settingsback", ["powertoys.settings.exe", "multicontrolwinui.exe"],
        [
            ("移動", [Key("上一頁", "Alt+←", 0xE72B, Alt, Left)]),
        ],
        FileMenu: [CloseWindow()]),

        // Git Bash's terminal, mintty, with its default shortcuts (mintty's manual). Ctrl+C would interrupt
        // what's running, so copy and paste are Ctrl+Insert and Shift+Insert; closing ends the shell, so
        // 檔案 only opens another window.
        new("mintty", ["mintty.exe"],
        [
            ("檢視",
            [
                Key("放大字型", "Ctrl+數字鍵 +", 0xE8A3, Ctrl, NumpadPlus),
                Key("縮小字型", "Ctrl+數字鍵 -", 0xE71F, Ctrl, NumpadMinus),
                Key("原始字型大小", "Ctrl+0", 0xE71E, Ctrl, '0'),
                Key("預設視窗大小", "Alt+F10", 0xE9A6, Alt, F10),
                Key("全螢幕", "Alt+F11", 0xE740, Alt, F11),
            ]),
            ("終端機",
            [
                Key("下一個視窗", "Ctrl+Tab", 0xE72A, Ctrl, Tab),
                Key("重設⋯", "Alt+F8", 0xE72C, Alt, F8),
            ]),
        ],
        FileMenu: [Key("新視窗", "Alt+F2", 0xE78B, Alt, F2)],
        EditMenu:
        [
            Key("複製", "Ctrl+Insert", 0xE8C8, Ctrl, Insert),
            Key("貼上", "Shift+Insert", 0xE77F, Shift, Insert),
            Key("搜尋⋯", "Alt+F3", 0xE721, Alt, F3),
        ]),
    ];

    /// <summary>
    /// Old-style console windows (cmd or PowerShell outside Windows Terminal), whatever program runs
    /// in them. No keys at all: Ctrl+C would interrupt the running program and Ctrl+S pause it, so
    /// 編輯 uses the console's own system-menu commands, and there's no 檔案 (closing ends the program).
    /// </summary>
    public static readonly AppMenuSet Console = new("console", [], [],
        FileMenu: [],
        EditMenu:
        [
            ConsoleCommand("標記", 0xFFF2, 0xE8B3),
            ConsoleCommand("複製選取的文字", 0xFFF0, 0xE8C8),
            ConsoleCommand("貼上", 0xFFF1, 0xE77F),
            ConsoleCommand("全選", 0xFFF5, 0xE8B3),
            ConsoleCommand("捲動", 0xFFF3, 0xE74B),
            ConsoleCommand("尋找⋯", 0xFFF4, 0xE721),
            ConsoleCommand("內容⋯", 0xFFF7, 0xE713),
        ]);

    public static AppMenuSet? For(string exe) =>
        Sets.FirstOrDefault(set => set.Exes.Contains(exe, StringComparer.OrdinalIgnoreCase));

    private static MenuEntry Key(string title, string shortcut, int glyph, params ushort[] chord) =>
        new(title, shortcut, glyph, app => Keys.SendTo(app, chord));

    // A command of an app whose shortcuts the user can change, with its default keys in that app's notation
    // (null if it has none). The menu shows and presses whatever the user's settings make of it (KeyBindings).
    private static MenuEntry Bound(string command, string title, string? keys, int glyph)
    {
        var parsed = keys is null ? null : KeyBindings.Parse(keys);
        return new(title, parsed?.Display ?? string.Empty, glyph, app =>
        {
            if (parsed is not null)
            {
                Keys.SendTo(app, parsed.Chords);
            }
        })
        {
            Command = command,
            DefaultKeys = parsed?.Text,
        };
    }

    // A key Bulk Crap Uninstaller takes only while the app list in its main window has the focus; not sent
    // otherwise. Its main window's title starts with the program's name (every translation keeps the English
    // one except Arabic, which has its own), and its other windows (leftover removal, for one) have lists too.
    private static MenuEntry ListKey(string title, string shortcut, int glyph, params ushort[] chord) =>
        new(title, shortcut, glyph, app => Keys.SendTo(app, () => Keys.FocusIsListIn("Bulk Crap Uninstaller", "حماقة  سائبة الغاء التثبيت"), chord));

    // Opens the folder in a new window; typing into the current window's address bar is too fragile.
    private static MenuEntry Folder(string title, string path, int glyph) =>
        new(title, "新視窗", glyph, _ => Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }));

    // A link the app handles itself, for apps with no shortcut to press.
    private static MenuEntry Link(string title, string uri, int glyph) =>
        new(title, string.Empty, glyph, _ => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }));

    // Same in classic and new Outlook, except that only classic documents Ctrl+E for searching mail.
    private static (string Title, MenuEntry[] Entries)[] OutlookMenus(bool search) =>
    [
        ("郵件",
        [
            Key("回覆", "Ctrl+R", 0xE8CA, Ctrl, 'R'),
            Key("全部回覆", "Ctrl+Shift+R", 0xE8C2, Ctrl, Shift, 'R'),
            Key("轉寄", "Ctrl+F", 0xE89C, Ctrl, 'F'),
            Key("標為已讀", "Ctrl+Q", 0xE8C3, Ctrl, 'Q'),
            Key("標為未讀", "Ctrl+U", 0xE715, Ctrl, 'U'),
        ]),
        ("移動",
        [
            Key("郵件", "Ctrl+1", 0xE715, Ctrl, '1'),
            Key("行事曆", "Ctrl+2", 0xE787, Ctrl, '2'),
            Key("人員", "Ctrl+4", 0xE77B, Ctrl, '4'),
            .. search ? new[] { Key("搜尋", "Ctrl+E", 0xE721, Ctrl, 'E') } : Array.Empty<MenuEntry>(),
        ]),
    ];

    // A command from the console window's own system menu (Edit submenu), sent as WM_SYSCOMMAND.
    private static MenuEntry ConsoleCommand(string title, int command, int glyph) =>
        new(title, string.Empty, glyph, app =>
        {
            if (AppWindows.StillBelongsTo(app.Window, app))
            {
                AppWindows.Activate(app.Window);
                Native.PostMessage(app.Window, 0x0112 /* WM_SYSCOMMAND */, command, IntPtr.Zero);
            }
        });

    private static MenuEntry CloseWindow() =>
        new("關閉視窗", "Alt+F4", 0xE8BB, app => AppWindows.Quit(AppWindows.StillOf(app, [app.Window])));

    private static MenuEntry[] TextFormat(bool alignment) =>
    [
        Key("粗體", "Ctrl+B", 0xE8DD, Ctrl, 'B'),
        Key("斜體", "Ctrl+I", 0xE8DB, Ctrl, 'I'),
        Key("底線", "Ctrl+U", 0xE8DC, Ctrl, 'U'),
        .. alignment
            ? new[]
            {
                Key("靠左對齊", "Ctrl+L", 0xE8E4, Ctrl, 'L'),
                Key("置中", "Ctrl+E", 0xE8E3, Ctrl, 'E'),
                Key("靠右對齊", "Ctrl+R", 0xE8E2, Ctrl, 'R'),
            }
            : Array.Empty<MenuEntry>(),
    ];
}
