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
        if (!VirtualKeys.TryGetValue(keyName, out var vk)) return false;
        bool extended = ExtendedKeys.Contains(keyName);
        Send(new[]
        {
            VkInput(vk, keyUp: false, extended),
            VkInput(vk, keyUp: true, extended),
        });
        return true;
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
    };

    // These live in the extended-key group; without the flag some apps
    // see numpad keys instead.
    private static readonly HashSet<string> ExtendedKeys =
        new() { "left", "up", "right", "down", "delete", "menu" };

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
