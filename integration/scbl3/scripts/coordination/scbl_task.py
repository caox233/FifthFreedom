#!/usr/bin/env python3
"""Trusted entry point for SCBL coordination and Rust task commands."""

from __future__ import annotations

import os
import hashlib
import json
import shutil
import stat
import subprocess
import sys
import tomllib
from dataclasses import dataclass
from pathlib import Path
from typing import Mapping, Sequence


HOOKS_CONFIG_MARKER = b"SCBL_HOOKS_CONFIG=scbl.toml.v1"
PLUGIN_INITIALIZE_MARKER = b"scbl_plugin_initialize"
PLUGIN_SHUTDOWN_MARKER = b"scbl_plugin_shutdown"
HOOKS_TARGET = "i686-pc-windows-msvc"
LOCAL_HOOKS_RELATIVE_PATH = Path("local-components/hooks/uplay_r1_loader.dll")
LOCAL_GAMEPLAY_RELATIVE_PATH = Path("local-components/hooks/scbl_gameplay.dll")


class ToolchainError(RuntimeError):
    """Raised when the pinned Rust toolchain cannot be proven consistent."""


@dataclass(frozen=True)
class RustTools:
    channel: str
    root: Path
    cargo: Path
    rustc: Path
    rustfmt: Path
    versions: tuple[str, str, str]


def repository_root() -> Path:
    return Path(__file__).resolve().parents[2]


def parse_toolchain_channel(root: Path) -> str:
    config_path = root / "rust-toolchain.toml"
    if not config_path.is_file():
        raise ToolchainError(f"Rust toolchain configuration is missing: {config_path}")
    try:
        config = tomllib.loads(config_path.read_text(encoding="utf-8"))
    except (OSError, tomllib.TOMLDecodeError) as exc:
        raise ToolchainError(f"Rust toolchain configuration is invalid: {config_path}") from exc
    channel = config.get("toolchain", {}).get("channel")
    if not isinstance(channel, str) or not channel.strip():
        raise ToolchainError("rust-toolchain.toml must contain a non-empty toolchain.channel")
    channel = channel.strip()
    if any(character in channel for character in ("/", "\\")) or channel in {".", ".."}:
        raise ToolchainError(f"Refusing an invalid Rust toolchain channel: {channel!r}")
    return channel


def rustup_home(environment: Mapping[str, str]) -> Path:
    configured = environment.get("RUSTUP_HOME", "").strip()
    if configured:
        candidate = Path(configured)
    else:
        profile = environment.get("USERPROFILE", "").strip()
        if not profile:
            profile = environment.get("HOME", "").strip()
        if not profile:
            raise ToolchainError("Neither RUSTUP_HOME nor a user profile is available")
        candidate = Path(profile) / ".rustup"
    if not candidate.is_absolute():
        raise ToolchainError(f"Rustup home must be an absolute path: {candidate}")
    return candidate


def executable_name(name: str) -> str:
    return f"{name}.exe" if os.name == "nt" else name


def expected_channel_marker(channel: str) -> str | None:
    if channel.startswith("nightly"):
        return "nightly"
    if channel.startswith("beta"):
        return "beta"
    return None


def validate_tool_paths(channel: str, toolchain_root: Path, paths: Sequence[Path]) -> None:
    if toolchain_root.name != channel and not toolchain_root.name.startswith(f"{channel}-"):
        raise ToolchainError(
            f"Resolved Rust directory does not match pinned channel {channel}: {toolchain_root}"
        )
    missing = [path for path in paths if not path.is_file()]
    if missing:
        raise ToolchainError(f"Pinned Rust tool is missing: {missing[0]}")
    resolved_parents = {path.resolve().parent for path in paths}
    expected_bin = (toolchain_root / "bin").resolve()
    if resolved_parents != {expected_bin}:
        raise ToolchainError("cargo, rustc, and rustfmt must resolve to the same pinned bin directory")


def probe_tool_versions(
    channel: str,
    paths: Sequence[Path],
    *,
    runner=subprocess.run,
) -> tuple[str, str, str]:
    outputs: list[str] = []
    marker = expected_channel_marker(channel)
    for path in paths:
        try:
            completed = runner(
                [str(path), "-V"],
                check=False,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
            )
        except OSError as exc:
            raise ToolchainError(f"Unable to run pinned Rust tool: {path}") from exc
        output = (completed.stdout or completed.stderr or "").strip()
        if completed.returncode != 0 or not output:
            raise ToolchainError(f"Pinned Rust tool version probe failed: {path}")
        if marker and marker not in output.lower():
            raise ToolchainError(
                f"Pinned {channel} tool reported an incompatible version: {output}"
            )
        outputs.append(output)
    return outputs[0], outputs[1], outputs[2]


