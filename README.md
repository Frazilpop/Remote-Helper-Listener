# Remote Helper — Listener

```
   .---------.
   | .-----. |
   |+| o o |b|      "Hello. I'm Remote Helper.
   | | \_/ |a|       Type on your phone, I'll type on your PC."
   | '-----' |
   '---------'
```

Remote Helper turns your iPhone or iPad into a wireless keyboard for your
Windows PC or Mac. Everything travels over your own Wi-Fi — no cloud, no
accounts. This repository is the **listener**: the small program that
runs on the computer, receives keystrokes, and types them into whatever
window is focused.

The iPhone/iPad app that talks to it is a companion piece and isn't part
of this repository — but the wire protocol is fully documented in
[docs/PROTOCOL.md](docs/PROTOCOL.md), so you can build your own client if
you like.

## Install (Windows)

1. Download `RemoteHelperListener.exe` from the
   [Releases page](../../releases).
2. Run it. First run: SmartScreen may say "Windows protected your PC" →
   **More info → Run anyway** (the exe is unsigned). Allow the firewall
   prompt on **Private networks**.
3. It installs itself to `%LOCALAPPDATA%\RemoteHelper`, starts with
   Windows, and lives in the system tray. Hover or double-click the
   mascot for connection status; right-click for the menu.

The first time a new device connects, a window shows a 6-digit code —
type it on the device once, and that device is trusted from then on.

To type into windows that run as administrator, run the listener as
administrator too.

## Install (Mac)

1. Download `RemoteHelperListener-mac-arm64.dmg` (Apple Silicon) or
   `RemoteHelperListener-mac-x64.dmg` (Intel) from the
   [Releases page](../../releases).
2. Open the DMG, drag **Remote Helper** into **Applications**, and launch
   it. The app is signed and notarized, so there are no Gatekeeper hoops —
   it appears as a keyboard icon in the menu bar.
3. Grant the two permissions macOS asks about:
   - **Local Network** — so devices can find and reach it.
   - **Accessibility** — so it's allowed to type. The app asks on first
     launch; flip the switch for Remote Helper in **System Settings →
     Privacy & Security → Accessibility**.

Pairing works like on Windows: a dialog shows the 6-digit code the first
time a new device connects. The menu bar icon shows who's connected and
has a **Start at login** toggle so the listener is always ready.

## Build from source

The listener is one C#/.NET 7 codebase that targets two platforms:

```
# Windows tray app (self-contained single exe):
tools/build-windows.sh          # cross-compiles from macOS/Linux too

# macOS menu bar app (self-contained .app in a DMG, both architectures;
# signs with your Developer ID certificate if the keychain has one):
tools/build-mac.sh

# macOS, straight from source:
dotnet run --project listener -f net7.0            # real typing (needs Accessibility permission)
dotnet run --project listener -f net7.0 -- --echo  # prints keystrokes instead of typing
```

`tools/test-client.py` exercises the protocol — pairing, a trusted
reconnect, and two devices at once — against a running listener.

## Security

One gate: pairing. A device can't type until someone at the PC approves
its 6-digit code once. Traffic is unencrypted, so this is designed for a
trusted home network, not a hostile one. See
[docs/PROTOCOL.md](docs/PROTOCOL.md) for the full model.

## Privacy

Remote Helper collects nothing and sends nothing to anyone — everything
stays on your own devices and network. See [PRIVACY.md](PRIVACY.md).

## Credits

Mascots and colour scheme come from
[CLInt](https://github.com/Frazilpop/CLInt).

## License

MIT — see [LICENSE](LICENSE).
