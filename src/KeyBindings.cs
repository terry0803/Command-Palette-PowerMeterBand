using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerMeterBand;

/// <summary>
/// The shortcuts a user set in VS Code's keybindings.json or Windows Terminal's settings.json, so menu
/// items for those apps press what actually works there. Read only when a menu opens, and parsed again
/// only when the file has changed.
///
/// An item keeps its default keys while they still run its command: not unbound, and not taken by
/// another command. Otherwise it uses the keys the user gave that command, and if there are none it's
/// hidden. Defaults are kept even when the user added other keys: Terminal users often add Ctrl+C for
/// copy, which sends an interrupt when nothing is selected, while the default Ctrl+Shift+C never does.
/// </summary>
internal static class KeyBindings
{
    public sealed record Keys(string Text, string Display, ushort[][] Chords);

    /// <summary>One user setting about some keys (normalized).</summary>
    /// <param name="Command">The command the keys now run, if any.</param>
    /// <param name="Removes">"*" if the keys were unbound altogether, or the one command they no longer run.</param>
    /// <param name="Conditional">Only in some situations (a VS Code "when" clause): not relied on as a replacement.</param>
    internal sealed record Binding(string Keys, string? Command, string? Removes = null, bool Conditional = false);

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, (DateTime Written, List<Binding> Bindings)> Cache = [];

    internal static Keys? Resolve(List<Binding> bindings, string command, string? defaultKeys)
    {
        // A Terminal id the user redefined runs something else now, whatever its keys.
        if (bindings.Any(b => b.Keys == $"<redefined:{command}>"))
        {
            return null;
        }

        if (defaultKeys is not null && Parse(defaultKeys) is { } fallback
            && !bindings.Any(b => Takes(b, fallback.Text, command) || b.Keys == $"<all:{command}>"))
        {
            return fallback;
        }

        // Several VS Code profiles with their own keys: which one this window uses can't be told, so only
        // keys untouched in all of them are trusted, never one profile's replacement.
        if (bindings.Any(b => b.Keys == "<several-sources>"))
        {
            return null;
        }

        // The user's own unconditional keys for it, newest first, unless something later took them over.
        for (var i = bindings.Count - 1; i >= 0; i--)
        {
            var binding = bindings[i];
            if (binding.Command == command && !binding.Conditional
                && !bindings.Skip(i + 1).Any(b => Takes(b, binding.Keys, command))
                && Parse(binding.Keys) is { } keys)
            {
                return keys;
            }
        }

        return null;
    }

    // Whether a setting stops these keys from running this command: given to another command (in any
    // situation, to be safe), unbound, or removed from this very command.
    // A sequence that starts with these keys (Ctrl+B Ctrl+S against Ctrl+B), or that these keys start with,
    // takes them too: the app waits for the rest after the first part instead of running the shorter one.
    private static bool Takes(Binding b, string keys, string command) =>
        (b.Keys == keys && ((b.Command is not null && b.Command != command) || b.Removes == "*" || b.Removes == command))
        || (b.Command is not null
            && (b.Keys.StartsWith(keys + " ", StringComparison.Ordinal) || keys.StartsWith(b.Keys + " ", StringComparison.Ordinal)));

    public static List<Binding> UserBindings(ActiveApp app)
    {
        var path = Native.ProcessPath(app.ProcessId);
        return Path.GetFileName(path ?? string.Empty).ToLowerInvariant() switch
        {
            "code.exe" => VsCodeBindings(path!),
            "windowsterminal.exe" => Read(TerminalSettings(path!), ReadTerminal),
            _ => [],
        };
    }

