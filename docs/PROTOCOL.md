# Remote Helper protocol

```
   .---------.
   | .-----. |
   |+| o o |b|      "I carry your keystrokes. Carefully."
   | | \_/ |a|
   | '-----' |
   '---------'
```

One phone (or iPad — any number of devices) talks to any number of
listeners over the local network. Plain TCP, newline-delimited JSON,
UTF-8. Nothing leaves the LAN.

## Discovery

Each listener advertises itself via mDNS/Bonjour:

- Service type: `_remotehelper._tcp`
- Instance name: the PC's hostname
- Default port: **8737**

Devices browse for that service type and keep a live connection to every
listener they find. Manual connection by IP address and port is always
available as a fallback (guest networks and some routers block mDNS).
Choosing which PC receives the typing happens purely on the device — all
connections stay open; input is only sent down the active one.

## Message framing

Every message is a single JSON object on one line, terminated by `\n`.
Every message has a `t` (type) field. Unknown types must be ignored, not
treated as errors — that's what lets old clients talk to new listeners.

## Handshake and one-time pairing

`hello` carries a stable per-device id and a friendly name:

```json
{"t":"hello","name":"Fraser's iPhone","deviceId":"<uuid>"}
```

- **Known device** (`deviceId` already trusted by this PC) → `{"t":"ok","host":"FRASER"}`, session is live.
- **New device** → the PC displays a 6-digit code and replies
  `{"t":"pair_required","host":"FRASER"}`. The client sends the code:

```json
{"t":"pair","deviceId":"<uuid>","name":"Fraser's iPhone","pin":"481920"}
```

  The PC replies `{"t":"paired","host":"FRASER"}` (and remembers the
  `deviceId` forever) or `{"t":"pair_failed","attemptsLeft":N}`. Three
  wrong codes drops the connection.

The device id lives in the phone's keychain, so it survives app
reinstalls — a device really does pair once and never again. Trust is
per (device, PC) pair: N devices and M PCs each pair once, independently.

## Input messages

```json
{"t":"text","s":"Hello £10 → naïve"}   // inject as literal Unicode text
{"t":"key","k":"backspace"}             // named non-printing key
```

Named keys: `backspace`, `return`, `tab`, `escape`, `delete`
(forward-delete), `up`, `down`, `left`, `right`, `f7`–`f12`, and `menu`
(the context-menu key).

Text is injected as Unicode (`KEYEVENTF_UNICODE` on Windows,
`CGEventKeyboardSetUnicodeString` on macOS), so the PC's keyboard layout
is irrelevant — `£` is `£` everywhere.

## Keepalive

```json
{"t":"ping"}   // client → listener
{"t":"pong"}   // listener → client
```

Clients ping every 10 s; a client that sees no pong for 30 s declares the
link dead and reconnects. Either side may simply close the socket.

## Security note

One gate: the per-device pairing code. A new device can't type until
someone standing at the PC reads the code off the screen and enters it
once; after that the device id authenticates it. This is *not* defence
against a determined attacker sniffing the unencrypted LAN traffic — it's
the right trade for a tool on a trusted home network. TLS-PSK is the
upgrade path if that ever changes.
