"""Explicit Linux HTTPS control-plane entry point; no implicit config or key."""
import argparse
import asyncio
import signal
import sys

from .authenticated_bridge import AuthenticatedSessionBridge
from .protected_transport import ProtectedSessionServer
from .secure_config import build_tls_context, load_config, load_issuer_key, validate_database
from .session_reader import AdmissionError


async def _run(config, bridge, context):
    server = ProtectedSessionServer(bridge, context, config.listen_ip, config.listen_port)
    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    for value in (signal.SIGTERM, signal.SIGINT):
        loop.add_signal_handler(value, stop.set)
    stopping = asyncio.create_task(stop.wait())
    serving = None
    try:
        await server.start()
        print("CONTROL_READY", flush=True)
        serving = asyncio.create_task(server.wait())
        done, _ = await asyncio.wait({stopping, serving}, return_when=asyncio.FIRST_COMPLETED)
        if serving in done:
            await serving
            raise AdmissionError("LISTENER_STOPPED")
    finally:
        stopping.cancel()
        await server.close()
        await asyncio.gather(stopping, *( [serving] if serving is not None else []), return_exceptions=True)
        for value in (signal.SIGTERM, signal.SIGINT):
            loop.remove_signal_handler(value)


def main(argv=None):
    parser = argparse.ArgumentParser(description="Fifth Freedom read-only HTTPS control plane")
    parser.add_argument("--config", required=True, help="absolute path to owner-only Linux configuration")
    parser.add_argument("--check-config", action="store_true", help="validate local inputs without opening a listener")
    args = parser.parse_args(argv)
    try:
        config = load_config(args.config)
        key = load_issuer_key(config.issuer_key_file)
        context = build_tls_context(config)
        reader = validate_database(config)
        bridge = AuthenticatedSessionBridge(reader, key)
        if args.check_config:
            print("CONTROL_CONFIG_VALID")
        else:
            asyncio.run(_run(config, bridge, context))
        return 0
    except AdmissionError as error:
        # These are only our static bounded codes; no traceback/path/key output.
        print(f"CONTROL_START_FAILED {error}", file=sys.stderr)
    except Exception:
        print("CONTROL_START_FAILED INTERNAL_ERROR", file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
