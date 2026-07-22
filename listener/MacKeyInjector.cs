using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteHelper.Listener;

/// <summary>
/// Injects keystrokes on macOS via CGEvent — used for developing and testing
/// Remote Helper without a Windows machine in the loop. Requires the hosting
/// terminal to have Accessibility permission (System Settings → Privacy &amp;
/// Security → Accessibility).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeyInjector : IKeyInjector
{
    public void InjectText(string text)
    {
        // CGEventKeyboardSetUnicodeString has a practical per-event limit;
        // send in modest chunks (typing arrives a few chars at a time anyway).
        const int chunk = 20;
        for (int i = 0; i < text.Length; i += chunk)
            PostUnicode(text.Substring(i, Math.Min(chunk, text.Length - i)));
    }

    public bool InjectKey(string keyName)
    {
        if (!KeyCodes.TryGetValue(keyName, out var code)) return false;
        PostKey(code, down: true);
        PostKey(code, down: false);
        return true;
    }

    // macOS virtual keycodes (Carbon HIToolbox Events.h).
    private static readonly Dictionary<string, ushort> KeyCodes = new()
    {
        ["return"] = 36,
        ["tab"] = 48,
        ["backspace"] = 51,  // kVK_Delete (backwards delete)
        ["escape"] = 53,
        ["delete"] = 117,    // kVK_ForwardDelete
        ["left"] = 123,
        ["right"] = 124,
        ["down"] = 125,
        ["up"] = 126,
        ["f7"] = 98,         // kVK_F7
        ["f8"] = 100,        // kVK_F8
        ["f9"] = 101,        // kVK_F9
        ["f10"] = 109,       // kVK_F10
        ["f11"] = 103,       // kVK_F11
        ["f12"] = 111,       // kVK_F12
        ["menu"] = 110,      // kVK_ContextualMenu
    };

    private static void PostUnicode(string s)
    {
        IntPtr down = CGEventCreateKeyboardEvent(IntPtr.Zero, 0, true);
        IntPtr up = CGEventCreateKeyboardEvent(IntPtr.Zero, 0, false);
        var units = s.ToCharArray();
        CGEventKeyboardSetUnicodeString(down, (nuint)units.Length, units);
        CGEventKeyboardSetUnicodeString(up, (nuint)units.Length, units);
        CGEventPost(kCGHIDEventTap, down);
        CGEventPost(kCGHIDEventTap, up);
        CFRelease(down);
        CFRelease(up);
    }

    private static void PostKey(ushort keyCode, bool down)
    {
        IntPtr evt = CGEventCreateKeyboardEvent(IntPtr.Zero, keyCode, down);
        CGEventPost(kCGHIDEventTap, evt);
        CFRelease(evt);
    }

    private const uint kCGHIDEventTap = 0;

    private const string AppServices =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(AppServices)]
    private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort keyCode, bool keyDown);

    [DllImport(AppServices, CharSet = CharSet.Unicode)]
    private static extern void CGEventKeyboardSetUnicodeString(IntPtr evt, nuint length, char[] chars);

    [DllImport(AppServices)]
    private static extern void CGEventPost(uint tap, IntPtr evt);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr obj);
}