def resolve_rust_tools(
    root: Path,
    environment: Mapping[str, str],
    *,
    runner=subprocess.run,
) -> RustTools:
    channel = parse_toolchain_channel(root)
    toolchains = rustup_home(environment) / "toolchains"
    if not toolchains.is_dir():
        raise ToolchainError(f"Rust toolchain directory is missing: {toolchains}")
    matches = sorted(
        path
        for path in toolchains.iterdir()
        if path.is_dir() and (path.name == channel or path.name.startswith(f"{channel}-"))
    )
    if len(matches) != 1:
        raise ToolchainError(
            f"Expected exactly one installed directory for {channel}, found {len(matches)}"
        )
    toolchain_root = matches[0]
    cargo = toolchain_root / "bin" / executable_name("cargo")
    rustc = toolchain_root / "bin" / executable_name("rustc")
    rustfmt = toolchain_root / "bin" / executable_name("rustfmt")
    paths = (cargo, rustc, rustfmt)
    validate_tool_paths(channel, toolchain_root, paths)
    versions = probe_tool_versions(channel, paths, runner=runner)
    return RustTools(channel, toolchain_root, cargo, rustc, rustfmt, versions)


def child_environment(tools: RustTools, base: Mapping[str, str]) -> dict[str, str]:
    environment = dict(base)
    original_path = environment.get("PATH", "")
    tool_bin = str((tools.root / "bin").resolve())
    environment["PATH"] = tool_bin if not original_path else f"{tool_bin}{os.pathsep}{original_path}"
    environment["RUSTC"] = str(tools.rustc.resolve())
    environment["RUSTUP_TOOLCHAIN"] = tools.channel
    return environment


def run_passthrough(command: Sequence[str], *, environment: Mapping[str, str]) -> int:
    try:
        completed = subprocess.run(list(command), check=False, env=dict(environment))
    except OSError as exc:
        print(f"SCBL task entry could not start {command[0]}: {exc}", file=sys.stderr)
        return 127
    return completed.returncode


@dataclass(frozen=True)
class HooksDevOptions:
    launcher_dir: Path | None
    release: bool
    launch: bool
    operation: str


def parse_hooks_dev_options(arguments: Sequence[str]) -> HooksDevOptions:
    launcher_dir: Path | None = None
    release = False
    launch = True
    operation = "install"
    index = 0
    while index < len(arguments):
        argument = arguments[index]
        if argument == "--release":
            release = True
        elif argument == "--no-launch":
            launch = False
        elif argument in {"--disable", "--status"}:
            requested = argument[2:]
            if operation != "install":
                raise ValueError("hooks-dev accepts only one of --disable or --status")
            operation = requested
            launch = False
        elif argument == "--launcher-dir":
            index += 1
            if index >= len(arguments) or not arguments[index].strip():
                raise ValueError("--launcher-dir requires a directory")
            launcher_dir = Path(arguments[index].strip())
        elif argument.startswith("--launcher-dir="):
            value = argument.split("=", 1)[1].strip()
            if not value:
                raise ValueError("--launcher-dir requires a directory")
            launcher_dir = Path(value)
        else:
            raise ValueError(f"unknown hooks-dev argument: {argument}")
        index += 1
    return HooksDevOptions(launcher_dir, release, launch, operation)


def resolve_test_launcher_directory(
    configured: Path | None,
    environment: Mapping[str, str],
) -> Path:
    candidate = configured
    if candidate is None:
        value = environment.get("SCBL_TEST_LAUNCHER_DIR", "").strip()
        if value:
            candidate = Path(value)
    if candidate is None:
        profile = environment.get("USERPROFILE", "").strip()
        if profile:
            desktop_candidate = Path(profile) / "Desktop" / "SCBL"
            if (desktop_candidate / "SCBL.exe").is_file():
                candidate = desktop_candidate
    if candidate is None:
        raise ValueError(
            "test Launcher directory is unknown; pass --launcher-dir or set SCBL_TEST_LAUNCHER_DIR"
        )
    if not candidate.is_absolute():
        raise ValueError(f"test Launcher directory must be absolute: {candidate}")
    resolved = candidate.resolve()
    launcher = resolved / "SCBL.exe"
    if not launcher.is_file():
        raise ValueError(f"test Launcher is missing: {launcher}")
    if launcher.stat().st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
        raise ValueError(f"test Launcher cannot be a reparse point: {launcher}")
    return resolved


