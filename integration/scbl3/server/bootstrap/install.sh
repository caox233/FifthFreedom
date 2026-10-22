#!/usr/bin/env bash
set -euo pipefail

PACKAGE=""
MODE="stable"
SERVER_ID="scbl-server"
GAME_PORT="11010"
UPDATE_PORT="18080"
CONTROL_PORT="19080"
PUBLIC_HOST=""
PUBLIC_UPDATE_BASE_URL=""
CONFIG_TARGET="/etc/scbl/server.toml"
TEMP_DIR="$(mktemp -d -t scbl-bootstrap.XXXXXX)"
CONFIG_CREATED="n"
FORCE_REINSTALL="n"

cleanup() {
  status=$?
  if [[ $status -ne 0 && "$CONFIG_CREATED" == "y" && ! -L /opt/scbl/current ]]; then
    rm -f -- "$CONFIG_TARGET"
  fi
  rm -rf -- "$TEMP_DIR"
  return "$status"
}
trap cleanup EXIT

usage() {
  cat <<'EOF'
用法：sudo ./install.sh --package 完整包.zip [选项]

必填：
  --package FILE                  SCBL 3.0 服务端完整 ZIP

服务器身份：
  --mode stable|test              默认 stable
  --server-id ID                  默认 scbl-server

本地监听端口：
  --game-port PORT                默认 11010
  --update-port PORT              默认 18080
  --control-port PORT             默认 19080

可选对外信息（不参与本地监听）：
  --public-host HOST              路由器映射后的域名或公网地址
  --public-update-base-url URL    发布完成后用于生成下载链接
  --force-reinstall               使用现有配置和数据库强制修复安装

DDNS 与 EasyTier 由安装后的 SCBL 菜单独立管理。
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --package) PACKAGE="${2:?--package 缺少值}"; shift 2 ;;
    --mode) MODE="${2:?--mode 缺少值}"; shift 2 ;;
    --server-id) SERVER_ID="${2:?--server-id 缺少值}"; shift 2 ;;
    --game-port) GAME_PORT="${2:?--game-port 缺少值}"; shift 2 ;;
    --update-port) UPDATE_PORT="${2:?--update-port 缺少值}"; shift 2 ;;
    --control-port) CONTROL_PORT="${2:?--control-port 缺少值}"; shift 2 ;;
    --public-host) PUBLIC_HOST="${2:?--public-host 缺少值}"; shift 2 ;;
    --public-update-base-url) PUBLIC_UPDATE_BASE_URL="${2:?--public-update-base-url 缺少值}"; shift 2 ;;
    --force-reinstall) FORCE_REINSTALL="y"; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "未知参数：$1" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "请使用 root 运行安装器。" >&2
  exit 1
fi
[[ -n "$PACKAGE" && -f "$PACKAGE" ]] || { echo "必须提供有效的 --package。" >&2; exit 1; }
[[ "$MODE" == "stable" || "$MODE" == "test" ]] || { echo "--mode 只能是 stable 或 test。" >&2; exit 1; }
if [[ "$FORCE_REINSTALL" == "y" ]]; then
  [[ -f "$CONFIG_TARGET" && -L /opt/scbl/current ]] || {
    echo "强制修复安装要求现有配置和 current 运行版本都存在。" >&2
    exit 1
  }
else
  [[ ! -e "$CONFIG_TARGET" ]] || { echo "检测到现有配置；首次安装不会覆盖：$CONFIG_TARGET" >&2; exit 1; }
fi

for command in python3 install; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "缺少必要命令：$command" >&2
    exit 1
  }
done
python3 - <<'PY'
import sys
if sys.version_info < (3, 11):
    raise SystemExit("SCBL 3.0 需要 Python 3.11 或更高版本")
PY

PACKAGE="$(readlink -f -- "$PACKAGE")"
MANIFEST="$TEMP_DIR/scbl-package.json"
MANAGER="$TEMP_DIR/scblctl.pyz"

