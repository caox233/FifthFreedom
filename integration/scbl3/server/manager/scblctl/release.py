from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import tempfile
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path, PurePosixPath
from typing import Any

from .file_io import atomic_write_text as _atomic_write
from .file_io import sha256_file as _sha256

MANIFEST_NAME = "server-package.json"
RUNTIME_MANIFEST_SCHEMA_VERSION = 2
REQUIRED_RUNTIME_FILES = frozenset(
    {
        "dedicated_server",
        "scbl_control_plane.pyz",
        "scbl_update_server.py",
        "data/mp_balancing.ini",
    }
)
EXECUTABLE_FILES = frozenset({"dedicated_server"})
UNIT_PROGRAM_ARGUMENTS = {
    "dedicated_server": frozenset({"--config"}),
    "scbl_update_server.py": frozenset(
        {"--root", "--port", "--ipv6", "--bundle-cache"}
    ),
}
_PROGRAM_ARGUMENT_PATTERN = re.compile(r"--[a-z0-9][a-z0-9-]*")


class ReleaseError(ValueError):
    pass


@dataclass(frozen=True, slots=True)
class RuntimeManifest:
    schema_version: int
    version: str
    files: dict[str, str]
    program_arguments: dict[str, frozenset[str]]
    database_migrations: dict[int, str] | None

    @classmethod
    def load(cls, package_dir: Path) -> "RuntimeManifest":
        path = package_dir / MANIFEST_NAME
        try:
            raw: Any = json.loads(path.read_text(encoding="utf-8"))
        except FileNotFoundError as exc:
            raise ReleaseError(f"运行时包缺少 {MANIFEST_NAME}") from exc
        except json.JSONDecodeError as exc:
            raise ReleaseError(f"{MANIFEST_NAME} 不是有效 JSON：{exc}") from exc
        if not isinstance(raw, dict):
            raise ReleaseError("运行时清单必须是 JSON 对象")
        schema_version = raw.get("schemaVersion")
        if schema_version != RUNTIME_MANIFEST_SCHEMA_VERSION:
            raise ReleaseError("不支持的运行时清单版本")
        if raw.get("packageType") != "scbl-server-runtime":
            raise ReleaseError("补丁包类型不是 scbl-server-runtime")
        version = raw.get("version")
        if not isinstance(version, str) or not re.fullmatch(r"\d+\.\d+\.\d+", version):
            raise ReleaseError("运行时版本必须是 X.Y.Z")
        files = raw.get("files")
        if not isinstance(files, dict) or not files:
            raise ReleaseError("运行时清单 files 不能为空")
        normalized: dict[str, str] = {}
        for name, digest in files.items():
            if not isinstance(name, str) or not _safe_relative_path(name):
                raise ReleaseError(f"不安全的运行时文件路径：{name!r}")
            if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", digest):
                raise ReleaseError(f"{name} 的 SHA256 无效")
            normalized[name] = digest.lower()
        missing = sorted(REQUIRED_RUNTIME_FILES - set(normalized))
        if missing:
            raise ReleaseError("运行时包缺少必要文件：" + ", ".join(missing))
        program_arguments = _parse_program_arguments(
            raw.get("programArguments"), normalized
        )
        database_migrations = _parse_database_migrations(
            raw.get("databaseMigrations")
        )
        return cls(
            schema_version,
            version,
            normalized,
            program_arguments,
            database_migrations,
        )

    def verify(self, package_dir: Path) -> None:
        root = package_dir.resolve()
        for name, expected in self.files.items():
            path = package_dir / Path(*PurePosixPath(name).parts)
            try:
                resolved = path.resolve(strict=True)
            except FileNotFoundError as exc:
                raise ReleaseError(f"清单文件不存在：{name}") from exc
            if root not in resolved.parents or not resolved.is_file() or path.is_symlink():
                raise ReleaseError(f"运行时文件不是包内普通文件：{name}")
            actual = _sha256(resolved)
            if actual != expected:
                raise ReleaseError(f"运行时文件校验失败：{name}")

    def require_program_arguments(
        self, program: str, required: set[str] | frozenset[str]
    ) -> None:
        declared = self.program_arguments.get(program)
        if declared is None:
            raise ReleaseError(f"运行时没有声明程序参数能力：{program}")
        missing = sorted(required - declared)
        if missing:
            raise ReleaseError(
                f"运行时程序 {program} 不支持 unit 参数：" + ", ".join(missing)
            )

    def supports_database_checkpoint(self, checkpoint: dict[int, str]) -> bool:
        if self.database_migrations is None:
            return not checkpoint
        return all(
            self.database_migrations.get(version) == digest
            for version, digest in checkpoint.items()
        )


def _safe_relative_path(name: str) -> bool:
    path = PurePosixPath(name)
    return bool(name) and not path.is_absolute() and ".." not in path.parts and "" not in path.parts


def _parse_program_arguments(
    raw: Any, files: dict[str, str]
) -> dict[str, frozenset[str]]:
    if not isinstance(raw, dict) or not raw:
        raise ReleaseError("运行时清单 programArguments 不能为空")
    result: dict[str, frozenset[str]] = {}
    for program, values in raw.items():
        if program not in files:
            raise ReleaseError(f"参数能力引用包外程序：{program}")
        if not isinstance(values, list) or not values:
            raise ReleaseError(f"{program} 的参数能力不能为空")
        arguments: list[str] = []
        for value in values:
            if not isinstance(value, str) or not _PROGRAM_ARGUMENT_PATTERN.fullmatch(value):
                raise ReleaseError(f"{program} 包含无效参数能力")
            arguments.append(value)
        if len(arguments) != len(set(arguments)):
            raise ReleaseError(f"{program} 包含重复参数能力")
        result[program] = frozenset(arguments)
    for program, required in UNIT_PROGRAM_ARGUMENTS.items():
        if program not in result:
            raise ReleaseError(f"运行时清单缺少程序参数能力：{program}")
        missing = sorted(required - result[program])
        if missing:
            raise ReleaseError(
                f"运行时程序 {program} 缺少基础 unit 参数：" + ", ".join(missing)
            )
    return result