def resolve_configured_game_directory(
    launcher_dir: Path,
    environment: Mapping[str, str],
) -> Path | None:
    machine = environment.get("COMPUTERNAME", "").strip()
    user = environment.get("USERNAME", "").strip()
    if not machine or not user:
        return None
    settings_path = (
        launcher_dir
        / "data"
        / machine
        / user
        / "config"
        / "launcher_settings.json"
    )
    if not settings_path.is_file():
        return None
    try:
        settings = json.loads(settings_path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError):
        return None
    value = settings.get("GameDirectory", "")
    if not isinstance(value, str) or not value.strip():
        return None
    candidate = Path(value.strip())
    if not candidate.is_absolute() or not candidate.is_dir():
        return None
    resolved = candidate.resolve()
    if not any((resolved / name).is_file() for name in ("Blacklist_DX11_game.exe", "Blacklist_game.exe")):
        return None
    return resolved


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def validate_x86_dll(path: Path, required_markers: Sequence[bytes]) -> None:
    if not path.is_file():
        raise ValueError(f"Hooks build output is missing: {path}")
    size = path.stat().st_size
    if size < 512:
        raise ValueError(f"Hooks build output is too small: {path}")
    with path.open("rb") as stream:
        header = stream.read(4096)
        if len(header) < 64 or header[:2] != b"MZ":
            raise ValueError("Hooks build output is not a PE DLL")
        pe_offset = int.from_bytes(header[0x3C:0x40], "little")
        if pe_offset + 26 > len(header):
            stream.seek(0)
            header = stream.read(pe_offset + 26)
        if header[pe_offset:pe_offset + 4] != b"PE\0\0":
            raise ValueError("Hooks build output has an invalid PE signature")
        machine = int.from_bytes(header[pe_offset + 4:pe_offset + 6], "little")
        characteristics = int.from_bytes(header[pe_offset + 22:pe_offset + 24], "little")
        if machine != 0x014C or characteristics & 0x2000 == 0:
            raise ValueError("Hooks build output must be a 32-bit x86 DLL")
    content = path.read_bytes()
    for marker in required_markers:
        if marker not in content:
            raise ValueError(f"DLL build output does not expose required marker: {marker.decode('ascii')}")


def validate_local_hooks_dll(path: Path) -> None:
    validate_x86_dll(path, (HOOKS_CONFIG_MARKER,))


def validate_local_plugin_dll(path: Path) -> None:
    validate_x86_dll(path, (PLUGIN_INITIALIZE_MARKER, PLUGIN_SHUTDOWN_MARKER))


def install_local_dll(
    source: Path,
    target: Path,
    validator,
) -> tuple[Path, str]:
    validator(source)
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(target.name + ".scbl-new")
    rollback = target.with_name(target.name + ".scbl-rollback")
    source_hash = sha256_file(source)
    for stale in (temporary, rollback):
        if stale.exists():
            stale.unlink()
    try:
        with source.open("rb") as input_stream, temporary.open("xb") as output_stream:
            shutil.copyfileobj(input_stream, output_stream, length=1024 * 1024)
            output_stream.flush()
            os.fsync(output_stream.fileno())
        if sha256_file(temporary) != source_hash:
            raise ValueError("local Hooks staging hash does not match the build output")
        if target.exists():
            shutil.copy2(target, rollback)
        os.replace(temporary, target)
        if sha256_file(target) != source_hash:
            raise ValueError("installed local Hooks hash does not match the build output")
        if rollback.exists():
            rollback.unlink()
        return target, source_hash
    except Exception:
        if temporary.exists():
            temporary.unlink()
        if rollback.exists():
            os.replace(rollback, target)
        raise


def install_local_hooks_override(source: Path, launcher_dir: Path) -> tuple[Path, str]:
    target = launcher_dir / LOCAL_HOOKS_RELATIVE_PATH
    if target.parent.resolve() != (launcher_dir / LOCAL_HOOKS_RELATIVE_PATH.parent).resolve():
        raise ValueError("local Hooks target escaped the test Launcher directory")
    return install_local_dll(source, target, validate_local_hooks_dll)