    // VS Code: the default profile's keybindings, plus those of any profile that keeps its own (it can't
    // be told which profile a window uses). With more than one, a marker stops replacements being trusted.
    private static List<Binding> VsCodeBindings(string exe)
    {
        var user = VsCodeSettings(exe);
        var files = new List<string> { Path.Combine(user, "keybindings.json") };
        var profiles = Path.Combine(user, "profiles");
        if (Directory.Exists(profiles))
        {
            files.AddRange(Directory.EnumerateDirectories(profiles).Select(p => Path.Combine(p, "keybindings.json")).Where(File.Exists));
        }

        // The default profile counts even with no file of its own (no changes): a window using it would
        // otherwise get another profile's keys.
        var bindings = files.SelectMany(file => Read(file, ReadVsCode)).ToList();
        if (files.Count > 1)
        {
            bindings.Add(new Binding("<several-sources>", null));
        }

        return bindings;
    }

    private static List<Binding> Read(string file, Func<string, List<Binding>> read)
    {
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            var written = File.GetLastWriteTimeUtc(file);
            lock (Gate)
            {
                if (Cache.TryGetValue(file, out var cached) && cached.Written == written)
                {
                    return cached.Bindings;
                }
            }

            var bindings = read(File.ReadAllText(file));
            lock (Gate)
            {
                Cache[file] = (written, bindings);
            }

            return bindings;
        }
        catch
        {
            // Unreadable (being saved, say): the default keys stand.
            return [];
        }
    }

    // The text of each { ... } directly inside the file's outer [ ... ], skipping strings and comments, so
    // one mistake (a null, a stray comma, a missing closing bracket) doesn't hide the rules around it.
    private static IEnumerable<string> TopLevelObjects(string json)
    {
        var open = new Stack<char>(); // The brackets around the current position, innermost on top.
        var start = -1;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (c == '"')
            {
                for (i++; i < json.Length && json[i] != '"'; i++)
                {
                    if (json[i] == '\\')
                    {
                        i++;
                    }
                }
            }
            else if (c == '/' && i + 1 < json.Length && json[i + 1] == '/')
            {
                while (i < json.Length && json[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < json.Length && json[i + 1] == '*')
            {
                var close = json.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? json.Length : close + 1;
            }
            else if (c is '[' or '{')
            {
                // The file's root must be an array, as VS Code requires; anything else holds no rules.
                if (open.Count == 0 && c != '[')
                {
                    yield break;
                }

                if (c == '{' && open.Count == 1)
                {
                    start = i;
                }

                open.Push(c);
            }
            else if ((c is ']' or '}') && open.Count > 0)
            {
                open.Pop();
                if (c == '}' && open.Count == 1 && start >= 0)
                {
                    yield return json[start..(i + 1)];
                    start = -1;
                }
            }
        }
    }

    // VS Code's User folder: in the data folder beside a portable copy, otherwise in Roaming AppData.
    private static string VsCodeSettings(string exe)
    {
        var portable = Path.Combine(Path.GetDirectoryName(exe) ?? string.Empty, "data");
        return Directory.Exists(portable)
            ? Path.Combine(portable, "user-data", "User")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User");
    }

    // Stable and Preview are separate Store packages with their own settings; a portable copy (a ".portable"
    // file beside it) keeps them in a settings folder next to it, and an unpackaged install in Local AppData.
    private static string TerminalSettings(string exe)
    {
        var folder = Path.GetDirectoryName(exe) ?? string.Empty;
        if (File.Exists(Path.Combine(folder, ".portable")))
        {
            return Path.Combine(folder, "settings", "settings.json");
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var package = exe.Contains("WindowsTerminalPreview", StringComparison.OrdinalIgnoreCase) ? "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe"
            : exe.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ? "Microsoft.WindowsTerminal_8wekyb3d8bbwe"
            : null;
        return package is null
            ? Path.Combine(local, "Microsoft", "Windows Terminal", "settings.json")
            : Path.Combine(local, "Packages", package, "LocalState", "settings.json");
    }

    // [{ "key": "ctrl+k ctrl+s", "command": "x", "when": "..." }, { "key": "...", "command": "-x" }].
    // "-x" takes x off those keys (or off all its keys, without a key) and leaves other commands on them.
    // VS Code reads this file leniently: rules before a mistake (a missing closing bracket, say) still
    // apply. So do the same, rather than treat the file as empty and fall back to keys it reassigned.
    internal static List<Binding> ReadVsCode(string json)
    {
        var bindings = new List<Binding>();
        foreach (var text in TopLevelObjects(json))
        {
            if (Rule(text) is not var (key, command, when) || command is null)
            {
                continue; // No usable command: VS Code skips the rule too.
            }

            var keys = key is null ? null : Normalize(key);
            if (command.StartsWith('-'))
            {
                var removed = command[1..];
                bindings.Add(new Binding(keys ?? $"<all:{removed}>", null, removed));
            }
            else if (keys is not null)
            {
                bindings.Add(new Binding(keys, command, Conditional: !string.IsNullOrEmpty(when)));
            }
        }

        return bindings;
    }

    // A rule's key, command and when. Strict JSON first; if the rule has a slip VS Code tolerates (a missing
    // comma, say), the three string fields are read directly. A field that isn't a string counts as absent,
    // and a rule whose key or command isn't a string is skipped (null), as VS Code's filter does.
    private static (string? Key, string? Command, string? When)? Rule(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var rule = document.RootElement;
            string? Field(string name, out bool wrongType)
            {
                wrongType = false;
                if (!rule.TryGetProperty(name, out var value))
                {
                    return null;
                }

                wrongType = value.ValueKind != JsonValueKind.String;
                return wrongType ? null : value.GetString();
            }

            var key = Field("key", out var badKey);
            var command = Field("command", out var badCommand);
            var when = Field("when", out _);
            return badKey || badCommand ? null : (key, command, when);
        }
        catch (JsonException)
        {
            // Comments are taken out first: a commented-out "command" mustn't be read as the rule's own.
            var code = WithoutComments(text);
            string? Field(string name) =>
                Regex.Match(code, "\"" + name + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"") is { Success: true } m ? Regex.Unescape(m.Groups[1].Value) : null;
            return (Field("key"), Field("command"), Field("when"));
        }
    }

    // The text with // and /* */ comments blanked out, strings left as they are.
    private static string WithoutComments(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                var start = i;
                for (i++; i < text.Length && text[i] != '"'; i++)
                {
                    if (text[i] == '\\')
                    {
                        i++;
                    }
                }

                result.Append(text, start, Math.Min(i, text.Length - 1) - start + 1);
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                result.Append(' ');
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 1;
                result.Append(' ');
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    // Newer Terminal: "actions" define commands under ids ({ "command": ..., "id": "User.X" }, which may also
    // redefine a built-in id) and "keybindings" put keys on ids ({ "id": ..., "keys": ... }, an id of null
    // unbinding them). Older: "actions" entries carry "keys" with their "command" ("unbound" or null unbinds).
    internal static List<Binding> ReadTerminal(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        var bindings = new List<Binding>();
        var actions = root.TryGetProperty("actions", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : [];
        var keybindings = root.TryGetProperty("keybindings", out var kb) && kb.ValueKind == JsonValueKind.Array ? kb.EnumerateArray().ToList() : [];

        // What each user-defined id runs, as the built-in id for the same thing where there is one.
        var runs = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in actions)
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.GetString() is string name
                && item.TryGetProperty("command", out var command))
            {
                // An entry with no command (null or "unbound") only unbinds its keys; it doesn't redefine the id.
                if (TerminalCommand(command) is { } target)
                {
                    runs[name] = target; // A later definition of the same id replaces an earlier one.
                }
            }
        }

        foreach (var (name, target) in runs)
        {
            if (name.StartsWith("Terminal.", StringComparison.Ordinal) && target != name)
            {
                bindings.Add(new Binding($"<redefined:{name}>", null));
            }
        }

        foreach (var item in actions.Concat(keybindings))
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("keys", out var keys))
            {
                continue;
            }

            // Keys put on an id run whatever that id is finally defined as (a later definition replaces
            // the command written beside the keys); keys without an id run their own command.
            string? target;
            if (item.TryGetProperty("command", out var unbinds) && TerminalCommand(unbinds) is null)
            {
                target = null; // Unbinds these keys, whatever id is written beside them.
            }
            else if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                var name = id.GetString()!;
                target = runs.TryGetValue(name, out var mapped) ? mapped : name;
            }
            else if (item.TryGetProperty("command", out var command))
            {
                target = TerminalCommand(command);
            }
            else if (item.TryGetProperty("id", out _))
            {
                target = null; // "id": null unbinds the keys.
            }
            else
            {
                continue;
            }

            IEnumerable<string?> all = keys.ValueKind == JsonValueKind.Array ? keys.EnumerateArray().Select(k => k.GetString()) : [keys.GetString()];
            foreach (var key in all.OfType<string>())
            {
                bindings.Add(target is null ? new Binding(Normalize(key), null, "*") : new Binding(Normalize(key), target));
            }
        }

        return bindings;
    }

    // A Terminal command, a name or { "action": name, ...arguments }, as the built-in id that does the same
    // (null for "unbound"). Others get a name of their own: all the resolver needs is that it's something else.
    private static string? TerminalCommand(JsonElement command)
    {
        if (command.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var action = command.ValueKind == JsonValueKind.String ? command.GetString()
            : command.ValueKind == JsonValueKind.Object && command.TryGetProperty("action", out var a) ? a.GetString() : null;
        string? Argument(string name) =>
            command.ValueKind == JsonValueKind.Object && command.TryGetProperty(name, out var v) ? v.ToString() : null;

        // "newTab" and { "action": "newTab" } are the same; with a profile or other arguments it's another tab.
        bool NoArguments() => command.ValueKind == JsonValueKind.String || command.EnumerateObject().Count() == 1;

        return action switch
        {
            null or "unbound" => null,
            "copy" => "Terminal.CopyToClipboard",
            "paste" => "Terminal.PasteFromClipboard",
            "find" => "Terminal.FindText",
            "selectAll" => "Terminal.SelectAll",
            "commandPalette" => "Terminal.ToggleCommandPalette",
            "toggleFullscreen" => "Terminal.ToggleFullscreen",
            "newTab" when NoArguments() => "Terminal.OpenNewTab",
            "newWindow" when NoArguments() => "Terminal.OpenNewWindow",
            "resetFontSize" => "Terminal.ResetFontSize",
            "openSettings" when Argument("target") == "settingsUI" => "Terminal.OpenSettingsUI",
            "adjustFontSize" when double.TryParse(Argument("delta"), out var delta) && delta != 0
                => delta > 0 ? "Terminal.IncreaseFontSize" : "Terminal.DecreaseFontSize",
            "splitPane" when Argument("splitMode") == "duplicate" && Argument("split") is null or "auto" => "Terminal.DuplicatePaneAuto",
            _ => $"other:{command.GetRawText()}",
        };
    }

    // "Shift+Ctrl+P", "ctrl+shift+p" and VS Code's "ctrl+=" vs Terminal's "ctrl+plus" are the same keys:
    // lower case, modifiers in a fixed order, and one name for each key.
    private static string Normalize(string keys) =>
        string.Join(' ', keys.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(chord =>
        {
            // Modifiers come first, each followed by "+" or "-" (VS Code accepts "ctrl-s" for "ctrl+s"); what's
            // left is the key, which can itself be "-" or "+" ("ctrl+-", "ctrl--", "ctrl++").
            var modifiers = new List<string>();
            var rest = chord;
            while (Modifier.Match(rest) is { Success: true } m && m.Length < rest.Length)
            {
                var name = m.Groups[1].Value;
                modifiers.Add(name is "control" ? "ctrl" : name is "meta" or "cmd" ? "win" : name);
                rest = rest[m.Length..];
            }

            var key = rest == "+" ? "plus" : rest;
            string[] order = ["ctrl", "alt", "shift", "win"]; // As the menus show them: Ctrl+Alt+R, Alt+Shift+D.
            return string.Join('+', order.Where(modifiers.Contains).Append(KeyName(key)));
        }));

    private static readonly Regex Modifier = new("^(ctrl|control|shift|alt|win|meta|cmd)[+-]", RegexOptions.Compiled);

    private static string KeyName(string key) => key switch
    {
        "=" or "plus" or "equals" => "=",
        "-" or "minus" => "-",
        "," or "comma" => ",",
        "." or "period" => ".",
        "/" or "slash" => "/",
        ";" or "semicolon" => ";",
        "'" or "quote" => "'",
        "`" or "backtick" => "`",
        "\\" or "backslash" => "\\",
        "esc" or "escape" => "escape",
        "numpad_plus" or "numpad_add" => "numpad_plus",
        "numpad_minus" or "numpad_subtract" => "numpad_minus",
        ['n', 'u', 'm', 'p', 'a', 'd', .. var rest] when rest.TrimStart('_') is { Length: 1 } d && char.IsAsciiDigit(d[0]) => $"numpad_{d}",
        _ => key,
    };

    // Keys in either app's notation, as the virtual keys to press and the way the menu shows them.
    public static Keys? Parse(string keys)
    {
        var text = Normalize(keys);
        var chords = new List<ushort[]>();
        var display = new List<string>();
        foreach (var chord in text.Split(' '))
        {
            var parts = chord.Split('+');
            var codes = new List<ushort>();
            var shown = new StringBuilder();
            foreach (var modifier in parts[..^1])
            {
                codes.Add(modifier switch { "ctrl" => 0x11, "shift" => 0x10, "alt" => 0x12, _ => 0x5B });
                shown.Append(modifier switch { "ctrl" => "Ctrl+", "shift" => "Shift+", "alt" => "Alt+", _ => "Win+" });
            }

            if (Key(parts[^1]) is not (ushort code, string name))
            {
                return null; // Some key this can't press: better hidden than pressing the wrong thing.
            }

            codes.Add(code);
            shown.Append(name);
            chords.Add([.. codes]);
            display.Add(shown.ToString());
        }

        return new Keys(text, string.Join(' ', display), [.. chords]);
    }

    private static (ushort Code, string Name)? Key(string key) => key switch
    {
        { Length: 1 } when key[0] is >= 'a' and <= 'z' => ((ushort)char.ToUpperInvariant(key[0]), key.ToUpperInvariant()),
        { Length: 1 } when key[0] is >= '0' and <= '9' => (key[0], key),
        ['f', .. var n] when int.TryParse(n, out var f) && f is >= 1 and <= 24 => ((ushort)(0x6F + f), $"F{f}"),
        "left" => (0x25, "←"),
        "up" => (0x26, "↑"),
        "right" => (0x27, "→"),
        "down" => (0x28, "↓"),
        "enter" => (0x0D, "Enter"),
        "tab" => (0x09, "Tab"),
        "escape" => (0x1B, "Esc"),
        "space" => (0x20, "Space"),
        "backspace" => (0x08, "Backspace"),
        "delete" => (0x2E, "Delete"),
        "insert" => (0x2D, "Insert"),
        "home" => (0x24, "Home"),
        "end" => (0x23, "End"),
        "pageup" => (0x21, "PageUp"),
        "pagedown" => (0x22, "PageDown"),
        "=" => (0xBB, "="),
        "-" => (0xBD, "-"),
        "," => (0xBC, ","),
        "." => (0xBE, "."),
        "/" => (0xBF, "/"),
        ";" => (0xBA, ";"),
        "'" => (0xDE, "'"),
        "`" => (0xC0, "`"),
        "[" => (0xDB, "["),
        "]" => (0xDD, "]"),
        "\\" => (0xDC, "\\"),
        "numpad_plus" => (0x6B, "數字鍵 +"),
        "numpad_minus" => (0x6D, "數字鍵 -"),
        ['n', 'u', 'm', 'p', 'a', 'd', '_', var d] when char.IsAsciiDigit(d) => ((ushort)(0x60 + (d - '0')), $"數字鍵 {d}"),
        _ => null,
    };
}
