#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
  echo "请使用 root 运行：sudo bash setup.sh" >&2
  exit 1
fi

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
INSTALLER="$SCRIPT_DIR/install.sh"
[[ -f "$INSTALLER" ]] || {
  echo "同目录缺少底层安装器：$INSTALLER" >&2
  exit 1
}

mapfile -t PACKAGES < <(find "$SCRIPT_DIR" -maxdepth 1 -type f \
  -name 'SCBL-Server-v*-full.zip' -print | sort)
if [[ ${#PACKAGES[@]} -ne 1 ]]; then
  echo "安装目录必须且只能有一个 SCBL-Server-v*-full.zip；当前找到 ${#PACKAGES[@]} 个。" >&2
  printf '  %s\n' "${PACKAGES[@]:-}" >&2
  exit 1
fi

PACKAGE="${PACKAGES[0]}"
CHECKSUM_FILE="$PACKAGE.sha256"
[[ -f "$CHECKSUM_FILE" ]] || {
  echo "缺少校验文件：$CHECKSUM_FILE" >&2
  exit 1
}

echo "正在校验安装包……"
(cd "$SCRIPT_DIR" && sha256sum -c "$(basename -- "$CHECKSUM_FILE")")

mapfile -t CLIENT_PACKAGES < <(find "$SCRIPT_DIR" -maxdepth 1 -type f \
  -name 'SCBL-Client-v*-win-x86.zip' -print | sort)
mapfile -t CLIENT_PATCHES < <(find "$SCRIPT_DIR" -maxdepth 1 -type f \
  -name 'SCBL-Client-*.scblpatch' -print | sort)
if [[ ${#CLIENT_PACKAGES[@]} -ne ${#CLIENT_PATCHES[@]} ]]; then
  echo "客户端完整包与组件补丁必须成对提供。" >&2
  exit 1
fi

publish_bundled_client_if_empty() {
  [[ ${#CLIENT_PACKAGES[@]} -eq 1 ]] || {
    echo "未随安装器提供客户端发布输入；现有客户端发布状态保持不变。"
    return 0
  }
  local channel manifest state
  channel="$(python3 - <<'PY'
import pathlib
import tomllib

with pathlib.Path('/etc/scbl/server.toml').open('rb') as source:
    print(tomllib.load(source)['server']['mode'])
PY
)"
  manifest="/var/lib/scbl/client-updates/components/channels/$channel/client_components_v2.json"
  state="$(python3 - "$manifest" <<'PY'
import json
import pathlib
import sys

path = pathlib.Path(sys.argv[1])
if not path.is_file():
    print('empty')
else:
    payload = json.loads(path.read_text(encoding='utf-8'))
    count = len(payload.get('components', {})) + len(payload.get('files', {}))
    print('empty' if count == 0 else 'present')
PY
)"
  if [[ "$state" == "present" ]]; then
    echo "检测到现有客户端组件基线；修复安装保持原发布内容，不重复覆盖。"
    return 0
  fi
  echo "正在建立首次客户端发布基线……"
  SCBL publish-components "${CLIENT_PATCHES[0]}"
  SCBL publish-client "${CLIENT_PACKAGES[0]}"
  echo "客户端完整包与 5 个程序组件、Client Files 已发布。"
}

if [[ -e /etc/scbl/server.toml || -L /opt/scbl/current ]]; then
  echo
  echo "检测到已经安装的 SCBL 服务器。"
  echo "可以使用当前完整包执行强制修复安装；现有身份、端口、网络密钥和数据库将保留。"
  read -r -p "确认执行强制修复安装 [y/N]：" CONFIRM
  [[ "$CONFIRM" =~ ^[Yy]$ ]] || {
    echo "已取消，未修改服务器。"
    exit 0
  }
  bash "$INSTALLER" --package "$PACKAGE" --force-reinstall
  publish_bundled_client_if_empty
  echo "SCBL 3.0 修复安装流程完成。"
  exit 0
fi
if [[ ${#CLIENT_PACKAGES[@]} -gt 1 ]]; then
  echo "安装目录最多只能包含一套客户端发布输入。" >&2
  exit 1
fi

echo
echo "SCBL 3.0 首次安装"
echo "  1. 正式服务器（stable，11010 / 18080 / 19080）"
echo "  2. 测试服务器（test，11011 / 18081 / 19081）"
read -r -p "请选择 [1]：" MODE_CHOICE
MODE_CHOICE="${MODE_CHOICE:-1}"

case "$MODE_CHOICE" in
  1)
    MODE="stable"
    SERVER_ID="scbl-prod-01"
    GAME_PORT="11010"
    UPDATE_PORT="18080"
    CONTROL_PORT="19080"
    ;;
  2)
    MODE="test"
    SERVER_ID="scbl-test-01"
    GAME_PORT="11011"
    UPDATE_PORT="18081"
    CONTROL_PORT="19081"
    ;;
  *)
    echo "无效选择：$MODE_CHOICE" >&2
    exit 2
    ;;
esac

read -r -p "服务器 ID [$SERVER_ID]：" INPUT
SERVER_ID="${INPUT:-$SERVER_ID}"

read -r -p "客户端使用的公网域名/IP（没有或由客户端保存则留空）：" PUBLIC_HOST
DEFAULT_UPDATE_URL=""
if [[ -n "$PUBLIC_HOST" ]]; then
  DEFAULT_UPDATE_URL="http://${PUBLIC_HOST}:${UPDATE_PORT}"
fi
read -r -p "公网更新/下载地址 [$DEFAULT_UPDATE_URL]：" INPUT
PUBLIC_UPDATE_BASE_URL="${INPUT:-$DEFAULT_UPDATE_URL}"

echo
echo "即将首次安装："
echo "  安装包：$(basename -- "$PACKAGE")"
echo "  身份：$MODE"
echo "  服务器 ID：$SERVER_ID"
echo "  游戏端口：$GAME_PORT（TCP/WSS + UDP/QUIC）"
echo "  更新端口：$UPDATE_PORT"
echo "  控制端口：$CONTROL_PORT"
echo "  客户端公网地址：${PUBLIC_HOST:-由客户端现有设置提供}"
echo "  公网下载地址：${PUBLIC_UPDATE_BASE_URL:-未设置}"
echo
read -r -p "确认这是全新服务器并开始安装 [Y/n]：" CONFIRM
CONFIRM="${CONFIRM:-Y}"
[[ "$CONFIRM" =~ ^[Yy]$ ]] || {
  echo "已取消，未修改服务器。"
  exit 0
}

ARGS=(
  --package "$PACKAGE"
  --mode "$MODE"
  --server-id "$SERVER_ID"
  --game-port "$GAME_PORT"
  --update-port "$UPDATE_PORT"
  --control-port "$CONTROL_PORT"
)
[[ -n "$PUBLIC_HOST" ]] && ARGS+=(--public-host "$PUBLIC_HOST")
[[ -n "$PUBLIC_UPDATE_BASE_URL" ]] && ARGS+=(--public-update-base-url "$PUBLIC_UPDATE_BASE_URL")

bash "$INSTALLER" "${ARGS[@]}"

echo
publish_bundled_client_if_empty

echo
read -r -p "是否现在安装并配置 IPv6 DDNS-Go [y/N]：" INSTALL_DDNS
if [[ "$INSTALL_DDNS" =~ ^[Yy]$ ]]; then
  SCBL ddns install
  SCBL ddns configure
  SCBL ddns start
fi

echo
echo "SCBL 3.0 安装流程完成。"
