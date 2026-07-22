#!/usr/bin/env python3
"""Protocol regression test for the Remote Helper listener.

Run the listener with its stdout captured, then point --log at that file:
    dotnet run --project listener -f net7.0 -- --echo --no-mdns > out.log 2>&1 &
    python3 tools/test-client.py --log out.log

Exercises the full pairing flow (unknown device → code → paired), then a
second device, then a reconnect of the first device (now trusted, so no
code).
"""
import argparse
import json
import re
import socket
import sys
import time
from pathlib import Path

def jline(f, expect_t=None):
    line = f.readline()
    if not line:
        sys.exit("FAIL: listener closed the connection unexpectedly")
    msg = json.loads(line)
    if expect_t and msg.get("t") != expect_t:
        sys.exit(f"FAIL: expected t={expect_t!r}, got {msg}")
    return msg

def send(sock, obj):
    sock.sendall((json.dumps(obj) + "\n").encode())

def read_latest_pin(log_path, deadline=5.0):
    end = time.time() + deadline
    while time.time() < end:
        pins = re.findall(r"code\s+>>> (\d{6}) <<<",
                          Path(log_path).read_text(encoding="utf-8", errors="replace"))
        if pins:
            return pins[-1]
        time.sleep(0.2)
    sys.exit(f"FAIL: no pairing code appeared in {log_path}")

def pair(host, port, name, device_id, log_path):
    s = socket.create_connection((host, port), timeout=5)
    f = s.makefile("r", encoding="utf-8")
    send(s, {"t": "hello", "name": name, "deviceId": device_id})
    jline(f, "pair_required")
    send(s, {"t": "pair", "deviceId": device_id, "name": name, "pin": "000000"})
    bad = jline(f, "pair_failed")
    assert bad["attemptsLeft"] == 2, bad
    send(s, {"t": "pair", "deviceId": device_id, "name": name, "pin": read_latest_pin(log_path)})
    jline(f, "paired")
    print(f"ok: {name} paired (wrong code rejected first)")
    return s, f

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8737)
    ap.add_argument("--log", required=True, help="file the listener's stdout is written to")
    a = ap.parse_args()

    phone_id = "test-iphone-" + str(a.port)
    pad_id = "test-ipad-" + str(a.port)

    phone, phone_f = pair(a.host, a.port, "Test iPhone", phone_id, a.log)
    pad, pad_f = pair(a.host, a.port, "Test iPad", pad_id, a.log)

    send(phone, {"t": "text", "s": "Hello £10 → naïve"})
    send(phone, {"t": "key", "k": "f11"})
    send(pad, {"t": "text", "s": "iPad here"})
    send(phone, {"t": "ping"}); jline(phone_f, "pong")
    send(pad, {"t": "ping"}); jline(pad_f, "pong")
    print("ok: both devices typed and pinged concurrently")
    phone.close()

    # Reconnect the phone with its now-trusted id: straight to ok, no code.
    s2 = socket.create_connection((a.host, a.port), timeout=5)
    f2 = s2.makefile("r", encoding="utf-8")
    send(s2, {"t": "hello", "name": "Test iPhone", "deviceId": phone_id})
    jline(f2, "ok")
    print("ok: trusted device reconnected without pairing")
    s2.close(); pad.close()

    print("\nALL PROTOCOL TESTS PASSED")

if __name__ == "__main__":
    main()