python3 - "$PACKAGE" "$MANIFEST" "$MANAGER" <<'PY'
import hashlib
import json
import pathlib
import re
import sys
import zipfile

package = pathlib.Path(sys.argv[1])
manifest_out = pathlib.Path(sys.argv[2])
manager_out = pathlib.Path(sys.argv[3])

with zipfile.ZipFile(package, "r") as bundle:
    infos = [item for item in bundle.infolist() if not item.is_dir()]
    names = [item.filename.replace("\\", "/") for item in infos]
    if len(names) != len(set(names)):
        raise SystemExit("完整包包含重复路径")
    for name in names:
        path = pathlib.PurePosixPath(name)
        if not name or path.is_absolute() or ".." in path.parts or "\\" in name or "%" in name:
            raise SystemExit("完整包包含不安全路径")
    if "scbl-package.json" not in names:
        raise SystemExit("完整包缺少组件清单")
    manifest = bundle.read("scbl-package.json")
    raw = json.loads(manifest)
    if raw.get("schemaVersion") != 3 or raw.get("packageType") != "full":
        raise SystemExit("首次安装必须使用 SCBL 3.0 full 包")
    if raw.get("integrityPolicy") != {"algorithm": "SHA256"}:
        raise SystemExit("完整包完整性策略无效")
    components = raw.get("components")
    if not isinstance(components, list):
        raise SystemExit("完整包组件清单无效")
    managers = [item for item in components if isinstance(item, dict) and item.get("id") == "server.manager"]
    if len(managers) != 1:
        raise SystemExit("完整包必须且只能包含一个 server.manager")
    manager = managers[0]
    manager_path = manager.get("path")
    if manager_path != "artifacts/server.manager/scblctl.pyz":
        raise SystemExit("server.manager 包内路径无效")
    payload = bundle.read(manager_path)
    if len(payload) != manager.get("size") or hashlib.sha256(payload).hexdigest() != manager.get("sha256"):
        raise SystemExit("server.manager 摘要不匹配")
    manifest_out.write_bytes(manifest)
    manager_out.write_bytes(payload)
PY

python3 "$MANAGER" --version >/dev/null

install -d -m 0711 /etc/scbl

init_args=(
  --mode "$MODE"
  --server-id "$SERVER_ID"
  --game-port "$GAME_PORT"
  --update-port "$UPDATE_PORT"
  --control-port "$CONTROL_PORT"
)
[[ -n "$PUBLIC_HOST" ]] && init_args+=(--public-host "$PUBLIC_HOST")
[[ -n "$PUBLIC_UPDATE_BASE_URL" ]] && init_args+=(--public-update-base-url "$PUBLIC_UPDATE_BASE_URL")
if [[ "$FORCE_REINSTALL" == "y" ]]; then
  echo "正在执行强制修复安装；保留现有配置、网络密钥和数据库。"
  python3 "$MANAGER" deploy repair --package "$PACKAGE"
else
  python3 "$MANAGER" init "${init_args[@]}"
  CONFIG_CREATED="y"
  python3 "$MANAGER" deploy install --package "$PACKAGE"
fi

[[ -x /usr/local/lib/scbl/scblctl.pyz ]] || {
  echo "Manager 安装结果不存在。" >&2
  exit 1
}
cat > /usr/local/bin/SCBL <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
exec /usr/bin/python3 /usr/local/lib/scbl/scblctl.pyz "$@"
EOF
chmod 0755 /usr/local/bin/SCBL
ln -sfn /usr/local/bin/SCBL /usr/local/bin/scbl

if [[ "$FORCE_REINSTALL" == "y" ]]; then
  echo "SCBL 3.0 服务器强制修复安装完成。"
else
  echo "SCBL 3.0 $MODE 服务器首次安装完成。"
fi
echo "服务器状态：SCBL status"
echo "完整诊断：SCBL doctor"