def install_local_plugin(source: Path, launcher_dir: Path, relative_path: Path) -> tuple[Path, str]:
    target = launcher_dir / relative_path
    expected_parent = (launcher_dir / relative_path.parent).resolve()
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.parent.resolve() != expected_parent:
        raise ValueError("local plugin target escaped the test Launcher directory")
    return install_local_dll(source, target, validate_local_plugin_dll)


def disable_local_development_dlls(launcher_dir: Path) -> list[Path]:
    disabled = []
    for relative_path in (
        LOCAL_HOOKS_RELATIVE_PATH,
        LOCAL_GAMEPLAY_RELATIVE_PATH,
    ):
        target = launcher_dir / relative_path
        if target.exists():
            retained = target.with_name(target.name + ".disabled")
            os.replace(target, retained)
            disabled.append(retained)
    return disabled


def disable_runtime_plugins(game_directory: Path | None) -> list[Path]:
    if game_directory is None:
        return []
    disabled = []
    for file_name in (
        "scbl_audio.dll",
        "scbl_gameplay.dll",
        "scbl_diagnostics.dll",
        "scbl_mod_runtime.dll",
        "scbl_diagnostics_runtime.dll",
    ):
        target = game_directory / file_name
        if target.exists():
            retained = target.with_name(target.name + ".disabled")
            os.replace(target, retained)
            disabled.append(retained)
    return disabled


