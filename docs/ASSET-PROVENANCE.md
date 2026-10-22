# Build assets and public-data boundary

The project owner states that redistribution authorization exists for the requested
game DLL, music, artwork and third-party build inputs. Source links and the precise
authorization conditions will be supplied by the owner. This is a recorded owner
statement, not an independent legal verification. The source license does not
relicense third-party binaries, media or SDK headers.

`scripts/local-assets.json` pins the existing build inputs. Reviewed non-save
resources are retained in this migration. Do not replace a pinned input merely to
make a missing-file check pass. Add matching upstream notices and source references
when available; FusionFix's archive currently lacks its upstream license notice.

`docs/ASSET-PUBLICATION.json` lists default SAV/META inputs held locally pending
privacy verification. No actual secret was identified, but opaque SAV buffer I/O
and a SHA-256 manifest do not establish that player-specific fields are absent.
The public source checkout therefore does **not yet independently build a complete
game package**. Code, source tests, protocol work and UI integration can proceed in
the cloud. A full Windows CI package is gated on approved matching inputs or a
reviewed, reproducible default-data replacement. Do not upload personal saves,
fake an asset, weaken package checks, or call this gate a successful build.

The pinned capture helper binary is preserved, but its build project has not yet
been migrated. Record this source-rebuild gap instead of calling every binary
independently reproducible. Local migration builds used the retained inputs and
are distinct from a clean public checkout build.

Legacy live deployment host defaults in the SCBL3 donor are replaced by inert
`.invalid` examples. Explicit deployment configuration belongs to cloud
integration; no automatic connection to a previous operator's service is intended.
