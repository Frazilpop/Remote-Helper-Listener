using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteHelper.Listener;

/// <summary>
/// Injects keystrokes on Windows via user32 SendInput. Text goes in as
/// KEYEVENTF_UNICODE so the PC's keyboard layout never matters; named keys
/// go in as virtual-key codes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsKeyInjector : IKeyInjector
{
    public void InjectText(string text)
    {
        // One INPUT pair (down+up) per UTF-16 code unit. Surrogate pairs
        // arrive as two consecutive units and Windows reassembles them.
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = UnicodeInput(text[i], keyUp: false);
            inputs[i * 2 + 1] = UnicodeInput(text[i], keyUp: true);
        }
        Send(inputs);
    }

    public bool InjectKey(string keyName)
    {
        if (!VirtualKeys.TryGetValue(keyName, out var vk) &&
            !TryCharKey(keyName, out vk)) return false;
        bool extended = ExtendedKeys.Contains(keyName);
        Send(new[]
        {
            VkInput(vk, keyUp: false, extended),
            VkInput(vk, keyUp: true, extended),
        });
        return true;
    }

    // A single printable character names its physical key (added in v1.10) —
    // a real key press, unlike text injection, so player hotkeys fire.
    // Letters/digits map straight to their VK codes; US punctuation to the
    // OEM keys. Same idea "f" pioneered, generalised.
    private static bool TryCharKey(string keyName, out ushort vk)
    {
        vk = 0;
        if (keyName.Length != 1) return false;
        char c = keyName[0];
        if (c is >= 'a' and <= 'z') { vk = (ushort)('A' + (c - 'a')); return true; }
        if (c is >= '0' and <= '9') { vk = (ushort)c; return true; }
        vk = c switch
        {
            ';' => 0xBA,  // VK_OEM_1
            '=' => 0xBB,  // VK_OEM_PLUS
            ',' => 0xBC,  // VK_OEM_COMMA
            '-' => 0xBD,  // VK_OEM_MINUS
            '.' => 0xBE,  // VK_OEM_PERIOD
            '/' => 0xBF,  // VK_OEM_2
            '`' => 0xC0,  // VK_OEM_3
            '[' => 0xDB,  // VK_OEM_4
            '\\' => 0xDC, // VK_OEM_5
            ']' => 0xDD,  // VK_OEM_6
            '\'' => 0xDE, // VK_OEM_7
            _ => (ushort)0,
        };
        return vk != 0;
    }

    private static readonly Dictionary<string, ushort> VirtualKeys = new()
    {
        ["backspace"] = 0x08, // VK_BACK
        ["tab"] = 0x09,       // VK_TAB
        ["return"] = 0x0D,    // VK_RETURN
        ["escape"] = 0x1B,    // VK_ESCAPE
        ["left"] = 0x25,      // VK_LEFT
        ["up"] = 0x26,        // VK_UP
        ["right"] = 0x27,     // VK_RIGHT
        ["down"] = 0x28,      // VK_DOWN
        ["delete"] = 0x2E,    // VK_DELETE
        ["menu"] = 0x5D,      // VK_APPS — the context-menu key
        ["space"] = 0x20,     // VK_SPACE — as a KEY so player hotkeys fire
        ["f"] = 0x46,         // fullscreen in VLC/YouTube/most players
        ["mute"] = 0xAD,      // VK_VOLUME_MUTE
        ["volumedown"] = 0xAE, // VK_VOLUME_DOWN
        ["volumeup"] = 0xAF,  // VK_VOLUME_UP
        ["nexttrack"] = 0xB0, // VK_MEDIA_NEXT_TRACK
        ["prevtrack"] = 0xB1, // VK_MEDIA_PREV_TRACK
        ["playpause"] = 0xB3, // VK_MEDIA_PLAY_PAUSE
        ["f7"] = 0x76,        // VK_F7
        ["f8"] = 0x77,        // VK_F8
        ["f9"] = 0x78,        // VK_F9
        ["f10"] = 0x79,       // VK_F10
        ["f11"] = 0x7A,       // VK_F11
        ["f12"] = 0x7B,       // VK_F12
        ["pageup"] = 0x21,    // VK_PRIOR
        ["pagedown"] = 0x22,  // VK_NEXT
        ["end"] = 0x23,       // VK_END
        ["home"] = 0x24,      // VK_HOME
    };

    // These live in the extended-key group; without the flag some apps
    // see numpad keys instead — and the media/volume keys are E0-prefixed
    // on real keyboards, so they get the flag too.
    private static readonly HashSet<string> ExtendedKeys =
        new() { "left", "up", "right", "down", "delete", "menu",
                "mute", "volumedown", "volumeup",
                "nexttrack", "prevtrack", "playpause",
                "pageup", "pagedown", "home", "end" };

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private static INPUT UnicodeInput(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
            }
        }
    };

    private static INPUT VkInput(ushort vk, bool keyUp, bool extended) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = (extended ? KEYEVENTF_EXTENDEDKEY : 0)
                        | (keyUp ? KEYEVENTF_KEYUP : 0),
            }
        }
    };

    private static void Send(INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            Console.WriteLine($"[warn] SendInput injected {sent}/{inputs.Length} events " +
                              "(is the focused window running as administrator?)");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    // The union must be pointer-aligned and sized for the largest member
    // (MOUSEINPUT); explicit layout with the full set keeps sizeof correct.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