def is_launcher_running(*, runner=subprocess.run) -> bool:
    if os.name != "nt":
        return False
    completed = runner(
        ["tasklist.exe", "/FI", "IMAGENAME eq SCBL.exe", "/FO", "CSV", "/NH"],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    return completed.returncode == 0 and '"SCBL.exe"' in completed.stdout


def run_hooks_dev(
    root: Path,
    tools: RustTools,
    arguments: Sequence[str],
    environment: Mapping[str, str],
) -> int:
    try:
        options = parse_hooks_dev_options(arguments)
        launcher_dir = resolve_test_launcher_directory(options.launcher_dir, environment)
    except ValueError as exc:
        print(f"SCBL Hooks local test setup failed: {exc}", file=sys.stderr)
        return 2

    local_dlls = (
        ("core", launcher_dir / LOCAL_HOOKS_RELATIVE_PATH),
        ("gameplay", launcher_dir / LOCAL_GAMEPLAY_RELATIVE_PATH),
    )
    game_directory = resolve_configured_game_directory(launcher_dir, environment)
    if options.operation == "status":
        for kind, target in local_dlls:
            state = "enabled" if target.is_file() else "disabled"
            digest = f"\nSHA256: {sha256_file(target)}" if target.is_file() else ""
            print(f"Local {kind} test DLL: {state}\nPath: {target}{digest}")
        return 0
    if options.operation == "disable":
        disabled = disable_local_development_dlls(launcher_dir)
        disabled.extend(disable_runtime_plugins(game_directory))
        if not disabled:
            print("Local test DLLs were already disabled.")
        else:
            print("Local test DLLs disabled; previous files retained at:")
            for path in disabled:
                print(path)
        return 0

    profile = "release" if options.release else "debug"
    cargo_command = [str(tools.cargo), "build"]
    packages = ("hooks", "scbl-gameplay")
    for package in packages:
        cargo_command.extend(("-p", package))
    cargo_command.extend(("--target", HOOKS_TARGET))
    if options.release:
        cargo_command.append("--release")
    build_result = run_passthrough(
        cargo_command,
        environment=child_environment(tools, environment),
    )
    if build_result != 0:
        return build_result

    output_directory = root / "target" / HOOKS_TARGET / profile
    sources = (
        ("core", output_directory / "hooks.dll", LOCAL_HOOKS_RELATIVE_PATH),
        (
            "gameplay",
            output_directory / "scbl_gameplay.dll",
            LOCAL_GAMEPLAY_RELATIVE_PATH,
        ),
    )
    try:
        installed_dlls = []
        for kind, source, relative_path in sources:
            if kind == "core":
                installed, digest = install_local_hooks_override(source, launcher_dir)
            else:
                installed, digest = install_local_plugin(source, launcher_dir, relative_path)
            installed_dlls.append((kind, source, installed, digest))
        if game_directory is not None:
            for kind, source, _relative_path in sources[1:]:
                runtime_target = game_directory / source.name
                if runtime_target.parent.resolve() != game_directory.resolve():
                    raise ValueError("runtime plugin target escaped the configured game directory")
                installed, digest = install_local_dll(source, runtime_target, validate_local_plugin_dll)
                installed_dlls.append((f"{kind}-runtime", source, installed, digest))
    except (OSError, ValueError) as exc:
        print(f"SCBL local test DLL install failed: {exc}", file=sys.stderr)
        return 2

    print("Local test DLL set installed:")
    for kind, source, installed, digest in installed_dlls:
        print(f"{kind}: {source} -> {installed}\nSHA256: {digest}")
    print("Scope: test channel only; stable ignores the core override.")
    if game_directory is None:
        print("Warning: configured game directory was not found; start through this command so SCBL_PLUGIN_DIR is inherited.")
    if not options.launch:
        return 0
    if is_launcher_running():
        print(
            "SCBL.exe is already running. Close it, then run this command again so the new process starts explicitly on test.",
            file=sys.stderr,
        )
        return 3
    try:
        launch_environment = dict(environment)
        launch_environment["SCBL_PLUGIN_DIR"] = str((launcher_dir / LOCAL_HOOKS_RELATIVE_PATH.parent).resolve())
        subprocess.Popen(
            [str(launcher_dir / "SCBL.exe"), "--update-channel", "test"],
            cwd=str(launcher_dir),
            env=launch_environment,
        )
    except OSError as exc:
        print(f"SCBL Hooks local test could not start Launcher: {exc}", file=sys.stderr)
        return 127
    print("Test Launcher started with local audio, gameplay, and development-diagnostics plugin discovery; no DLL patch package was created or downloaded by this command.")
    return 0


def print_usage() -> None:
    print(
        "Usage: scbl-task.cmd <freeze|cleanup|build|versions|cargo|rustc|rustfmt|hooks-dev> [arguments...]\n"
        "  freeze     Run the component-aware immutable freeze entry.\n"
        "  cleanup    Dry-run/apply exact completed-task artifact pruning.\n"
        "  build      Build Launcher/Updater through the fixed cached .NET entry.\n"
        "  versions   Validate and print the pinned cargo/rustc/rustfmt versions.\n"
        "  cargo      Run pinned cargo with unchanged arguments and exit code.\n"
        "  rustc      Run pinned rustc with unchanged arguments and exit code.\n"
        "  rustfmt    Run pinned rustfmt with unchanged arguments and exit code.\n"
        "  hooks-dev  Incrementally build and atomically install a test-only local Hooks override.\n"
        "             Options: --launcher-dir DIR, --release, --no-launch, --status, --disable."
    )


def main(arguments: Sequence[str] | None = None) -> int:
    argv = list(sys.argv[1:] if arguments is None else arguments)
    if not argv or argv[0] in {"-h", "--help", "help"}:
        print_usage()
        return 0

    action, passthrough = argv[0], argv[1:]
    root = repository_root()
    if action in {"freeze", "cleanup", "build"}:
        script = {
            "freeze": root / "scripts" / "coordination" / "freeze_workspace.py",
            "cleanup": root / "scripts" / "maintenance" / "clean_workspace.py",
            "build": root / "scripts" / "build" / "build_component.py",
        }[action]
        if not script.is_file():
            print(f"SCBL {action} entry is missing: {script}", file=sys.stderr)
            return 2
        return run_passthrough(
            [sys.executable, str(script), *passthrough], environment=os.environ
        )

    if action not in {"versions", "cargo", "rustc", "rustfmt", "hooks-dev"}:
        print(f"Unknown SCBL task action: {action}", file=sys.stderr)
        print_usage()
        return 2

    try:
        tools = resolve_rust_tools(root, os.environ)
    except ToolchainError as exc:
        print(f"SCBL Rust toolchain validation failed: {exc}", file=sys.stderr)
        return 2

    if action == "versions":
        for output in tools.versions:
            print(output)
        return 0

    if action == "hooks-dev":
        return run_hooks_dev(root, tools, passthrough, os.environ)

    tool = getattr(tools, action)
    return run_passthrough(
        [str(tool), *passthrough], environment=child_environment(tools, os.environ)
    )


if __name__ == "__main__":
    raise SystemExit(main())
