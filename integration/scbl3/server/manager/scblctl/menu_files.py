from __future__ import annotations

from datetime import datetime
from pathlib import Path


def select_server_file(
    title: str,
    *,
    suffixes: tuple[str, ...] = (),
    filenames: tuple[str, ...] = (),
    prefixes: tuple[str, ...] = (),
    cache: Path = Path("/var/cache/scbl"),
) -> Path | None:
    """List recent uploaded files and let the operator select by number."""
    normalized_suffixes = tuple(item.lower() for item in suffixes)
    normalized_filenames = {item.lower() for item in filenames}
    normalized_prefixes = tuple(item.lower() for item in prefixes)

    def matches(path: Path) -> bool:
        name = path.name.lower()
        if normalized_filenames and name in normalized_filenames:
            return True
        return bool(
            normalized_suffixes
            and path.suffix.lower() in normalized_suffixes
            and (not normalized_prefixes or name.startswith(normalized_prefixes))
        )

    cache.mkdir(parents=True, exist_ok=True)
    candidates = sorted(
        (path for path in cache.rglob("*") if path.is_file() and matches(path)),
        key=lambda path: path.stat().st_mtime,
        reverse=True,
    )[:20]

    print(f"\n{title}")
    for index, path in enumerate(candidates, start=1):
        stat = path.stat()
        relative = path.relative_to(cache)
        modified = datetime.fromtimestamp(stat.st_mtime).strftime("%Y-%m-%d %H:%M")
        print(
            f"  {index}. {relative}  "
            f"({stat.st_size / 1024 / 1024:.1f} MiB, {modified})"
        )
    manual = len(candidates) + 1
    print(f"  {manual}. 手动输入其他路径")
    print("  0. 取消")
    choice = input("请选择文件：").strip()
    if choice == "0":
        return None
    if choice.isdigit():
        selected = int(choice)
        if 1 <= selected <= len(candidates):
            return candidates[selected - 1]
        if selected == manual:
            value = input("文件路径：").strip()
            return Path(value) if value else None
    print("无效选择。")
    return None
