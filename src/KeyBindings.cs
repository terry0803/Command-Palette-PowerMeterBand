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

    private const string Unreadable = "<unreadable>";
    private const string Unsure = "<unsure>";

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, (DateTime Written, List<Binding> Bindings)> Cache = [];

    internal static Keys? Resolve(List<Binding> bindings, string command, string? defaultKeys)
    {
        // The file couldn't be read, or not all of it with certainty: any key may have been given to something
        // else, so none is pressed rather than the wrong one (Ctrl+S closing the editor, say).
        if (bindings.Any(b => b.Keys is Unreadable or Unsure))
        {
            return null;
        }

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
            // Unreadable (being saved, say): not the same as no changes, and not cached, so it's read again.
            return [new Binding(Unreadable, null)];
        }
    }

    // The text of each { ... } directly inside the file's outer [ ... ], skipping strings and comments, so
    // one mistake (a null, a stray comma, a missing closing bracket) doesn't hide the rules around it.
    // `unfinished` says the last one was never closed, or its brackets don't pair up, which leaves its keys
    // uncertain.
    private static List<string> TopLevelObjects(string json, out bool unfinished)
    {
        var found = new List<string>();
        unfinished = false;
        var open = new Stack<char>(); // The brackets around the current position, innermost on top.
        var start = -1;
        var rooted = false; // The root array has been opened.
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            var comment = c == '/' && i + 1 < json.Length && json[i + 1] is '/' or '*';
            if (open.Count == 0 && !comment && !char.IsWhiteSpace(c) && c != '\uFEFF')
            {
                // Nothing after the root array counts, a second array pasted in included, as in VS Code.
                if (rooted)
                {
                    break;
                }

                // An object for the root is read as one, and isn't a list of rules: VS Code uses none of it.
                if (c == '{')
                {
                    return found;
                }

                // Something else before it (a stray character, a null): VS Code may still find the rules after it,
                // so what they are can't be told from here, and none of the keys is trusted.
                if (c != '[')
                {
                    unfinished = true;
                    return found;
                }

                rooted = true;
            }

            if (c == '"')
            {
                for (i++; i < json.Length && json[i] != '"'; i++)
                {
                    if (json[i] == '\\')
                    {
                        i++;
                    }

                    // A string can't run onto the next line: VS Code ends it there as a mistake and reads on, so the
                    // rules after it count there and can't be told apart from here. Nothing is trusted then.
                    if (i < json.Length && json[i] is '\n' or '\r')
                    {
                        unfinished = true;
                        return found;
                    }
                }

                // Never closed at all.
                if (i >= json.Length)
                {
                    unfinished = true;
                    return found;
                }
            }
            else if (c == '/' && i + 1 < json.Length && json[i + 1] == '/')
            {
                while (i < json.Length && !IsLineBreak(json[i]))
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
                if (c == '{' && open.Count == 1)
                {
                    start = i;
                }

                open.Push(c);
            }
            else if (c is ']' or '}')
            {
                // A bracket that closes the wrong kind (a "]]" typed for "}]"): what VS Code recovers from it can't
                // be told from here, so the rule under way is left uncertain.
                if (open.Pop() != (c == ']' ? '[' : '{'))
                {
                    unfinished = true;
                    return found;
                }

                // The root array is closed: like VS Code, nothing after it counts (checked at the top of the loop).
                if (c == '}' && open.Count == 1 && start >= 0)
                {
                    found.Add(json[start..(i + 1)]);
                    start = -1;
                }
            }
        }

        unfinished = start >= 0;
        return found;
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
        var objects = TopLevelObjects(json, out var unfinished);
        var unsure = unfinished;
        foreach (var text in objects)
        {
            var rule = Rule(text, out var guessed);
            unsure |= guessed;
            if (rule is not var (key, command, when) || command is null)
            {
                continue; // No usable command: VS Code skips the rule too.
            }

            // In VS Code's own names first: one it doesn't take, or one whose key can't be told from here
            // ("ctrl+[KeyS]" moves with the layout), may stand for any key, so none is trusted.
            var keys = key is null ? null : InAppNotation(key, VsCode) is { } named ? Normalize(named) : null;
            if (key is not null && keys is null)
            {
                unsure = true;
                continue;
            }
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

        // A rule read only by picking fields out of broken JSON, or one left unclosed, may not be what VS Code
        // makes of it: a "command" inside "args" passes for the rule's own, for one. Then nothing is trusted.
        if (unsure)
        {
            bindings.Add(new Binding(Unsure, null));
        }

        return bindings;
    }

    // A rule's key, command and when. Strict JSON first; if the rule has a slip VS Code tolerates (a missing
    // comma, say), the three string fields are read directly. A field that isn't a string counts as absent,
    // and a rule whose key or command isn't a string is skipped (null), as VS Code's filter does.
    private static (string? Key, string? Command, string? When)? Rule(string text, out bool guessed)
    {
        guessed = false;
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
            guessed = true;
            // Comments are taken out first: a commented-out "command" mustn't be read as the rule's own.
            var code = WithoutComments(text);
            string? Field(string name) =>
                Regex.Match(code, "\"" + name + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"") is { Success: true } m ? Regex.Unescape(m.Groups[1].Value) : null;
            return (Field("key"), Field("command"), Field("when"));
        }
    }

    // What VS Code's JSON reader takes as the end of a line, and so of a // comment: a file saved with CR
    // alone still has its next line read.
    private static bool IsLineBreak(char c) => c is '\n' or '\r' or '\u2028' or '\u2029';

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
                while (i < text.Length && !IsLineBreak(text[i]))
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

            // Terminal takes "keys" as one string, or a list of exactly one; anything else it may ignore.
            var key = keys.ValueKind == JsonValueKind.String ? keys.GetString()
                : keys.ValueKind == JsonValueKind.Array && keys.GetArrayLength() == 1 && keys[0].ValueKind == JsonValueKind.String ? keys[0].GetString()
                : null;
            if (keys.ValueKind == JsonValueKind.Null)
            {
                continue; // No keys at all: nothing is put on any.
            }

            // Only what Terminal itself takes ("[F12]" is VS Code's, "ctrl-f12" isn't its separator): another may be
            // ignored there, or not.
            if (key is null || InAppNotation(key, Terminal) is not { } named)
            {
                bindings.Add(new Binding(Unsure, null));
                continue;
            }

            var normalized = Normalize(named);
            bindings.Add(target is null ? new Binding(normalized, null, "*") : new Binding(normalized, target));
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

    // VS Code's "[KeyS]", "[F12]": the key at a place on the keyboard rather than the one with that name. Where
    // that's the same key whatever the layout, it's the named one ("ctrl+[F12]" is "ctrl+f12"). Letters, digits
    // and signs move with the layout, so which named key they are isn't known: left in brackets (see Physical).
    private static string PhysicalKey(string code) => code switch
    {
        _ when FunctionKey(code) => code,
        "escape" or "enter" or "tab" or "space" or "backspace" or "delete" or "insert" or "home" or "end" or "pageup" or "pagedown" => code,
        "arrowleft" => "left",
        "arrowup" => "up",
        "arrowright" => "right",
        "arrowdown" => "down",
        "numpadadd" => "numpad_plus",
        "numpadsubtract" => "numpad_minus",
        ['n', 'u', 'm', 'p', 'a', 'd', var d] when char.IsAsciiDigit(d) => $"numpad_{d}",
        _ => $"[{code}]",
    };

    // How an app's settings write keys: the modifiers it takes and what may follow each, whether one setting may be a
    // sequence ("ctrl+k ctrl+s"), its key names, and whether those are matched as written ("F12" isn't "f12") or in
    // any case. Modifiers are taken in any case by both.
    private sealed record Notation(Regex Modifiers, bool Sequences, Func<string, string?> Key, bool KeysAsWritten);

    // VS Code's keybindingParser: ctrl, shift, alt, meta, win or cmd, then "+" or "-"; sequences parted by spaces. It
    // reads the whole setting in lower case.
    private static readonly Notation VsCode = new(
        new("^(ctrl|shift|alt|meta|win|cmd)[+-]", RegexOptions.Compiled | RegexOptions.IgnoreCase), true, VsCodeKey, false);

    // Windows Terminal's KeyChordSerialization: ctrl, alt, shift or win, only ever then "+"; one chord, no sequences.
    // Its key names are looked up as written, in lower case.
    private static readonly Notation Terminal = new(
        new(@"^(ctrl|alt|shift|win)\+", RegexOptions.Compiled | RegexOptions.IgnoreCase), false, TerminalKey, true);

    // Keys as an app's settings write them, in the names used here, ready for Normalize. Null unless all of it is
    // written the way that app takes it: a modifier it doesn't know ("control+f12"), a separator it doesn't use
    // (Terminal's "ctrl-f12"), a sequence where it takes none, or a key name it doesn't know. Such a setting may be
    // ignored there, so it can't be read as these keys or as none.
    private static string? InAppNotation(string keys, Notation notation)
    {
        var chords = keys.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (chords.Length == 0 || (chords.Length > 1 && !notation.Sequences) || (!notation.Sequences && keys != keys.Trim()))
        {
            return null;
        }

        var read = new List<string>();
        foreach (var chord in chords)
        {
            var modifiers = string.Empty;
            var rest = chord;
            while (notation.Modifiers.Match(rest) is { Success: true } m && m.Length < rest.Length)
            {
                modifiers += m.Value.ToLowerInvariant();
                rest = rest[m.Length..];
            }

            if (notation.Key(notation.KeysAsWritten ? rest : rest.ToLowerInvariant()) is not { } key)
            {
                return null;
            }

            read.Add(modifiers + key);
        }

        return string.Join(' ', read);
    }

    private static bool Sign(string key) => key is "=" or "-" or "," or "." or "/" or ";" or "'" or "`" or "[" or "]" or "\\";

    // "f1" to "f24" exactly: not "f01" or "f+1", which no app reads as F1.
    private static bool FunctionKey(string key) =>
        key is ['f', '1' or '2' or '3' or '4' or '5' or '6' or '7' or '8' or '9', .. var more]
        && more.Length <= 1 && more.All(char.IsAsciiDigit) && int.Parse(key[1..], System.Globalization.CultureInfo.InvariantCulture) <= 24;

    // VS Code's names (its keyCodes.ts), including the Windows names for the signs ("ctrl+oem_plus" is "ctrl+=") and
    // keys at a place on the keyboard ("[F12]"). Keys this never presses and nothing else stands for are kept as they
    // are; anything else is null.
    private static string? VsCodeKey(string key) => key switch
    {
        { Length: 1 } when char.IsAsciiLetterOrDigit(key[0]) || Sign(key) => key,
        _ when FunctionKey(key) => key,
        "left" or "up" or "right" or "down" or "enter" or "tab" or "escape" or "space" or "backspace" or "delete"
            or "insert" or "home" or "end" or "pageup" or "pagedown" => key,
        "oem_1" => ";",
        "oem_plus" => "=",
        "oem_comma" => ",",
        "oem_minus" => "-",
        "oem_period" => ".",
        "oem_2" => "/",
        "oem_3" => "`",
        "oem_4" => "[",
        "oem_5" => "\\",
        "oem_6" => "]",
        "oem_7" => "'",
        ['n', 'u', 'm', 'p', 'a', 'd', var d] when char.IsAsciiDigit(d) => $"numpad_{d}",
        "numpad_add" => "numpad_plus",
        "numpad_subtract" => "numpad_minus",
        "numpad_multiply" or "numpad_divide" or "numpad_decimal" or "numpad_separator" or "capslock" or "pausebreak"
            or "contextmenu" or "numlock" or "scrolllock" => key,
        ['[', .. var code, ']'] when code.Length > 0 && PhysicalKey(code) is var named && named[0] != '[' => named,
        _ => null,
    };

    // Windows Terminal's names (its KeyChordSerialization), which differ: "plus", "esc", "pgup", "numpad_1".
    // Names of more than one letter are matched as written: "F12" or "Esc" isn't one it knows. A single letter is that
    // letter's key in either case ("ctrl+C" is Ctrl+C, with no Shift added).
    private static string? TerminalKey(string key) => key switch
    {
        { Length: 1 } when char.IsAsciiLetter(key[0]) => key.ToLowerInvariant(),
        { Length: 1 } when char.IsAsciiDigit(key[0]) || Sign(key) => key,
        _ when FunctionKey(key) => key,
        "plus" => "=",
        "minus" => "-",
        "left" or "up" or "right" or "down" or "enter" or "tab" or "space" or "backspace" or "delete" or "insert"
            or "home" or "end" => key,
        "esc" or "escape" => "escape",
        "pgup" or "pageup" => "pageup",
        "pgdn" or "pagedown" => "pagedown",
        ['n', 'u', 'm', 'p', 'a', 'd', var d] when char.IsAsciiDigit(d) => $"numpad_{d}",
        ['n', 'u', 'm', 'p', 'a', 'd', '_', var d] when char.IsAsciiDigit(d) => $"numpad_{d}",
        "numpad_plus" or "numpad_add" => "numpad_plus",
        "numpad_minus" or "numpad_subtract" => "numpad_minus",
        "numpad_multiply" or "numpad_divide" or "numpad_period" or "numpad_decimal" or "app" or "menu" => key,
        _ => null,
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
