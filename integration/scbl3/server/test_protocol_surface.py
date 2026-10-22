#!/usr/bin/env python3
from pathlib import Path


quazal_rmc = Path("server/quazal/src/rmc.rs").read_text(encoding="utf-8")
protocol_root = Path("server/dedicated-server/sc_bl_protocols/src/protocols")
generated_protocols = "\n".join(
    path.read_text(encoding="utf-8")
    for path in sorted(protocol_root.rglob("*_protocol*.rs"))
)

# SCBL is a Quazal server. The old generated client half had no request
# transport, ended in unimplemented!(), and was never instantiated. Keep the
# generated server handlers while preventing that dead public surface from
# silently returning during a later source import.
assert "trait ClientProtocol" not in quazal_rmc
assert "ClientProtocol" not in generated_protocols
assert "ProtocolClient<" not in generated_protocols
assert "impl<T:" in generated_protocols
assert " Protocol<CI> for " in generated_protocols
assert not Path("server/quazal/src/errors.rs").exists()
assert not Path("server/quazal/src/rmc/protocols.rs").exists()

print("Quazal generated server-only protocol surface checks passed.")
