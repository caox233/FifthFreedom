from __future__ import annotations

import threading
import time
from collections.abc import Callable


class GameplaySnapshotPublisher:
    """Coalesce gameplay-state writes onto one background thread."""

    def __init__(self, publish_snapshot: Callable[[], None], *, refresh_seconds: float) -> None:
        self._publish_snapshot = publish_snapshot
        self._refresh_seconds = refresh_seconds
        self._condition = threading.Condition()
        self._generation = 0
        self._published_generation = -1
        self._stopping = False
        self._writer: threading.Thread | None = None

    def mark_dirty(self) -> int:
        with self._condition:
            self._generation += 1
            generation = self._generation
            self._condition.notify_all()
            return generation

    def _loop(self) -> None:
        next_periodic = time.monotonic()
        while True:
            with self._condition:
                while True:
                    now = time.monotonic()
                    dirty = self._generation > self._published_generation
                    if self._stopping or dirty or now >= next_periodic:
                        target_generation = self._generation
                        stopping = self._stopping
                        break
                    self._condition.wait(timeout=max(0.0, next_periodic - now))
            try:
                self._publish_snapshot()
            except Exception as error:
                print(f"control-plane gameplay snapshot publish failed: {error}", flush=True)
            else:
                with self._condition:
                    self._published_generation = max(
                        self._published_generation,
                        target_generation,
                    )
                    self._condition.notify_all()
            next_periodic = time.monotonic() + self._refresh_seconds
            if stopping:
                with self._condition:
                    if self._published_generation >= target_generation:
                        return

    def start(self) -> None:
        with self._condition:
            if self._writer is not None and self._writer.is_alive():
                return
            self._stopping = False
            self._writer = threading.Thread(
                target=self._loop,
                name="scbl-gameplay-mode-snapshot",
                daemon=True,
            )
            self._writer.start()
            self._generation += 1
            self._condition.notify_all()

    def publish(self, timeout: float) -> bool:
        with self._condition:
            self._generation += 1
            target_generation = self._generation
            self._condition.notify_all()
            deadline = time.monotonic() + max(0.0, timeout)
            while self._published_generation < target_generation:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return False
                self._condition.wait(timeout=remaining)
            return True

    def stop(self, timeout: float) -> bool:
        with self._condition:
            writer = self._writer
            if writer is None:
                return True
            self._generation += 1
            final_generation = self._generation
            self._stopping = True
            self._condition.notify_all()

        writer.join(timeout=max(0.0, timeout))
        with self._condition:
            flushed = self._published_generation >= final_generation
            stopped = not writer.is_alive()
            if stopped and self._writer is writer:
                self._writer = None
            return flushed and stopped
