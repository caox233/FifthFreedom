"""Synthetic public recipes/CLI shapes; never start an overlay or network."""
import base64
import copy
from dataclasses import replace
import importlib.metadata
import ipaddress
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
import tomllib
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/public"))
from fifth_freedom_control.easytier_profile import (
    ProfileError, PeerObservationError, core_arguments, emit_private_toml, parse_peer_observation,
    parse_profile, peer_query_arguments, write_private_toml,
)


# RFC 7748 section 6.1 published Alice/Bob vectors, never deployment credentials.
ALICE_PRIVATE = bytes.fromhex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a")
ALICE_PUBLIC = bytes.fromhex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a")
BOB_PUBLIC = bytes.fromhex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f")
PSK = bytes(range(32))


def recipe(transport="tcp", role="initiator"):
    return dict(schema="ff-easytier-owned-probe-v1", purpose="owned-isolated-development",
        transport=transport, role=role, instance_id="4de8dbaf-3ce0-4e5e-a358-5edb96577f12",
        network_name="ffprobe-" + "1" * 32, interface_name="fftest1", rpc_port=15888,
        local=dict(underlay_ipv4="10.77.0.1", overlay_cidr="10.78.0.1/30", listen_port=22001,
                   public_key=base64.b64encode(ALICE_PUBLIC).decode()),
        remote=dict(underlay_ipv4="10.77.0.2", overlay_cidr="10.78.0.2/30", listen_port=22002,
                    public_key=base64.b64encode(BOB_PUBLIC).decode()),
        candidate=dict(upstream_commit="8428a89d2dabc94c97d370ec607c6ca142473626",
            source_manifest_sha256="1" * 64, patch_manifest_sha256="2" * 64,
            cargo_lock_sha256="3" * 64, build_profile_sha256="4" * 64,
            executable_sha256="5" * 64, executable_size=10000,
            role="easytier-core", target="x86_64-unknown-linux-gnu"))


def parsed(value=None):
    return parse_profile(json.dumps(recipe() if value is None else value).encode())


def peer_json(profile):
    """Pinned generated serde shape; synthetic observation, not actual RPC."""
    ident = uuid.UUID("ba32ace5-d0ac-4b60-928e-31be9a34ba5e")
    parts = {f"part{i+1}": int.from_bytes(ident.bytes[i*4:i*4+4], "big") for i in range(4)}
    local_port = profile.local.listen_port if profile.role == "responder" else 35000
    remote_port = profile.remote.listen_port if profile.role == "initiator" else 35000
    suffix = "/" if profile.transport == "wss" else ""
    remote_url = dict(url=f"{profile.transport}://{profile.remote.underlay_ipv4}:{remote_port}{suffix}")
    conn = dict(conn_id=str(ident), my_peer_id=101, peer_id=102, features=[], stats=None, loss_rate=0.0,
        is_client=profile.role == "initiator", network_name=profile.network_name, is_closed=False,
        noise_local_static_pubkey=list(ALICE_PUBLIC), noise_remote_static_pubkey=list(BOB_PUBLIC),
        secure_auth_level=3, peer_identity_type=0,
        tunnel=dict(tunnel_type=profile.transport,
            local_addr=dict(url=f"{profile.transport}://{profile.local.underlay_ipv4}:{local_port}{suffix}"),
            remote_addr=remote_url, resolved_remote_addr=copy.deepcopy(remote_url)))
    return [dict(peer=dict(peer_id=102, conns=[conn], default_conn_id=parts, directly_connected_conns=[parts]),
        route=dict(peer_id=102, cost=1, next_hop_peer_id=102, proxy_cidrs=[],
            ipv4_addr=dict(address=dict(addr=int(profile.remote.overlay.ip)), network_length=30)))]


