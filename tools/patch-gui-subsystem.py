#!/usr/bin/env python3
"""Flip a Windows exe's PE subsystem from console to GUI.

Cross-compiling a WinExe from macOS skips the apphost customisation step
(warning NETSDK1074), which leaves the exe marked as a console app — so
Windows opens a terminal window alongside it, and closing that terminal
kills the app. This applies the same subsystem patch the SDK would have
applied had the build run on Windows.
"""
import struct
import sys

path = sys.argv[1]
with open(path, "rb") as f:
    data = bytearray(f.read())

e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
assert data[e_lfanew:e_lfanew + 4] == b"PE\x00\x00", "not a PE file"
opt_header = e_lfanew + 4 + 20
magic = struct.unpack_from("<H", data, opt_header)[0]
assert magic in (0x10B, 0x20B), f"unexpected optional header magic {magic:#x}"

subsystem_offset = opt_header + 68
old = struct.unpack_from("<H", data, subsystem_offset)[0]
if old == 2:
    print(f"{path}: already GUI subsystem, nothing to do")
elif old == 3:
    struct.pack_into("<H", data, subsystem_offset, 2)
    with open(path, "wb") as f:
        f.write(data)
    print(f"{path}: patched subsystem console(3) -> GUI(2)")
else:
    sys.exit(f"{path}: unexpected subsystem {old}, refusing to touch it")
