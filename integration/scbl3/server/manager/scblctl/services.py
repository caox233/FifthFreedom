from __future__ import annotations

from dataclasses import asdict, dataclass

from .system import CommandRunner


@dataclass(frozen=True, slots=True)
class ServiceDefinition:
    component: str
    unit: str
    required: bool = True


SERVICES = (
    ServiceDefinition("tunnel", "scbl-tunnel.service"),
    ServiceDefinition("dedicated", "scbl-dedicated.service"),
    ServiceDefinition("control", "scbl-control-plane.service"),
    ServiceDefinition("update", "scbl-update.service"),
    ServiceDefinition("ddns", "ddns-go.service", required=False),
)

SERVICE_BY_COMPONENT = {service.component: service for service in SERVICES}


@dataclass(frozen=True, slots=True)
class ServiceStatus:
    component: str
    unit: str
    active: str
    sub: str
    enabled: str
    exit_status: str
    result: str
    restarts: int | None
    available: bool
    required: bool

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


class SystemdManager:
    def __init__(self, runner: CommandRunner | None = None) -> None:
        self.runner = runner or CommandRunner()

    @property
    def available(self) -> bool:
        return self.runner.available("systemctl")

    def status(self, service: ServiceDefinition) -> ServiceStatus:
        if not self.available:
            return ServiceStatus(
                service.component,
                service.unit,
                "unknown",
                "unknown",
                "unknown",
                "unknown",
                "unknown",
                None,
                False,
                service.required,
            )
        result = self.runner.run(
            (
                "systemctl",
                "show",
                service.unit,
                "--property=LoadState,ActiveState,SubState,UnitFileState,"
                "ExecMainStatus,Result,NRestarts",
                "--no-pager",
            )
        )
        values: dict[str, str] = {}
        for line in result.stdout.splitlines():
            key, separator, value = line.partition("=")
            if separator:
                values[key] = value
        loaded = values.get("LoadState", "not-found") != "not-found"
        return ServiceStatus(
            service.component,
            service.unit,
            values.get("ActiveState", "unknown"),
            values.get("SubState", "unknown"),
            values.get("UnitFileState", "unknown"),
            values.get("ExecMainStatus", "unknown"),
            values.get("Result", "unknown"),
            _parse_nonnegative_int(values.get("NRestarts")),
            loaded,
            service.required,
        )

    def all_statuses(self) -> list[ServiceStatus]:
        return [self.status(service) for service in SERVICES]

    def restart(self, component: str) -> None:
        service = SERVICE_BY_COMPONENT.get(component)
        if service is None:
            choices = ", ".join(sorted(SERVICE_BY_COMPONENT))
            raise ValueError(f"未知组件 {component}；可选：{choices}")
        if not self.available:
            raise RuntimeError("当前系统没有 systemctl")
        result = self.runner.run(("systemctl", "restart", service.unit), timeout=45)
        if not result.ok:
            detail = result.stderr or result.stdout or f"退出码 {result.returncode}"
            raise RuntimeError(f"重启 {service.unit} 失败：{detail}")


def _parse_nonnegative_int(value: str | None) -> int | None:
    if value is None or not value.isdigit():
        return None
    parsed = int(value)
    return parsed if parsed >= 0 else None
