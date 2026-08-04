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
        if (SystemKeys.TryGetValue(keyName, out var nx))
        {
            PostSystemKey(nx, down: true);
            PostSystemKey(nx, down: false);
            return true;
        }
        if (!KeyCodes.TryGetValue(keyName, out var code))
        {
            if (keyName.Length != 1 || !CharCodes.TryGetValue(keyName[0], out code))
                return false;
        }
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
        ["space"] = 49,      // kVK_Space — as a KEY so player hotkeys fire
        ["f"] = 3,           // kVK_ANSI_F — fullscreen in most players
        ["pageup"] = 116,    // kVK_PageUp
        ["pagedown"] = 121,  // kVK_PageDown
        ["home"] = 115,      // kVK_Home
        ["end"] = 119,       // kVK_End
    };

    // A single printable character names its physical key (added in v1.10) —
    // a real key press, so player hotkeys fire. kVK_ANSI_* positions, so a
    // non-US layout gets the US key's position, same as "f" always has.
    private static readonly Dictionary<char, ushort> CharCodes = new()
    {
        ['a'] = 0, ['b'] = 11, ['c'] = 8, ['d'] = 2, ['e'] = 14, ['f'] = 3,
        ['g'] = 5, ['h'] = 4, ['i'] = 34, ['j'] = 38, ['k'] = 40, ['l'] = 37,
        ['m'] = 46, ['n'] = 45, ['o'] = 31, ['p'] = 35, ['q'] = 12, ['r'] = 15,
        ['s'] = 1, ['t'] = 17, ['u'] = 32, ['v'] = 9, ['w'] = 13, ['x'] = 7,
        ['y'] = 16, ['z'] = 6,
        ['0'] = 29, ['1'] = 18, ['2'] = 19, ['3'] = 20, ['4'] = 21,
        ['5'] = 23, ['6'] = 22, ['7'] = 26, ['8'] = 28, ['9'] = 25,
        ['-'] = 27, ['='] = 24, ['['] = 33, [']'] = 30, ['\\'] = 42,
        [';'] = 41, ['\''] = 39, [','] = 43, ['.'] = 47, ['/'] = 44,
        ['`'] = 50,
    };

    // Volume and media-transport keys have no virtual keycodes — they're
    // NX_KEYTYPE_* system events, built through NSEvent and posted as
    // CGEvents like everything else.
    private static readonly Dictionary<string, int> SystemKeys = new()
    {
        ["volumeup"] = 0,    // NX_KEYTYPE_SOUND_UP
        ["volumedown"] = 1,  // NX_KEYTYPE_SOUND_DOWN
        ["mute"] = 7,        // NX_KEYTYPE_MUTE
        ["playpause"] = 16,  // NX_KEYTYPE_PLAY
        ["nexttrack"] = 17,  // NX_KEYTYPE_NEXT
        ["prevtrack"] = 18,  // NX_KEYTYPE_PREVIOUS
    };

    private static bool _appKitLoaded;

    private static void PostSystemKey(int keyType, bool down)
    {
        if (!_appKitLoaded)
        {
            // NSEvent lives in AppKit, which nothing links for us.
            NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
            _appKitLoaded = true;
        }
        var pool = Send(Send(Cls("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        try
        {
            // data1 packs the key type and press state the way the HID
            // system expects: 0x0A = key down, 0x0B = key up.
            nint data1 = (keyType << 16) | ((down ? 0x0A : 0x0B) << 8);
            var evt = SendEvent(Cls("NSEvent"),
                Sel("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
                14 /* NSEventTypeSystemDefined */, default,
                (nuint)(down ? 0xA00 : 0xB00), 0.0, 0, IntPtr.Zero,
                8 /* NX_SUBTYPE_AUX_CONTROL_BUTTONS — media/volume keys */,
                data1, -1);
            if (evt == IntPtr.Zero) return;
            var cgEvent = Send(evt, Sel("CGEvent")); // owned by the NSEvent
            if (cgEvent != IntPtr.Zero) CGEventPost(kCGHIDEventTap, cgEvent);
        }
        finally
        {
            Send(pool, Sel("drain"));
        }
    }

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
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NSPoint
    {
        public readonly double X, Y;
    }

    [DllImport(LibObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr Cls(string name);

    [DllImport(LibObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr Sel(string name);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendEvent(IntPtr receiver, IntPtr selector,
        nuint type, NSPoint location, nuint modifierFlags, double timestamp,
        nint windowNumber, IntPtr context, short subtype, nint data1, nint data2);

    [DllImport(AppServices)]
    private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort keyCode, bool keyDown);

    [DllImport(AppServices, CharSet = CharSet.Unicode)]
    private static extern void CGEventKeyboardSetUnicodeString(IntPtr evt, nuint length, char[] chars);

    [DllImport(AppServices)]
    private static extern void CGEventPost(uint tap, IntPtr evt);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr obj);
}
