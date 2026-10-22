from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
import warnings
import urllib.request
import zipfile
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any

from .backup import BackupManager
from .config import ServerConfig
from .file_io import atomic_copy as _atomic_copy
from .file_io import atomic_write_bytes as _atomic_write
from .file_io import sha256_file as _sha256_file
from .live_state import LiveStateError, read_live_state
from .paths import DEPLOYMENT_PATHS, RuntimePaths
from .release import RuntimeManifest, create_runtime_manifest


PACKAGE_MANIFEST = "scbl-package.json"
PACKAGE_SCHEMA_VERSION = 3
PACKAGE_TYPES = {"full": "full", "patch": "update"}
MAX_PACKAGE_SIZE = 1536 * 1024 * 1024
MAX_EXPANDED_SIZE = 2 * 1024 * 1024 * 1024
MANAGER_TARGET = Path("/usr/local/lib/scbl/scblctl.pyz")
COMPONENT_VERSIONS_FILE = "component-versions.json"

COMPONENT_TARGETS: dict[str, str | None] = {
    "server.manager": None,
    "server.dedicated": "dedicated_server",
    "server.control-plane": "scbl_control_plane.pyz",
    "server.update-server": "scbl_update_server.py",
    "server.assets": "data/mp_balancing.ini",
}
COMPONENT_ARCHIVE_PATHS = {
    component: (
        "artifacts/server.manager/scblctl.pyz"
        if component == "server.manager"
        else f"artifacts/{component}/{target}"
    )
    for component, target in COMPONENT_TARGETS.items()
}
COMPONENT_RESTART_UNITS: dict[str, tuple[str, ...]] = {
    "server.manager": (),
    "server.dedicated": ("scbl-dedicated.service",),
    "server.control-plane": ("scbl-control-plane.service",),
    "server.update-server": ("scbl-update.service",),
    "server.assets": ("scbl-dedicated.service",),
}
RUNTIME_COMPONENTS = frozenset(COMPONENT_TARGETS) - {"server.manager"}
FULL_COMPONENTS = frozenset(COMPONENT_TARGETS)


class DeploymentError(RuntimeError):
    pass


@dataclass(frozen=True, slots=True)
class Artifact:
    component: str
    version: str
    path: str
    target: str | None
    size: int
    sha256: str


@dataclass(frozen=True, slots=True)
class RuntimeContract:
    program_arguments: dict[str, frozenset[str]]
    database_migrations: dict[int, str]


@dataclass(frozen=True, slots=True)
class DeploymentPackage:
    package_type: str
    version: str
    archive: Path
    artifacts: tuple[Artifact, ...]
    runtime_contract: RuntimeContract | None
    sha256: str


@dataclass(frozen=True, slots=True)
class DeploymentResult:
    operation: str
    package: DeploymentPackage
    applied: tuple[str, ...]
    backup: Path | None
    release: Path | None


@dataclass(frozen=True, slots=True)
class RollbackResult:
    previous: Path
    replaced: Path


def _requires_online_guard(config: ServerConfig, *, allow_online: bool) -> bool:
    """Protect stable players while allowing uninterrupted test deployments."""

    return config.server.mode != "test" and not allow_online


