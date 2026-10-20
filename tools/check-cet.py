#!/usr/bin/env python3
"""Fail if a PE image opts into CET (hardware-enforced stack protection).

.NET 8+ marks the generated apphost with IMAGE_DLLCHARACTERISTICS_EX_CET_COMPAT by default.
On a Windows 10 install whose ntdll predates the 2023 CET kernel fixes, coreclr fail-fasts
during startup before any managed code runs:

    Fatal error.
    Your Windows doesn't fully support CET. Please install all available Windows updates.

The marking lives in the PE debug directory, entry type 20
(IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS), bit 0 - not in the classic DllCharacteristics word,
where 0x8000 is TERMINAL_SERVER_AWARE. Checking the wrong field would silently pass a broken
build, so this reads the debug entry specifically.

Usage:
    python tools/check-cet.py <file.exe> [more.exe ...]

Exit code 0 means every file is CET-free; 1 means at least one is marked or unreadable.
"""

import struct
import sys

IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS = 20
IMAGE_DLLCHARACTERISTICS_EX_CET_COMPAT = 0x0001


def read_headers(data):
    """Return (section count, debug directory offset, section table offset)."""
    if data[:2] != b"MZ":
        raise ValueError("not a PE image (missing MZ)")
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    if data[e_lfanew:e_lfanew + 4] != b"PE\x00\x00":
        raise ValueError("not a PE image (missing PE signature)")
    coff = e_lfanew + 4
    sections = struct.unpack_from("<H", data, coff + 2)[0]
    optional_size = struct.unpack_from("<H", data, coff + 16)[0]
    optional = coff + 20
    magic = struct.unpack_from("<H", data, optional)[0]
    plus = magic == 0x20B
    directories = optional + (112 if plus else 96)
    table = optional + optional_size
    return sections, directories, table


def rva_to_offset(data, table, sections, rva):
    for index in range(sections):
        base = table + index * 40
        vsize, vaddr = struct.unpack_from("<II", data, base + 8)
        rawsize, rawptr = struct.unpack_from("<II", data, base + 16)
        if vaddr <= rva < vaddr + max(vsize, rawsize):
            return rawptr + (rva - vaddr)
    return None


def cet_state(path):
    """Return (has_entry, value) for the EX_DLLCHARACTERISTICS debug entry."""
    with open(path, "rb") as handle:
        data = handle.read()
    sections, directories, table = read_headers(data)
    debug_rva, debug_size = struct.unpack_from("<II", data, directories + 6 * 8)
    offset = rva_to_offset(data, table, sections, debug_rva)
    if offset is None:
        return False, None
    for index in range(debug_size // 28):
        base = offset + index * 28
        fields = struct.unpack_from("<IIHHIIII", data, base)
        dtype, dptr, daddr = fields[4], fields[7], fields[6]
        if dtype != IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS:
            continue
        location = dptr if dptr else rva_to_offset(data, table, sections, daddr)
        if location is None or location + 4 > len(data):
            return False, None
        return True, struct.unpack_from("<I", data, location)[0]
    return False, None


def main(argv):
    if len(argv) < 2:
        print(__doc__.strip(), file=sys.stderr)
        return 2

    failed = False
    for path in argv[1:]:
        try:
            has_entry, value = cet_state(path)
        except (OSError, ValueError, struct.error) as error:
            print(f"FAIL  {path}: cannot read PE headers ({error})")
            failed = True
            continue

        if not has_entry:
            # No marking at all: the loader cannot arm shadow stacks, which is what we want.
            print(f"OK    {path}: no EX_DLLCHARACTERISTICS entry (CET not requested)")
            continue

        if value & IMAGE_DLLCHARACTERISTICS_EX_CET_COMPAT:
            print(f"FAIL  {path}: CET_COMPAT is set (value=0x{value:08X}); "
                  "this image fail-fasts on unpatched Windows 10")
            failed = True
        else:
            print(f"OK    {path}: CET_COMPAT is clear (value=0x{value:08X})")

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
