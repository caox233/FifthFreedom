from __future__ import annotations

import base64
import hashlib
import secrets
import sqlite3
from contextlib import closing
from pathlib import Path

from .paths import DEPLOYMENT_PATHS


class AccountRecoveryError(RuntimeError):
    pass


def normalize_recovery_code(value: str) -> str:
    return "".join(ch for ch in value.upper() if ch.isalnum())


def recovery_code_hash(value: str) -> str:
    return hashlib.sha256(normalize_recovery_code(value).encode("ascii")).hexdigest()


def generate_recovery_code() -> str:
    compact = base64.b32encode(secrets.token_bytes(15)).decode("ascii").rstrip("=")
    return "-".join(compact[index : index + 4] for index in range(0, len(compact), 4))


class AccountRecoveryManager:
    def __init__(self, database: str | Path | None = None) -> None:
        self.database = Path(database or Path(DEPLOYMENT_PATHS.data) / "dedicated" / "5th-echelon.db")

    def issue(self, username: str) -> str:
        username = username.strip()
        if not username:
            raise AccountRecoveryError("账号不能为空。")
        if not self.database.is_file():
            raise AccountRecoveryError(f"账号数据库不存在：{self.database}")

        code = generate_recovery_code()
        try:
            with closing(sqlite3.connect(self.database, timeout=10)) as connection, connection:
                columns = {
                    row[1]
                    for row in connection.execute("PRAGMA table_info(users)").fetchall()
                }
                if "recovery_code_hash" not in columns:
                    raise AccountRecoveryError("服务端尚未升级到支持恢复码的版本。")
                result = connection.execute(
                    "UPDATE users SET recovery_code_hash = ?, updated_at = CURRENT_TIMESTAMP WHERE username = ?",
                    (recovery_code_hash(code), username),
                )
                if result.rowcount != 1:
                    raise AccountRecoveryError("账号不存在。")
        except sqlite3.Error as exc:
            raise AccountRecoveryError(f"账号数据库操作失败：{exc}") from exc
        return code