def _parse_database_migrations(raw: Any) -> dict[int, str]:
    if not isinstance(raw, dict) or not raw:
        raise ReleaseError("运行时清单 databaseMigrations 不能为空")
    result: dict[int, str] = {}
    for version_text, digest in raw.items():
        if not isinstance(version_text, str) or not version_text.isdigit():
            raise ReleaseError("运行时清单包含无效 migration 版本")
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-fA-F]{96}", digest):
            raise ReleaseError(f"migration {version_text} 的 SHA384 无效")
        version = int(version_text)
        if version in result:
            raise ReleaseError(f"运行时清单包含重复 migration：{version_text}")
        result[version] = digest.lower()
    return result


def create_runtime_manifest(
    package_dir: Path,
    version: str,
    *,
    program_arguments: dict[str, frozenset[str]],
    database_migrations: dict[int, str],
) -> RuntimeManifest:
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ReleaseError("运行时版本必须是 X.Y.Z")
    files: dict[str, str] = {}
    for path in sorted(package_dir.rglob("*")):
        if not path.is_file() or path.name == MANIFEST_NAME:
            continue
        if path.is_symlink():
            raise ReleaseError(f"运行时包不能包含符号链接：{path}")
        name = path.relative_to(package_dir).as_posix()
        if not _safe_relative_path(name):
            raise ReleaseError(f"不安全的运行时文件路径：{name}")
        files[name] = _sha256(path)
    normalized_arguments = _parse_program_arguments(
        {program: sorted(values) for program, values in program_arguments.items()},
        files,
    )
    normalized_migrations = _parse_database_migrations(
        {str(key): value for key, value in database_migrations.items()}
    )
    manifest = RuntimeManifest(
        RUNTIME_MANIFEST_SCHEMA_VERSION,
        version,
        files,
        normalized_arguments,
        normalized_migrations,
    )
    missing = sorted(REQUIRED_RUNTIME_FILES - set(files))
    if missing:
        raise ReleaseError("运行时包缺少必要文件：" + ", ".join(missing))
    payload = {
        "schemaVersion": RUNTIME_MANIFEST_SCHEMA_VERSION,
        "packageType": "scbl-server-runtime",
        "version": version,
        "files": files,
        "programArguments": {
            program: sorted(arguments)
            for program, arguments in sorted(normalized_arguments.items())
        },
        "databaseMigrations": {
            str(migration): digest
            for migration, digest in sorted(normalized_migrations.items())
        },
    }
    _atomic_write(package_dir / MANIFEST_NAME, json.dumps(payload, indent=2) + "\n", 0o644)
    return manifest


def stage_release(package_dir: Path, releases_dir: Path) -> tuple[Path, RuntimeManifest]:
    manifest = RuntimeManifest.load(package_dir)
    manifest.verify(package_dir)
    release_id = f"{manifest.version}-{datetime.now(UTC).strftime('%Y%m%d%H%M%S')}"
    target = releases_dir / release_id
    releases_dir.mkdir(parents=True, exist_ok=True)
    if target.exists():
        raise ReleaseError(f"发布目录已存在：{target}")
    temporary = Path(tempfile.mkdtemp(prefix=f".{release_id}.", dir=releases_dir))
    try:
        os.chmod(temporary, 0o755)
        for name in manifest.files:
            source = package_dir / Path(*PurePosixPath(name).parts)
            destination = temporary / Path(*PurePosixPath(name).parts)
            destination.parent.mkdir(parents=True, exist_ok=True)
            _make_parents_traversable(destination.parent, temporary)
            try:
                os.link(source, destination)
            except OSError:
                shutil.copy2(source, destination, follow_symlinks=False)
            os.chmod(destination, 0o755 if name in EXECUTABLE_FILES else 0o644)
        shutil.copy2(package_dir / MANIFEST_NAME, temporary / MANIFEST_NAME)
        manifest.verify(temporary)
        os.replace(temporary, target)
    except Exception:
        shutil.rmtree(temporary, ignore_errors=True)
        raise
    return target, manifest


def _make_parents_traversable(directory: Path, release_root: Path) -> None:
    current = directory
    while current != release_root.parent:
        os.chmod(current, 0o755)
        if current == release_root:
            break
        current = current.parent


def activate_release(target: Path, current_link: Path) -> Path | None:
    if not target.is_dir():
        raise ReleaseError(f"发布目录不存在：{target}")
    previous: Path | None = None
    if current_link.is_symlink():
        previous = current_link.resolve(strict=False)
    elif current_link.exists():
        raise ReleaseError(f"current 必须是符号链接：{current_link}")
    current_link.parent.mkdir(parents=True, exist_ok=True)
    temporary = current_link.with_name(f".{current_link.name}.{os.getpid()}.new")
    temporary.unlink(missing_ok=True)
    temporary.symlink_to(target, target_is_directory=True)
    os.replace(temporary, current_link)
    return previous
