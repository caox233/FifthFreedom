# Authenticated receive prerequisite for owned Public transport

Status: **ready-for-agent**. Public remains closed. This is one implemented source
prerequisite in the full migration, not completion of Windows ownership or the
six project goals.

The [new source candidate](../dependency-patches/easytier-2.6.4-authenticated-receive/README.md)
adds a production-linked local Data receive observation. It records only after
actual session AEAD/replay success and invalidates retained observations on owner
loss, connection close, session replacement, shared-session key sync or generation
changes. Its explicit sync revision closes the independently reproduced
same-generation/ABA defect in the first implementation. It does not grant a lease.

The remaining data chain has concrete existing endpoints:

| Link | Existing implementation and precise remaining boundary |
| --- | --- |
| Launcher → owned core | `PublicReturnProcess.Capture` already duplicates the exact `Process.Start` HANDLE and checks process creation time and image path for game-return ownership. Its kernel PID/handle approach is reusable. A core launch still needs its own verified executable/config, retained exact handle and parent-death/JobObject lifecycle; a later PID/name lookup is insufficient. |
| Core → retained kernel session | UI-09 `NicCtx::observe_windows_session` reads the actual Wintun Session/Adapter observer. `Instance::NicCtxContainer` currently stores its context behind private `Box<dyn Any>`; a separate typed accessor is being implemented. Adapter names, config IPs or an unrelated interface snapshot cannot supply that retained owner. |
| Core → authenticated peer receive | The new `PeerConn::authenticated_data_observation` binds the actual Noise session to accepted local encrypted Data. Historical auth/key JSON, plaintext Ping/Pong and repeated local CLI polling cannot refresh it. An active, bounded challenge and explicit expected-peer policy remain separate. |
| Core/launcher → native lifetime | Existing Public-return code already implements exact pipe-server/client PID checks, pinned process birth/image anchors, bounded frames, identification-only SQOS and cancellation/drain. Its current direction returns native authentication to the launcher; it is not a core-owned readiness channel. A new owner handshake must bind exact core process, kernel-session identity, expected peer, current game instance and a one-use generation with expiry/EOF revocation. |
| Native lifetime → API sockets | UI-08 `LiveOverlayLease` already closes established streams, wakes pending I/O, fences late connections/publication and checks source/index/LUID on every connect. Its production issuer remains closed. Config JSON, a pipe heartbeat or observed source address cannot instantiate it. Other game UDP paths are still outside that API-only slice. |

A local receive age reports a completed event, not future remote liveness. The new
observation deliberately contains no serialized authorization, private material,
callback claiming cryptographic proof or replacement admission factory. Next work
must combine actual runtime ownership and challenge evidence through these existing
endpoints, and preserve failures at each boundary rather than convert them into
readiness. Kernel/driver probes and full builds retain their own exact source and
execution receipts; they are not inferred from these local filter tests.
