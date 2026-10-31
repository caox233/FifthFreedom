"""Inventory x86 detour declarations without claiming a native ABI match.

This reads Rust source only. It includes lexically present test/feature-disabled
declarations; it does not resolve cfg, execute a DLL, infer a retail function
signature, or cover raw callsite patches and manually built virtual tables.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess


def source_mask(text: str, *, literals: bool = False) -> str:
    """Keep offsets while removing comments and, optionally, literals."""
    out = list(text)
    i = 0
    while i < len(text):
        raw = re.match(r'(?:b|c)?r(#+)?"', text[i:])
        if raw:
            start = i
            end = text.find('"' + (raw[1] or ''), i + raw.end())
            if end < 0:
                raise ValueError("unterminated raw string")
            i = end + 1 + len(raw[1] or '')
            if literals:
                out[start:i] = ['\n' if c == '\n' else ' ' for c in text[start:i]]
        elif text[i] == '"':
            start = i
            i += 1
            while i < len(text):
                if text[i] == '\\':
                    i += 2
                elif text[i] == '"':
                    i += 1
                    break
                else:
                    i += 1
            if literals:
                out[start:i] = ['\n' if c == '\n' else ' ' for c in text[start:i]]
        elif text[i] == "'" and (character := re.match(r"'(?:\\.|[^'\\\n])'", text[i:])):
            if literals:
                out[i:i + character.end()] = [' '] * character.end()
            i += character.end()
        elif text.startswith('//', i) or text.startswith('/*', i):
            start = i
            if text.startswith('//', i):
                end = text.find('\n', i)
                i = len(text) if end < 0 else end
            else:
                i += 2
                depth = 1
                while i < len(text) and depth:
                    if text.startswith('/*', i):
                        depth += 1
                        i += 2
                    elif text.startswith('*/', i):
                        depth -= 1
                        i += 2
                    else:
                        i += 1
                if depth:
                    raise ValueError("unterminated block comment")
            out[start:i] = ['\n' if c == '\n' else ' ' for c in text[start:i]]
        else:
            i += 1
    return ''.join(out)


def word_bytes(value: str) -> int | None:
    if value.startswith(('*', '&')):
        # Trait/slice/string pointers and references may carry metadata. This
        # inventory deliberately does not infer layouts of unsized aliases.
        if re.search(r'\b(?:dyn|str)\b|[\[\]<>]', value):
            return None
        return 4
    if value in {'usize', 'isize', 'u32', 'i32', 'u16', 'i16', 'u8', 'i8', 'bool', 'f32', 'c_int', 'c_uint'}:
        return 4
    if value in {'u64', 'i64', 'f64'}:
        return 8
    return None


def layout(abi: str, arguments: list[str], result: str) -> dict:
    receiver = 'none'
    stack = arguments
    if abi == 'thiscall':
        if (not arguments or word_bytes(arguments[0]) != 4
                or not (arguments[0].startswith(('*', '&')) or arguments[0] in {'usize', 'isize', 'u32', 'i32'})):
            return {'status': 'unsupported_receiver_shape'}
        receiver = 'ECX: argument 0'
        stack = arguments[1:]
    elif abi not in {'C', 'cdecl', 'stdcall', 'system'}:
        return {'status': 'unsupported_calling_convention'}
    widths = [word_bytes(value) for value in stack]
    if any(value is None for value in widths):
        return {'status': 'unknown_stack_type', 'receiver': receiver}
    count = sum(widths)
    if result == '()':
        returned = 'no declared value'
    elif result in {'u8', 'i8', 'bool'}:
        returned = 'AL'
    elif result in {'u16', 'i16'}:
        returned = 'AX'
    elif result in {'f32', 'f64'}:
        returned = 'x87 ST(0)'
    elif result in {'u64', 'i64'}:
        returned = 'EDX:EAX'
    elif word_bytes(result) == 4:
        returned = 'EAX'
    else:
        return {'status': 'unknown_return_type_possible_hidden_arguments', 'receiver': receiver}
    return {'status': 'declared_i686_msvc_scalar_layout_only', 'receiver': receiver,
            'stack_argument_bytes': count,
            'callee_pop_bytes': 0 if abi in {'C', 'cdecl'} else count,
            'caller_pop_bytes': count if abi in {'C', 'cdecl'} else 0,
            'return_location': returned}


def inventory(root: Path, revision: str | None = None) -> dict:
    entries = []
    hashes = {}
    commit = subprocess.check_output(['git', 'rev-parse', '--verify', (revision or 'HEAD') + '^{commit}'], cwd=root, text=True).strip()
    if revision:
        names = subprocess.check_output(['git', 'ls-tree', '-rz', '--name-only', commit, '--', 'src/native/client/hooks'], cwd=root).decode('utf-8').split('\0')
        files = sorted(name for name in names if name.endswith('.rs'))
    else:
        files = sorted(path.relative_to(root).as_posix() for path in (root / 'src/native/client/hooks').rglob('*.rs'))
    for name in files:
        raw = subprocess.check_output(['git', 'show', f'{commit}:{name}'], cwd=root) if revision else (root / name).read_bytes()
        text = raw.decode('utf-8')
        clean = source_mask(text)
        masked = source_mask(text, literals=True)
        # Detour declarations contain no function bodies. Refuse an unsupported
        # declaration instead of silently assigning an argument/cleanup count.
        blocks = list(re.finditer(r'\bstatic_detour\s*!\s*\{([^{}]*)\}', masked, re.S))
        if len(blocks) != len(re.findall(r'\bstatic_detour\s*!', masked)):
            raise ValueError(f'unsupported detour macro shape in {name}')
        for block in blocks:
            body = clean[block.start(1):block.end(1)]
            pattern = r'\bstatic\s+(\w+)\s*:\s*(?:unsafe\s+)?extern\s+"([^"]+)"\s+fn\s*\(([^()]*)\)\s*(?:->\s*([^;]+))?;'
            found = list(re.finditer(pattern, body, re.S))
            if len(found) != len(re.findall(r'\bstatic\s+\w+\s*:', body)):
                raise ValueError(f"unsupported detour declaration in {name}")
            for match in found:
                hashes[name] = hashlib.sha256(raw).hexdigest()
                arguments = [' '.join(value.split()) for value in match[3].split(',') if value.strip()]
                result = ' '.join((match[4] or '()').split())
                entries.append({'file': name, 'line': text.count('\n', 0, block.start(1) + match.start()) + 1,
                                'name': match[1], 'abi': match[2], 'arguments': arguments, 'return_type': result,
                                'declared_layout': layout(match[2], arguments, result)})
    if not entries:
        raise ValueError('no detour declarations found')
    if not revision:
        for name, digest in hashes.items():
            if hashlib.sha256((root / name).read_bytes()).hexdigest() != digest:
                raise ValueError(f'source changed during audit: {name}')
    return {'schema': 1, 'source_commit': commit,
            'source_basis': 'immutable_git_tree' if revision else 'working_tree_with_per_file_hashes_not_a_clean_commit_claim',
            'scope': 'Lexical static_detour declarations, including test and feature-disabled code; cfg activation unresolved.',
            'limitations': ['No retail signature/ret opcode verification or runtime execution.',
                            'Declared cleanup is not evidence that the native target uses that cleanup.',
                            'Lexical Rust subset only, not a type resolver; simple named pointer targets are treated as sized.',
                            'Raw callsite patches, indirect native function pointers and manually built vtables require separate review.'],
            'source_sha256': hashes, 'declarations': entries}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--revision', help='Read an immutable Git commit without checking it out; default reads the working tree.')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    result = json.dumps(inventory(args.root.resolve(), args.revision), indent=2, ensure_ascii=False) + '\n'
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(result, encoding='utf-8')
    else:
        print(result, end='')


if __name__ == '__main__':
    main()
