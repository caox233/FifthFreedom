from __future__ import annotations

import hashlib
import json
import zipfile
from pathlib import Path, PurePosixPath
from typing import Any

from .client_components import COMPONENTS, ClientComponentError, ClientComponentPublisher
from .client_publish import ClientPackage, ClientPublishError, ClientPublisher


DIRECT_COMPONENT_PATHS = {
    "launcher": "SCBL.exe",
    "updater": "SCBL.Updater.exe",
    "hooks": "components/hooks/uplay_r1_loader.dll",
}
ZIP_COMPONENT_PATHS = {
    "route-guard": {
        "scbl-process-router.exe": "components/route-guard/scbl-process-router.exe",
        "WinDivert.dll": "components/route-guard/WinDivert.dll",
        "WinDivert64.sys": "components/route-guard/WinDivert64.payload.sys",
    },
    "easytier": {
        "easytier-core.exe": "components/network/easytier-core.exe",
        "easytier-cli.exe": "components/network/easytier-cli.exe",
        "Packet.dll": "components/network/Packet.dll",
        "wintun.dll": "components/network/wintun.dll",
    },
}


class ClientPackageAssemblyError(RuntimeError):
    pass


class ClientPackageAssembler:
    def __init__(self, root: Path, *, channel: str) -> None:
        self.root = root
        self.channel = channel
        self.components = ClientComponentPublisher(root, refresh_descriptor=False)

    def build(self, output_dir: Path) -> ClientPackage:
        try:
            manifest = self.components.load_manifest(self.channel)
        except ClientComponentError as exc:
            raise ClientPackageAssemblyError(str(exc)) from exc
        entries = {**manifest["components"], **manifest["files"]}
        if set(entries) != set(COMPONENTS):
            missing = sorted(set(COMPONENTS) - set(entries))
            extra = sorted(set(entries) - set(COMPONENTS))
            raise ClientPackageAssemblyError(
                f"完整包需要当前六个组件；缺少={missing or '-'}，多余={extra or '-'}"
            )
        for component, entry in entries.items():
            try:
                self.components._verify_entry(component, entry)
            except ClientComponentError as exc:
                raise ClientPackageAssemblyError(str(exc)) from exc
        generated_at = manifest.get("generatedAt")
        if not isinstance(generated_at, str) or not generated_at.strip():
            raise ClientPackageAssemblyError("当前组件清单缺少生成时间")

        version = entries["launcher"]["version"]
        payloads: dict[str, bytes] = {}
        receipt_files: dict[str, dict[str, str]] = {}

        for component, installed_path in DIRECT_COMPONENT_PATHS.items():
            payload = self._artifact(component, entries[component]).read_bytes()
            self._add_payload(payloads, installed_path, payload)
            receipt_files[component] = {installed_path: _sha256_bytes(payload)}

        hooks_digest = receipt_files["hooks"][DIRECT_COMPONENT_PATHS["hooks"]]
        hook_sidecar = f"{hooks_digest}  uplay_r1_loader.dll\n".encode("ascii")
        self._add_payload(
            payloads,
            "components/hooks/uplay_r1_loader.dll.sha256",
            hook_sidecar,
        )

        for component, mapping in ZIP_COMPONENT_PATHS.items():
            installed = self._extract_exact_zip(
                self._artifact(component, entries[component]), mapping
            )
            receipt_files[component] = {}
            for installed_path, payload in installed.items():
                self._add_payload(payloads, installed_path, payload)
                receipt_files[component][installed_path] = _sha256_bytes(payload)

        client_files = self._extract_client_files(
            self._artifact("client-files", entries["client-files"])
        )
        receipt_files["client-files"] = {}
        for installed_path, payload in client_files.items():
            self._add_payload(payloads, installed_path, payload)
            receipt_files["client-files"][installed_path] = _sha256_bytes(payload)

        required_mod = {
            "components/hooks/mod/scbl_gameplay.dll",
            "components/hooks/mod/scbl_gameplay.dll.sha256",
        }
        if not required_mod.issubset(payloads):
            raise ClientPackageAssemblyError("Client Files 缺少 MOD gameplay DLL 或校验文件")

        component_versions = {
            component: entries[component]["version"] for component in COMPONENTS
        }
        component_receipts = {
            component: {
                "version": entries[component]["version"],
                "sha256": entries[component]["sha256"],
                "files": dict(sorted(receipt_files[component].items())),
            }
            for component in COMPONENTS
        }
        package_manifest = {
            "schemaVersion": 1,
            "clientVersion": version,
            "generatedAt": generated_at,
            "bootstrapHooksSha256": hooks_digest,
            "componentVersions": component_versions,
            "componentReceipts": component_receipts,
            "files": [
                {
                    "path": path,
                    "size": len(payload),
                    "sha256": _sha256_bytes(payload),
                }
                for path, payload in sorted(payloads.items())
            ],
        }

        output_dir.mkdir(parents=True, exist_ok=True)
        output = output_dir / f"SCBL-Client-v{version}-win-x86.zip"
        if output.exists():
            raise ClientPackageAssemblyError(f"输出已存在，拒绝覆盖：{output}")
        try:
            with zipfile.ZipFile(
                output,
                "x",
                compression=zipfile.ZIP_DEFLATED,
                compresslevel=6,
            ) as bundle:
                for path, payload in sorted(payloads.items()):
                    _write_deterministic(bundle, path, payload)
                _write_deterministic(
                    bundle,
                    "client_package_manifest.json",
                    (json.dumps(package_manifest, ensure_ascii=False, indent=2) + "\n").encode(
                        "utf-8"
                    ),
                )
            return ClientPublisher(self.root, refresh_descriptor=False).verify(output)
        except (OSError, ClientPublishError, zipfile.BadZipFile) as exc:
            output.unlink(missing_ok=True)
            raise ClientPackageAssemblyError(f"完整包封装失败：{exc}") from exc

    def _artifact(self, component: str, entry: dict[str, Any]) -> Path:
        spec = COMPONENTS[component]
        return self.components.artifacts_root / component / entry["version"] / spec.filename

    @staticmethod
    def _extract_exact_zip(archive: Path, mapping: dict[str, str]) -> dict[str, bytes]:
        try:
            with zipfile.ZipFile(archive, "r") as bundle:
                names = [
                    item.filename.replace("\\", "/")
                    for item in bundle.infolist()
                    if not item.is_dir()
                ]
                if set(names) != set(mapping) or len(names) != len(set(names)):
                    raise ClientPackageAssemblyError(
                        f"组件 ZIP 内容不符合固定布局：{archive.name}"
                    )
                return {mapping[name]: bundle.read(name) for name in names}
        except zipfile.BadZipFile as exc:
            raise ClientPackageAssemblyError(f"组件 ZIP 已损坏：{archive.name}") from exc

    @staticmethod
    def _extract_client_files(archive: Path) -> dict[str, bytes]:
        try:
            with zipfile.ZipFile(archive, "r") as bundle:
                infos = [item for item in bundle.infolist() if not item.is_dir()]
                names = [item.filename.replace("\\", "/") for item in infos]
                if len(names) != len(set(names)) or "client-files-manifest.json" not in names:
                    raise ClientPackageAssemblyError("Client Files ZIP 文件重复或缺少清单")
                try:
                    manifest: Any = json.loads(bundle.read("client-files-manifest.json"))
                except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                    raise ClientPackageAssemblyError("Client Files 清单不是有效 UTF-8 JSON") from exc
                if (
                    not isinstance(manifest, dict)
                    or manifest.get("schemaVersion") != 1
                    or not isinstance(manifest.get("files"), list)
                    or not isinstance(manifest.get("delete"), list)
                ):
                    raise ClientPackageAssemblyError("Client Files 清单无效")
                result: dict[str, bytes] = {}
                declared = {"client-files-manifest.json"}
                for item in manifest["files"]:
                    if not isinstance(item, dict) or set(item) != {"path", "size", "sha256"}:
                        raise ClientPackageAssemblyError("Client Files 文件记录无效")
                    path = item["path"]
                    archive_path = f"files/{path}" if isinstance(path, str) else ""
                    if not _safe_relative_path(path) or archive_path not in names or path in result:
                        raise ClientPackageAssemblyError(f"Client Files 路径无效：{path!r}")
                    payload = bundle.read(archive_path)
                    if (
                        type(item["size"]) is not int
                        or len(payload) != item["size"]
                        or not isinstance(item["sha256"], str)
                        or _sha256_bytes(payload) != item["sha256"].lower()
                    ):
                        raise ClientPackageAssemblyError(f"Client Files 校验失败：{path}")
                    result[path] = payload
                    declared.add(archive_path)
                for path in manifest["delete"]:
                    if not _safe_relative_path(path) or path in result:
                        raise ClientPackageAssemblyError(f"Client Files 删除路径无效：{path!r}")
                if set(names) != declared:
                    raise ClientPackageAssemblyError("Client Files ZIP 包含清单之外的文件")
                return result
        except zipfile.BadZipFile as exc:
            raise ClientPackageAssemblyError("Client Files ZIP 已损坏") from exc

    @staticmethod
    def _add_payload(target: dict[str, bytes], path: str, payload: bytes) -> None:
        if not _safe_relative_path(path) or path in target:
            raise ClientPackageAssemblyError(f"完整包路径冲突或不安全：{path}")
        target[path] = payload


def _safe_relative_path(value: Any) -> bool:
    if not isinstance(value, str) or not value or "\\" in value or "\x00" in value:
        return False
    path = PurePosixPath(value)
    return not path.is_absolute() and all(part not in {"", ".", ".."} for part in path.parts)


def _sha256_bytes(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def _write_deterministic(bundle: zipfile.ZipFile, path: str, payload: bytes) -> None:
    info = zipfile.ZipInfo(path, date_time=(1980, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = 0o100644 << 16
    bundle.writestr(info, payload, compress_type=zipfile.ZIP_DEFLATED, compresslevel=6)
