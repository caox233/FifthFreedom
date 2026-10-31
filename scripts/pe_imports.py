"""Bounded static DLL-name inventory; never loads or executes a PE image.

This inventories normal and delay-load directory names only. It does not resolve
the DLL search path, inspect forwarded imports, or establish runtime readiness.
"""
import re
import struct


def pe_imports(data: bytes) -> dict:
    """Read PE32/PE32+ import names, rejecting ambiguous or truncated inputs."""
    def require(condition):
        if not condition:
            raise ValueError("PE_IMPORTS_INVALID")

    require(isinstance(data, bytes) and 64 <= len(data) <= 256 * 1024 * 1024)

    def unpack(fmt, offset):
        size = struct.calcsize(fmt)
        require(0 <= offset <= len(data) - size)
        return struct.unpack_from(fmt, data, offset)

    require(data[:2] == b"MZ")
    pe, = unpack("<I", 60)
    require(64 <= pe <= len(data) - 24 and data[pe:pe + 4] == b"PE\0\0")
    count, = unpack("<H", pe + 6)
    optional_size, = unpack("<H", pe + 20)
    optional = pe + 24
    require(1 <= count <= 96 and optional + optional_size <= len(data))
    magic, = unpack("<H", optional)
    require(magic in (0x10B, 0x20B))
    directory_start = 96 if magic == 0x10B else 112
    require(optional_size >= directory_start)
    image_base, = unpack("<I" if magic == 0x10B else "<Q",
                         optional + (28 if magic == 0x10B else 24))
    header_size, = unpack("<I", optional + 60)
    directories, = unpack("<I", optional + directory_start - 4)
    require(directories <= 16 and directory_start + directories * 8 <= optional_size)
    sections_at = optional + optional_size
    require(sections_at + count * 40 <= header_size <= len(data))
    sections = []
    for number in range(count):
        offset = sections_at + number * 40
        virtual_size, address, raw_size, raw_offset = unpack("<IIII", offset + 8)
        span = max(virtual_size, raw_size)
        require(span > 0 and address >= header_size and address + span <= 2**32)
        require(raw_size == 0 or header_size <= raw_offset <= len(data) - raw_size)
        for prior_address, prior_span, prior_raw, prior_offset in sections:
            require(address + span <= prior_address or prior_address + prior_span <= address)
            if raw_size and prior_raw:
                require(raw_offset + raw_size <= prior_offset
                        or prior_offset + prior_raw <= raw_offset)
        sections.append((address, span, raw_size, raw_offset))

    def mapped(address, size):
        require(0 < address < 2**32 and 0 < size <= 1024 * 1024
                and address + size <= 2**32)
        if address < header_size:
            require(address + size <= header_size)
            return address, header_size - address
        matches = [(raw_offset + address - start, raw_size - (address - start))
                   for start, span, raw_size, raw_offset in sections
                   if start <= address and address + size <= start + raw_size]
        require(len(matches) == 1)
        return matches[0]

    def dll_name(address):
        offset, available = mapped(address, 1)
        raw = data[offset:offset + min(available, 256)]
        end = raw.find(b"\0")
        require(end > 0)
        name = raw[:end]
        require(re.fullmatch(rb"[A-Za-z0-9][A-Za-z0-9_.-]{0,249}\.dll", name,
                             flags=re.IGNORECASE) is not None)
        return name.decode("ascii").lower()

    def inventory(index, stride, delayed):
        if directories <= index:
            return []
        address, size = unpack("<II", optional + directory_start + index * 8)
        if address == 0 and size == 0:
            return []
        require(address != 0 and stride <= size <= 1024 * 1024)
        offset, _ = mapped(address, size)
        names = set()
        for number in range(min(size // stride, 129)):
            row = unpack("<" + "I" * (stride // 4), offset + number * stride)
            if not any(row):
                return sorted(names)
            require(number < 128)
            if delayed:
                attributes, name = row[:2]
                require(attributes in (0, 1))
                if attributes == 0:
                    require(name >= image_base)
                    name -= image_base
            else:
                name = row[3]
            names.add(dll_name(name))
        raise ValueError("PE_IMPORTS_INVALID")

    return {"schema": 1, "normal": inventory(1, 20, False),
            "delay": inventory(13, 32, True)}