class ProfileTests(unittest.TestCase):
    def test_explicit_crypto_tool_pin(self):
        self.assertEqual(importlib.metadata.version("cryptography"), "50.0.0")

    def test_canonical_digest_binds_role_transport_candidate_and_key(self):
        value = recipe()
        one = parsed(value)
        self.assertEqual(one.sha256, parse_profile(json.dumps(value, sort_keys=True, indent=4).encode()).sha256)
        for name, replacement in (("role", "responder"), ("transport", "quic")):
            changed = copy.deepcopy(value)
            changed[name] = replacement
            self.assertNotEqual(one.sha256, parsed(changed).sha256)
        changed = copy.deepcopy(value)
        changed["candidate"]["executable_sha256"] = "6" * 64
        self.assertNotEqual(one.sha256, parsed(changed).sha256)
        self.assertNotIn(base64.b64encode(PSK), one.canonical_bytes)
        self.assertNotIn(base64.b64encode(ALICE_PRIVATE), one.canonical_bytes)

    def test_recipe_rejects_ambient_unsafe_or_ambiguous_settings(self):
        changes = [("transport", "udp"), ("rpc_port", True), ("rpc_port", 0),
            ("instance_id", "missing"), ("network_name", "default"), ("purpose", "production"),
            ("interface_name", "tun0;id"), ("schema", 1)]
        for name, value in changes:
            with self.subTest(name=name, value=value), self.assertRaises(ProfileError):
                data = recipe(); data[name] = value; parsed(data)
        for name, value in (("underlay_ipv4", "example.org"), ("underlay_ipv4", "0.0.0.0"),
                            ("underlay_ipv4", "8.8.8.8"), ("overlay_cidr", "10.78.0.1/8"),
                            ("overlay_cidr", "10.78.0.0/30"), ("listen_port", 0)):
            with self.subTest(name=name, value=value), self.assertRaises(ProfileError):
                data = recipe(); data["local"][name] = value; parsed(data)
        for field in ("network_secret", "flags", "private_key", "peer", "rpc_address"):
            with self.subTest(field=field), self.assertRaises(ProfileError):
                data = recipe(); data[field] = "do not retain"; parsed(data)
        for raw in (b'{"schema":1,"schema":2}', b"NaN", b"x" * 16385, b"[" * 2000):
            with self.assertRaises(ProfileError): parse_profile(raw)

    def test_all_transports_and_directions_emit_exact_private_config(self):
        for transport in ("tcp", "quic", "wss"):
            for role in ("initiator", "responder"):
                with self.subTest(transport=transport, role=role):
                    profile = parsed(recipe(transport, role))
                    doc = tomllib.loads(emit_private_toml(profile, network_secret=PSK,
                                                        local_private_key=ALICE_PRIVATE).decode())
                    self.assertEqual(doc["listeners"], [profile.uri(profile.local)])
                    self.assertEqual(doc["secure_mode"]["local_public_key"], profile.local.public_key)
                    self.assertTrue(doc["secure_mode"]["enabled"])
                    self.assertEqual(doc["network_identity"]["network_secret"], base64.b64encode(PSK).decode())
                    self.assertEqual(doc["flags"]["encryption_algorithm"], "aes-256-gcm")
                    self.assertTrue(doc["flags"]["disable_tcp_hole_punching"])
                    self.assertTrue(doc["flags"]["disable_relay_data"])
                    self.assertFalse(doc["flags"]["enable_exit_node"])
                    self.assertFalse(doc["flags"]["no_tun"])
                    for field in ("stun_servers", "stun_servers_v6", "mapped_listeners", "routes", "exit_nodes", "proxy_network"):
                        self.assertEqual(doc[field], [])
                    self.assertNotIn("tcp_stun_servers", doc)
                    self.assertEqual(len(doc["peer"]), 1 if role == "initiator" else 0)
                    args = core_arguments(profile, "/tmp/owned/config.toml")
                    self.assertIn("127.0.0.1:15888", args)
                    self.assertIn("--disable-env-parsing", args)
                    self.assertIn("--verbose", peer_query_arguments(profile))

    def test_key_pair_mismatch_low_order_and_missing_secrets_fail_without_echo(self):
        profile = parsed()
        for kwargs in (dict(network_secret=b"short", local_private_key=ALICE_PRIVATE),
                       dict(network_secret=PSK, local_private_key=b"q" * 32)):
            with self.assertRaises(ProfileError) as caught: emit_private_toml(profile, **kwargs)
            self.assertRegex(str(caught.exception), "^[A-Z_]+$")
        data = recipe(); data["remote"]["public_key"] = base64.b64encode(b"\x01" + b"\0" * 31).decode()
        with self.assertRaises(ProfileError): emit_private_toml(parsed(data), network_secret=PSK, local_private_key=ALICE_PRIVATE)
        with self.assertRaises(ProfileError): emit_private_toml(replace(profile, transport="udp"), network_secret=PSK, local_private_key=ALICE_PRIVATE)

    @unittest.skipUnless(sys.platform == "linux", "Linux owner/dirfd file policy")
    def test_private_output_is_new_owned_file_never_overwrites_or_follows_links(self):
        with tempfile.TemporaryDirectory() as name:
            path = Path(name) / "config.toml"
            write_private_toml(path, b"private synthetic config\n")
            self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)
            with self.assertRaises(ProfileError): write_private_toml(path, b"replace")
            self.assertEqual(path.read_bytes(), b"private synthetic config\n")
            link = Path(name) / "link"; link.symlink_to(path)
            with self.assertRaises(ProfileError): write_private_toml(link, b"replace")
            os.chmod(name, 0o755)
            with self.assertRaises(ProfileError): write_private_toml(Path(name) / "unsafe", b"private")
            os.chmod(name, 0o700)

    def test_synthetic_verbose_projection_accepts_six_directions(self):
        for transport in ("tcp", "quic", "wss"):
            for role in ("initiator", "responder"):
                with self.subTest(transport=transport, role=role):
                    profile = parsed(recipe(transport, role))
                    observed = parse_peer_observation(profile, json.dumps(peer_json(profile)).encode())
                    self.assertEqual(observed.profile_sha256, profile.sha256)
                    self.assertEqual(observed.remote_peer_id, 102)

    def test_quic_initiator_accepts_only_pinned_producer_wildcard_bind_forms(self):
        profile = parsed(recipe("quic", "initiator"))
        for host in ("0.0.0.0", "[::]"):
            data = peer_json(profile)
            data[0]["peer"]["conns"][0]["tunnel"]["local_addr"]["url"] = f"quic://{host}:35000"
            result = parse_peer_observation(profile, json.dumps(data).encode())
            self.assertEqual(result.remote_peer_id, 102)
            self.assertEqual(result.local_bind_kind, "wildcard")
        for transport, role in (("tcp", "initiator"), ("wss", "initiator"), ("quic", "responder")):
            other = parsed(recipe(transport, role))
            for host in ("0.0.0.0", "[::]"):
                data = peer_json(other)
                data[0]["peer"]["conns"][0]["tunnel"]["local_addr"]["url"] = f"{transport}://{host}:35000"
                with self.assertRaises(ProfileError): parse_peer_observation(other, json.dumps(data).encode())
        for url in ("quic://[::1]:35000", "quic://[::ffff:0.0.0.0]:35000", "quic://10.99.0.1:35000",
                    "quic://0.0.0.0:0", "quic://[::]:65536", "quic://[::]", "quic://[::]:35000?proxy=x"):
            data = peer_json(profile)
            data[0]["peer"]["conns"][0]["tunnel"]["local_addr"]["url"] = url
            with self.assertRaises(ProfileError): parse_peer_observation(profile, json.dumps(data).encode())

    def test_actual_upstream_url_and_serde_fixture_roundtrip(self):
        fixture = json.loads((ROOT / "docs/dependency-patches/easytier-2.6.4-profile/quic-bind-serde-fixture.json").read_bytes())
        self.assertEqual(len(fixture), 3)
        profile = parsed(recipe("quic", "initiator"))
        for index, projection in enumerate(fixture):
            # Full production-generated PeerRoutePair serde: key byte arrays,
            # auth enum, UUID fields, IPv4 route and all tunnel URL fields.
            observed = parse_peer_observation(profile, json.dumps(projection).encode())
            self.assertEqual(observed.local_bind_kind, "wildcard" if index < 2 else "exact")

    def test_quic_initiator_mapped_resolved_address_from_actual_producer(self):
        fixture = json.loads((ROOT / "docs/dependency-patches/easytier-2.6.4-profile/quic-loopback-endpoints.json").read_bytes())
        self.assertTrue(fixture["echo_verified"])
        self.assertFalse(fixture["noise_or_overlay_verified"])
        # Actual production outer transport produced a mapped client resolved
        # endpoint, but IPv4 configured remote and responder endpoints.
        self.assertIn("[::ffff:7f00:1]", fixture["client"]["resolved_remote_addr"]["url"])
        self.assertIn("127.0.0.1:", fixture["client"]["remote_addr"]["url"])
        for field in ("remote_addr", "resolved_remote_addr"):
            self.assertIn("127.0.0.1:", fixture["server"][field]["url"])
        profile = parsed(recipe("quic", "initiator"))
        data = peer_json(profile)
        tunnel = data[0]["peer"]["conns"][0]["tunnel"]
        tunnel["local_addr"] = fixture["client"]["local_addr"]
        # Same production URL builder applied to this public synthetic profile.
        # The loopback connection did not perform Noise/peer authentication.
        tunnel["resolved_remote_addr"] = {"url": fixture["synthetic_profile_mapped_url"]}
        observed = parse_peer_observation(profile, json.dumps(data).encode())
        self.assertEqual(observed.local_bind_kind, "wildcard")

    def test_mapped_equivalence_is_limited_to_exact_quic_initiator_resolved_endpoint(self):
        for transport in ("quic", "tcp", "wss"):
            for role in ("initiator", "responder"):
                profile = parsed(recipe(transport, role))
                for field in ("local_addr", "remote_addr", "resolved_remote_addr"):
                    if (transport, role, field) == ("quic", "initiator", "resolved_remote_addr"):
                        continue
                    with self.subTest(transport=transport, role=role, field=field), self.assertRaises(PeerObservationError):
                        data = peer_json(profile)
                        host = "::ffff:a4d:1" if field == "local_addr" else "::ffff:a4d:2"
                        port = (22001 if role == "responder" else 35000) if field == "local_addr" else (22002 if role == "initiator" else 35000)
                        data[0]["peer"]["conns"][0]["tunnel"][field] = dict(url=f"{transport}://[{host}]:{port}")
                        parse_peer_observation(profile, json.dumps(data).encode())
        profile = parsed(recipe("quic", "initiator"))
        for url in ("quic://[::ffff:a4d:3]:22002", "quic://[::ffff:a4d:2]:22003",
                "quic://[::ffff:a4d:2]:0", "quic://[::a4d:2]:22002", "quic://[fd00::a4d:2]:22002",
                "quic://[64:ff9b::a4d:2]:22002", "quic://[::ffff:a4d:2%25eth0]:22002",
                "quic://[::ffff:10.77.0.2]:22002", "quic://[::ffff:a4d:2]:22002?proxy=invalid",
                "quic://u@[::ffff:a4d:2]:22002", "quic://[::ffff:a4d:2]:22002/extra"):
            with self.subTest(url=url), self.assertRaises(PeerObservationError) as caught:
                data = peer_json(profile)
                data[0]["peer"]["conns"][0]["tunnel"]["resolved_remote_addr"] = dict(url=url)
                parse_peer_observation(profile, json.dumps(data).encode())
            self.assertEqual(caught.exception.reason, "PEER_RESOLVED_ENDPOINT_INVALID")

    def test_observation_failure_reasons_are_fixed_and_keep_old_error_contract(self):
        profile = parsed(recipe("quic", "initiator"))
        cases = [(b"private-invalid-input", "PEER_JSON_INVALID"), (b"[]", "PEER_SET_EMPTY")]
        for field, url, expected in (("local_addr", "quic://10.99.0.1:35000", "PEER_LOCAL_BIND_INVALID"),
                ("remote_addr", "quic://0.0.0.0:22002", "PEER_REMOTE_ENDPOINT_INVALID")):
            data = peer_json(profile); data[0]["peer"]["conns"][0]["tunnel"][field]["url"] = url
            cases.append((json.dumps(data).encode(), expected))
        for raw, reason in cases:
            with self.assertRaises(PeerObservationError) as caught:
                parse_peer_observation(profile, raw)
            self.assertEqual(str(caught.exception), "PEER_OBSERVATION_INVALID")
            self.assertEqual(caught.exception.reason, reason)
            self.assertNotIn("private", repr(caught.exception))
        for field in ("remote_addr", "resolved_remote_addr"):
            data = peer_json(profile)
            data[0]["peer"]["conns"][0]["tunnel"][field]["url"] = "quic://0.0.0.0:22002"
            with self.assertRaises(ProfileError): parse_peer_observation(profile, json.dumps(data).encode())

    def test_rejects_wrong_auth_key_network_direction_and_unencrypted_state(self):
        profile = parsed()
        for key, value in (("secure_auth_level", 0), ("secure_auth_level", 1), ("secure_auth_level", 2),
                           ("secure_auth_level", True), ("is_closed", True), ("is_client", False),
                           ("network_name", "other"), ("peer_id", 999), ("noise_remote_static_pubkey", list(ALICE_PUBLIC)),
                           ("noise_local_static_pubkey", list(BOB_PUBLIC)), ("noise_remote_static_pubkey", profile.remote.public_key)):
            with self.subTest(key=key, value=value), self.assertRaises(ProfileError):
                data = peer_json(profile); data[0]["peer"]["conns"][0][key] = value
                parse_peer_observation(profile, json.dumps(data).encode())

    def test_rejects_extra_peers_conns_routes_and_wrong_transport(self):
        profile = parsed()
        data = peer_json(profile)
        variants = [[], data * 2, dict(instance_id=profile.instance_id, result=data)]
        two = copy.deepcopy(data); two[0]["peer"]["conns"] *= 2; variants.append(two)
        for key, value in (("cost", 2), ("next_hop_peer_id", 103), ("proxy_cidrs", ["0.0.0.0/0"])):
            altered = copy.deepcopy(data); altered[0]["route"][key] = value; variants.append(altered)
        for url in ("tcp://8.8.8.8:22002", "tcp://10.77.0.2:22003", "tcp://10.77.0.2:22002?proxy=evil", "wss://10.77.0.2:22002/"):
            altered = copy.deepcopy(data); altered[0]["peer"]["conns"][0]["tunnel"]["remote_addr"]["url"] = url; variants.append(altered)
        for value in variants:
            with self.assertRaises(ProfileError): parse_peer_observation(profile, json.dumps(value).encode())
        for raw in (b"x" * 65537, b"not json", b'[{"peer":null,"route":null}]'):
            with self.assertRaises(ProfileError) as caught: parse_peer_observation(profile, raw)
            self.assertEqual(str(caught.exception), "PEER_OBSERVATION_INVALID")


if __name__ == "__main__":
    unittest.main()