class DeploymentManager:
    def __init__(
        self,
        config: ServerConfig,
        paths: RuntimePaths,
    ) -> None:
        self.config = config
        self.paths = paths

    def verify(self, archive: Path, *, expected_kind: str) -> DeploymentPackage:
        if expected_kind not in PACKAGE_TYPES:
            raise DeploymentError(f"未知包类型：{expected_kind}")
        archive = archive.resolve(strict=True)
        if not archive.is_file() or archive.suffix.lower() != ".zip":
            raise DeploymentError("服务端部署包必须是普通 .zip 文件")
        if archive.stat().st_size > MAX_PACKAGE_SIZE:
            raise DeploymentError("部署包超过 1.5 GiB")
        try:
            bundle = zipfile.ZipFile(archive, "r")
        except zipfile.BadZipFile as exc:
            raise DeploymentError("部署包 ZIP 结构已损坏") from exc
        with bundle:
            infos = [item for item in bundle.infolist() if not item.is_dir()]
            if len(infos) > 64:
                raise DeploymentError("部署包文件数量超过限制")
            names = [item.filename.replace("\\", "/") for item in infos]
            if len(names) != len(set(names)) or any(not _safe_name(name) for name in names):
                raise DeploymentError("部署包包含不安全或重复路径")
            if sum(item.file_size for item in infos) > MAX_EXPANDED_SIZE:
                raise DeploymentError("部署包解压后超过 2 GiB")
            for info in infos:
                mode = (info.external_attr >> 16) & 0o170000
                if mode == 0o120000:
                    raise DeploymentError("部署包不能包含符号链接")
            if PACKAGE_MANIFEST not in names:
                raise DeploymentError("部署包缺少组件清单")
            manifest_bytes = bundle.read(PACKAGE_MANIFEST)
            try:
                raw: Any = json.loads(manifest_bytes)
            except (json.JSONDecodeError, UnicodeDecodeError) as exc:
                raise DeploymentError("部署包清单不是有效 UTF-8 JSON") from exc
            package_type, version, artifacts, contract = _parse_manifest(raw)
            if package_type != PACKAGE_TYPES[expected_kind]:
                raise DeploymentError(
                    f"操作需要 {PACKAGE_TYPES[expected_kind]} 包，实际为 {package_type}"
                )
            components = {item.component for item in artifacts}
            if expected_kind == "full" and components != FULL_COMPONENTS:
                missing = sorted(FULL_COMPONENTS - components)
                extra = sorted(components - FULL_COMPONENTS)
                detail = ", ".join(
                    [*("缺少 " + item for item in missing), *("多出 " + item for item in extra)]
                )
                raise DeploymentError("完整安装包组件集合不完整：" + detail)
            if components & RUNTIME_COMPONENTS and contract is None:
                raise DeploymentError("包含运行组件的部署包必须携带 runtimeContract")
            expected_names = {
                PACKAGE_MANIFEST,
                *(item.path for item in artifacts),
            }
            if set(names) != expected_names:
                raise DeploymentError("部署包实际文件与清单不一致")
            by_name = {item.filename.replace("\\", "/"): item for item in infos}
            for artifact in artifacts:
                info = by_name[artifact.path]
                if info.file_size != artifact.size:
                    raise DeploymentError(f"组件大小不符：{artifact.component}")
                with bundle.open(info) as source:
                    actual = _sha256_stream(source)
                if actual != artifact.sha256:
                    raise DeploymentError(f"组件 SHA256 不符：{artifact.component}")
        return DeploymentPackage(
            package_type,
            version,
            archive,
            artifacts,
            contract,
            _sha256_file(archive),
        )

    def apply(
        self, archive: Path, *, operation: str, allow_online: bool = False
    ) -> DeploymentResult:
        if operation not in {"install", "repair", "update"}:
            raise DeploymentError(f"未知部署操作：{operation}")
        expected_kind = "patch" if operation == "update" else "full"
        package = self.verify(archive, expected_kind=expected_kind)
        current_link = Path(DEPLOYMENT_PATHS.current)
        has_runtime = current_link.is_symlink()
        if operation == "install" and has_runtime:
            raise DeploymentError("检测到现有服务端；首次安装不能覆盖，请使用 update")
        if operation in {"repair", "update"} and not has_runtime:
            raise DeploymentError("尚未安装服务端；请先使用完整 ZIP 执行首次安装")
        self._validate_versions(package, operation=operation)

        runtime_artifacts = tuple(
            item for item in package.artifacts if item.component in RUNTIME_COMPONENTS
        )
        manager_artifact = next(
            (item for item in package.artifacts if item.component == "server.manager"),
            None,
        )
        if operation == "update" and manager_artifact is not None and runtime_artifacts:
            raise DeploymentError(
                "Manager 与 Runtime 必须使用独立组件更新包，不能在一次 update 中混合"
            )
        if has_runtime and runtime_artifacts and _requires_online_guard(
            self.config, allow_online=allow_online
        ):
            try:
                live = read_live_state(self.config)
            except LiveStateError as exc:
                raise DeploymentError(
                    f"{exc}；为避免误踢玩家，已阻止服务端更新"
                ) from exc
            if live.online_count:
                raise DeploymentError(
                    f"当前有 {live.online_count} 名玩家在线，已阻止服务端更新"
                )

        self._require_root()
        cache_root = Path(DEPLOYMENT_PATHS.cache)
        cache_root.mkdir(parents=True, exist_ok=True)
        backup = None
        release = None
        previous_release = current_link.resolve(strict=False) if has_runtime else None
        with tempfile.TemporaryDirectory(prefix="deployment-", dir=cache_root) as temporary:
            root = Path(temporary)
            extracted = self._extract_artifacts(package, root / "artifacts")
            self._preflight_components(package, extracted)
            stored = self._store_components(package, extracted)
            manager_target = MANAGER_TARGET
            manager_before = manager_target.read_bytes() if manager_target.is_file() else None
            manager_modified = False
            applied: list[str] = []
            candidate = None
            restart_units = None
            if runtime_artifacts:
                if has_runtime:
                    backup = BackupManager(self.paths).create().path
                candidate = self._assemble_candidate(
                    package,
                    stored,
                    root / "candidate",
                    current_link.resolve(strict=True) if has_runtime else None,
                )
                if previous_release is not None:
                    _replace_symlink(Path(DEPLOYMENT_PATHS.previous), previous_release)
                    restart_units = {
                        unit
                        for item in runtime_artifacts
                        for unit in COMPONENT_RESTART_UNITS[item.component]
                    }
            try:
                manager = stored.get("server.manager")
                if manager is not None:
                    _atomic_copy(manager, manager_target, 0o755)
                    manager_modified = True
                    applied.append("server.manager")
                if candidate is not None:
                    from .provision import Provisioner

                    release = Provisioner().install(
                        self.config,
                        candidate,
                        restart_units=restart_units,
                    )
                    applied.extend(item.component for item in runtime_artifacts)
            except Exception as exc:
                try:
                    if manager_modified:
                        self._rollback_manager(
                            manager_target=manager_target,
                            manager_before=manager_before,
                        )
                except Exception as rollback_error:
                    raise DeploymentError(
                        "部署失败且 Manager 回滚失败，需要人工检查："
                        f"deploy={exc}; rollback={rollback_error}"
                    ) from exc
                raise DeploymentError(f"部署失败，事务已回滚：{exc}") from exc
        if release is not None:
            try:
                from .maintenance import prune_obsolete_runtime_storage

                prune_obsolete_runtime_storage(apply=True)
            except (OSError, RuntimeError) as exc:
                warnings.warn(
                    f"部署已成功，但旧 Runtime 自动保留清理失败：{exc}",
                    RuntimeWarning,
                    stacklevel=2,
                )
        return DeploymentResult(
            operation,
            package,
            tuple(applied),
            backup,
            release,
        )

    def rollback(self, *, allow_online: bool = False) -> RollbackResult:
        current_link = Path(DEPLOYMENT_PATHS.current)
        previous_link = Path(DEPLOYMENT_PATHS.previous)
        if not current_link.is_symlink() or not previous_link.is_symlink():
            raise DeploymentError("当前版本或直接回滚版本不存在")
        current = current_link.resolve(strict=True)
        previous = previous_link.resolve(strict=True)
        if current == previous:
            raise DeploymentError("current 与 previous 指向同一版本")
        RuntimeManifest.load(current).verify(current)
        RuntimeManifest.load(previous).verify(previous)
        if _requires_online_guard(self.config, allow_online=allow_online):
            try:
                live = read_live_state(self.config)
            except LiveStateError as exc:
                raise DeploymentError(f"{exc}；已阻止回滚") from exc
            if live.online_count:
                raise DeploymentError(f"当前有 {live.online_count} 名玩家在线，已阻止回滚")
        self._require_root()
        _replace_symlink(previous_link, current)
        _replace_symlink(current_link, previous)
        try:
            from .provision import Provisioner

            Provisioner().repair(self.config)
        except Exception as exc:
            _replace_symlink(current_link, current)
            _replace_symlink(previous_link, previous)
            try:
                from .provision import Provisioner

                Provisioner().repair(self.config)
            except Exception as recovery_error:
                raise DeploymentError(
                    f"回滚失败且原版本恢复失败：rollback={exc}; recovery={recovery_error}"
                ) from exc
            raise DeploymentError(f"回滚失败，已恢复原版本：{exc}") from exc
        return RollbackResult(previous, current)


    def _validate_versions(self, package: DeploymentPackage, *, operation: str) -> None:
        installed = installed_components()
        newer = 0
        for artifact in package.artifacts:
            current = installed.get(artifact.component)
            if current is None:
                newer += 1
                continue
            current_version, current_sha256 = current
            comparison = _compare_versions(artifact.version, current_version)
            if comparison < 0:
                raise DeploymentError(
                    f"拒绝降级 {artifact.component}：当前 {current_version}，包内 {artifact.version}"
                )
            if comparison == 0 and artifact.sha256 != current_sha256:
                raise DeploymentError(
                    f"拒绝同版本不同字节 {artifact.component}@{artifact.version}"
                )
            if comparison > 0:
                newer += 1
        if operation == "update" and newer == 0:
            raise DeploymentError("更新包没有任何高于当前版本的组件")

    def _extract_artifacts(
        self, package: DeploymentPackage, destination: Path
    ) -> dict[str, Path]:
        result: dict[str, Path] = {}
        with zipfile.ZipFile(package.archive, "r") as bundle:
            for artifact in package.artifacts:
                target = destination / artifact.component
                target.parent.mkdir(parents=True, exist_ok=True)
                with bundle.open(artifact.path) as source, target.open("xb") as output:
                    shutil.copyfileobj(source, output, length=1024 * 1024)
                result[artifact.component] = target
        return result

    def _preflight_components(
        self, package: DeploymentPackage, extracted: dict[str, Path]
    ) -> None:
        for artifact in package.artifacts:
            if extracted[artifact.component].stat().st_size == 0:
                raise DeploymentError(f"组件为空：{artifact.component}")
        manager = extracted.get("server.manager")
        if manager is not None:
            result = subprocess.run(
                ("python3", str(manager), "--version"),
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=30,
                check=False,
            )
            artifact = next(x for x in package.artifacts if x.component == "server.manager")
            if result.returncode != 0 or artifact.version not in result.stdout:
                raise DeploymentError("server.manager 无法运行或内部版本不符")

    def _store_components(
        self, package: DeploymentPackage, extracted: dict[str, Path]
    ) -> dict[str, Path]:
        root = Path(DEPLOYMENT_PATHS.component_store)
        root.mkdir(parents=True, exist_ok=True)
        os.chmod(root, 0o755)
        result: dict[str, Path] = {}
        for artifact in package.artifacts:
            directory = root / artifact.sha256
            directory.mkdir(mode=0o755, exist_ok=True)
            target = directory / "payload"
            if target.exists():
                if (
                    not target.is_file()
                    or target.is_symlink()
                    or _sha256_file(target) != artifact.sha256
                ):
                    raise DeploymentError(f"组件仓库内容冲突：{artifact.component}")
            else:
                mode = (
                    0o755
                    if artifact.component
                    in {
                        "server.manager",
                        "server.dedicated",
                    }
                    else 0o644
                )
                _atomic_copy(extracted[artifact.component], target, mode)
            result[artifact.component] = target
        return result

    def _assemble_candidate(
        self,
        package: DeploymentPackage,
        stored: dict[str, Path],
        candidate: Path,
        current: Path | None,
    ) -> Path:
        candidate.mkdir(parents=True)
        component_versions: dict[str, dict[str, str]] = {}
        current_manifest: RuntimeManifest | None = None
        if current is not None:
            current_manifest = RuntimeManifest.load(current)
            current_manifest.verify(current)
            versions_path = current / COMPONENT_VERSIONS_FILE
            try:
                raw_versions = json.loads(versions_path.read_text(encoding="utf-8"))
            except (FileNotFoundError, json.JSONDecodeError) as exc:
                raise DeploymentError("当前版本缺少 SCBL 3.0 组件版本清单") from exc
            if not isinstance(raw_versions, dict):
                raise DeploymentError("当前组件版本清单无效")
            component_versions = raw_versions
            for name in current_manifest.files:
                if name == COMPONENT_VERSIONS_FILE:
                    continue
                source = current / Path(*PurePosixPath(name).parts)
                target = candidate / Path(*PurePosixPath(name).parts)
                target.parent.mkdir(parents=True, exist_ok=True)
                _link_or_copy(source, target)

        for artifact in package.artifacts:
            if artifact.target is None:
                continue
            target = candidate / Path(*PurePosixPath(artifact.target).parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.unlink(missing_ok=True)
            _link_or_copy(stored[artifact.component], target)
            component_versions[artifact.component] = {
                "version": artifact.version,
                "sha256": artifact.sha256,
                "target": artifact.target,
            }

        missing = sorted(RUNTIME_COMPONENTS - set(component_versions))
        if missing:
            raise DeploymentError("候选版本缺少运行组件：" + ", ".join(missing))
        _atomic_write(
            candidate / COMPONENT_VERSIONS_FILE,
            (
                json.dumps(
                    component_versions,
                    ensure_ascii=False,
                    indent=2,
                    sort_keys=True,
                )
                + "\n"
            ).encode(),
            0o644,
        )
        contract = package.runtime_contract
        if contract is None:
            if current_manifest is None or current_manifest.database_migrations is None:
                raise DeploymentError("候选版本缺少 runtimeContract")
            contract = RuntimeContract(
                dict(current_manifest.program_arguments),
                dict(current_manifest.database_migrations),
            )
        create_runtime_manifest(
            candidate,
            package.version,
            program_arguments=contract.program_arguments,
            database_migrations=contract.database_migrations,
        )
        return candidate

    @staticmethod
    def _require_root() -> None:
        if os.name != "posix" or os.geteuid() != 0:
            raise DeploymentError("部署需要在 Linux 服务端使用 root 执行")

    @staticmethod
    def _rollback_manager(*, manager_target: Path, manager_before: bytes | None) -> None:
        if manager_before is None:
            manager_target.unlink(missing_ok=True)
        else:
            _atomic_write(manager_target, manager_before, 0o755)


def installed_versions() -> dict[str, str]:
    return {
        component: version
        for component, (version, _sha256) in installed_components().items()
    }


def installed_components() -> dict[str, tuple[str, str]]:
    result: dict[str, tuple[str, str]] = {}
    manager_version = _installed_manager_version(MANAGER_TARGET)
    if manager_version is not None:
        result["server.manager"] = (manager_version, _sha256_file(MANAGER_TARGET))
    current = Path(DEPLOYMENT_PATHS.current)
    if current.is_symlink():
        versions = current.resolve(strict=True) / COMPONENT_VERSIONS_FILE
        try:
            raw: Any = json.loads(versions.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            raw = None
        if isinstance(raw, dict):
            for component, entry in raw.items():
                if (
                    component in RUNTIME_COMPONENTS
                    and isinstance(entry, dict)
                    and isinstance(entry.get("version"), str)
                    and isinstance(entry.get("sha256"), str)
                    and re.fullmatch(r"[0-9a-f]{64}", entry["sha256"])
                ):
                    result[component] = (entry["version"], entry["sha256"])
    return result


def _installed_manager_version(path: Path) -> str | None:
    if not path.is_file():
        return None
    try:
        result = subprocess.run(
            ("python3", str(path), "--version"),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=15,
            check=False,
        )
    except (OSError, subprocess.SubprocessError):
        return None
    if result.returncode != 0:
        return None
    match = re.search(r"(?<!\d)(\d+\.\d+\.\d+)(?!\d)", result.stdout)
    return match.group(1) if match else None


def online_package_url(config: ServerConfig, *, kind: str) -> str:
    if kind not in PACKAGE_TYPES:
        raise DeploymentError(f"未知包类型：{kind}")
    filename = "SCBL-Server-full.zip" if kind == "full" else "SCBL-Server-update.zip"
    return (
        f"https://github.com/{config.updates.repository}/releases/download/"
        f"server-latest/{filename}"
    )


def download_online_package(config: ServerConfig, *, kind: str, destination: Path) -> Path:
    url = online_package_url(config, kind=kind)
    request = urllib.request.Request(
        url, headers={"User-Agent": "SCBL-Server-Manager/3.0"}
    )
    destination.parent.mkdir(parents=True, exist_ok=True)
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            length = response.headers.get("Content-Length")
            if length and int(length) > MAX_PACKAGE_SIZE:
                raise DeploymentError("在线部署包超过 1.5 GiB")
            with destination.open("xb") as output:
                copied = 0
                while True:
                    chunk = response.read(1024 * 1024)
                    if not chunk:
                        break
                    copied += len(chunk)
                    if copied > MAX_PACKAGE_SIZE:
                        raise DeploymentError("在线部署包超过 1.5 GiB")
                    output.write(chunk)
    except Exception as exc:
        destination.unlink(missing_ok=True)
        if isinstance(exc, DeploymentError):
            raise
        raise DeploymentError(f"在线下载失败：{url}：{exc}") from exc
    return destination


def _parse_manifest(
    raw: Any,
) -> tuple[str, str, tuple[Artifact, ...], RuntimeContract | None]:
    if not isinstance(raw, dict):
        raise DeploymentError("部署包清单必须是 JSON 对象")
    required = {
        "schemaVersion",
        "packageType",
        "releaseVersion",
        "integrityPolicy",
        "components",
        "runtimeContract",
    }
    if set(raw) != required or raw.get("schemaVersion") != PACKAGE_SCHEMA_VERSION:
        raise DeploymentError("部署包清单字段或 schemaVersion 无效")
    package_type = raw.get("packageType")
    if package_type not in set(PACKAGE_TYPES.values()):
        raise DeploymentError("部署包 packageType 无效")
    version = raw.get("releaseVersion")
    if not isinstance(version, str) or not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise DeploymentError("部署包 releaseVersion 必须是 X.Y.Z")
    policy = raw.get("integrityPolicy")
    if policy != {"algorithm": "SHA256"}:
        raise DeploymentError("部署包 integrityPolicy 无效")
    raw_components = raw.get("components")
    if not isinstance(raw_components, list) or not raw_components:
        raise DeploymentError("部署包 components 不能为空")
    artifacts: list[Artifact] = []
    seen: set[str] = set()
    for item in raw_components:
        if not isinstance(item, dict) or set(item) != {
            "id",
            "version",
            "path",
            "size",
            "sha256",
        }:
            raise DeploymentError("部署包组件字段无效")
        component = item.get("id")
        if component not in COMPONENT_TARGETS or component in seen:
            raise DeploymentError(f"部署包组件未知或重复：{component}")
        seen.add(component)
        artifact_version = item.get("version")
        path = item.get("path")
        size = item.get("size")
        digest = item.get("sha256")
        if not isinstance(artifact_version, str) or not re.fullmatch(
            r"\d+\.\d+\.\d+", artifact_version
        ):
            raise DeploymentError(f"{component} 版本无效")
        if path != COMPONENT_ARCHIVE_PATHS[component]:
            raise DeploymentError(f"{component} 使用了非标准包内路径")
        if type(size) is not int or size <= 0 or size > MAX_EXPANDED_SIZE:
            raise DeploymentError(f"{component} 大小无效")
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest):
            raise DeploymentError(f"{component} SHA256 无效")
        artifacts.append(
            Artifact(
                component,
                artifact_version,
                path,
                COMPONENT_TARGETS[component],
                size,
                digest,
            )
        )
    contract = _parse_runtime_contract(raw.get("runtimeContract"))
    return package_type, version, tuple(artifacts), contract


def _parse_runtime_contract(raw: Any) -> RuntimeContract | None:
    if raw is None:
        return None
    if not isinstance(raw, dict) or set(raw) != {
        "programArguments",
        "databaseMigrations",
    }:
        raise DeploymentError("runtimeContract 字段无效")
    raw_arguments = raw.get("programArguments")
    raw_migrations = raw.get("databaseMigrations")
    if not isinstance(raw_arguments, dict) or not isinstance(raw_migrations, dict):
        raise DeploymentError("runtimeContract 内容无效")
    arguments: dict[str, frozenset[str]] = {}
    for program, values in raw_arguments.items():
        if not isinstance(program, str) or not isinstance(values, list) or not values:
            raise DeploymentError("runtimeContract programArguments 无效")
        if not all(
            isinstance(value, str)
            and re.fullmatch(r"--[a-z0-9][a-z0-9-]*", value)
            for value in values
        ):
            raise DeploymentError("runtimeContract 包含无效程序参数")
        if len(values) != len(set(values)):
            raise DeploymentError("runtimeContract 包含重复程序参数")
        arguments[program] = frozenset(values)
    migrations: dict[int, str] = {}
    for version, digest in raw_migrations.items():
        if not isinstance(version, str) or not version.isdigit():
            raise DeploymentError("runtimeContract migration 版本无效")
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{96}", digest):
            raise DeploymentError("runtimeContract migration 摘要无效")
        migrations[int(version)] = digest
    return RuntimeContract(arguments, migrations)


def _safe_name(name: str) -> bool:
    if not name or "\\" in name or "%" in name:
        return False
    path = PurePosixPath(name)
    return not path.is_absolute() and ".." not in path.parts and "" not in path.parts


def _sha256_stream(source: Any) -> str:
    digest = hashlib.sha256()
    for chunk in iter(lambda: source.read(1024 * 1024), b""):
        digest.update(chunk)
    return digest.hexdigest()


def _compare_versions(left: str, right: str) -> int:
    left_parts = tuple(int(part) for part in left.split("."))
    right_parts = tuple(int(part) for part in right.split("."))
    return (left_parts > right_parts) - (left_parts < right_parts)


def _link_or_copy(source: Path, destination: Path) -> None:
    try:
        os.link(source, destination)
    except OSError:
        shutil.copy2(source, destination, follow_symlinks=False)


def _replace_symlink(link: Path, target: Path) -> None:
    link.parent.mkdir(parents=True, exist_ok=True)
    temporary = link.with_name(f".{link.name}.{os.getpid()}.new")
    temporary.unlink(missing_ok=True)
    temporary.symlink_to(target, target_is_directory=True)
    os.replace(temporary, link)
