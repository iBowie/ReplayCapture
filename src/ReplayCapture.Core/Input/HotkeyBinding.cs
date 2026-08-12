namespace ReplayCapture.Core.Input;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,

    /// <summary>Suppress auto-repeat while the key is held; always set for a save trigger.</summary>
    NoRepeat = 0x4000,
}

/// <summary>A parsed keyboard shortcut such as <c>Alt+F10</c>, ready for <c>RegisterHotKey</c>.</summary>
public sealed record HotkeyBinding(HotkeyModifiers Modifiers, uint VirtualKey, string Display)
{
    public static readonly HotkeyBinding Default =
        new(HotkeyModifiers.Alt | HotkeyModifiers.NoRepeat, 0x79 /* VK_F10 */, "Alt+F10");

    public static HotkeyBinding Parse(string text)
    {
        if (!TryParse(text, out var binding, out var error))
            throw new FormatException($"Invalid hotkey '{text}': {error}");
        return binding;
    }

    public static bool TryParse(string text, out HotkeyBinding binding, out string? error)
    {
        binding = null!;
        error = null;

        var tokens = (text ?? "")
            .Split(['+', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            error = "empty";
            return false;
        }

        var modifiers = HotkeyModifiers.NoRepeat;
        uint? key = null;
        var displayParts = new List<string>();

        foreach (var token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "alt": modifiers |= HotkeyModifiers.Alt; displayParts.Add("Alt"); continue;
                case "ctrl":
                case "control": modifiers |= HotkeyModifiers.Control; displayParts.Add("Ctrl"); continue;
                case "shift": modifiers |= HotkeyModifiers.Shift; displayParts.Add("Shift"); continue;
                case "win":
                case "super": modifiers |= HotkeyModifiers.Win; displayParts.Add("Win"); continue;
            }

            if (key is not null)
            {
                error = $"more than one non-modifier key ('{token}')";
                return false;
            }

            if (!TryParseKey(token, out var vk, out var display))
            {
                error = $"unrecognised key '{token}'";
                return false;
            }

            key = vk;
            displayParts.Add(display);
        }

        if (key is null)
        {
            error = "no non-modifier key";
            return false;
        }

        // Windows will happily register a bare key and then swallow it globally, which makes the
        // machine unusable. Require at least one modifier, except for the function keys.
        var bare = (modifiers & ~HotkeyModifiers.NoRepeat) == HotkeyModifiers.None;
        if (bare && key is < 0x70 or > 0x87)
        {
            error = "needs at least one modifier (Alt, Ctrl, Shift or Win)";
            return false;
        }

        binding = new HotkeyBinding(modifiers, key.Value, string.Join("+", displayParts));
        return true;
    }

    private static bool TryParseKey(string token, out uint vk, out string display)
    {
        vk = 0;
        display = token.ToUpperInvariant();

        // Function keys: VK_F1 (0x70) through VK_F24 (0x87).
        if (token.Length is 2 or 3 &&
            (token[0] is 'f' or 'F') &&
            int.TryParse(token.AsSpan(1), out var fn) &&
            fn is >= 1 and <= 24)
        {
            vk = (uint)(0x6F + fn);
            display = "F" + fn;
            return true;
        }

        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c;
                display = c.ToString();
                return true;
            }
        }

        (uint Vk, string Name)? named = token.ToLowerInvariant() switch
        {
            "space" => (0x20u, "Space"),
            "insert" or "ins" => (0x2Du, "Insert"),
            "delete" or "del" => (0x2Eu, "Delete"),
            "home" => (0x24u, "Home"),
            "end" => (0x23u, "End"),
            "pageup" or "pgup" => (0x21u, "PageUp"),
            "pagedown" or "pgdn" => (0x22u, "PageDown"),
            "printscreen" or "prtsc" => (0x2Cu, "PrintScreen"),
            "pause" => (0x13u, "Pause"),
            "scrolllock" => (0x91u, "ScrollLock"),
            "up" => (0x26u, "Up"),
            "down" => (0x28u, "Down"),
            "left" => (0x25u, "Left"),
            "right" => (0x27u, "Right"),
            "tab" => (0x09u, "Tab"),
            "backspace" => (0x08u, "Backspace"),
            _ => null,
        };

        if (named is null) return false;
        (vk, display) = named.Value;
        return true;
    }

    public override string ToString() => Display;
}
