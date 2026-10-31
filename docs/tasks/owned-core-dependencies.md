# Owned core dependency file lifetime

Status: implemented and independently reviewed; actual hosted Windows13/13,
zero skips passed at `c42e0eb`, run `40371709757`, job `121743870443`.
The official artifact digest and exact merge/source binding were independently
verified in the [recovery receipt](../evidence/hosted-recovery-c42e0eb.json).

The actual process creation path can now accept an optional immutable
`OwnedCoreDependencyInputs`. Its sole factory identifies the reviewed official
Wintun 0.14.1 AMD64 member. The source URL, archive size/digest and member
size/digest match the [preserved official evidence](wintun-official-binary-evidence.json).
It accepts no caller-supplied digest, arbitrary dependency name or environment
item. This identifies reviewed bytes and their recorded origin; it does not
independently establish licensing, Authenticode validity or runtime readiness.

The only accepted path is canonical `wintun.dll` immediately beside the declared
executable. It cannot alias the executable or configuration. The existing
`OwnedCoreLaunchInputs.Pin()` opens and validates image, configuration and optional
DLL before `CreateProcess`. It uses the existing ancestor handles, reparse checks,
exact byte digests and no-write/no-delete file sharing. Partial failure disposes
every already-acquired input handle and creates no child.

There is no separately disposable dependency lease. The existing exact job owns
all input handles until the primary process is signaled and job accounting reports
zero active processes. Primary exit alone cannot release the DLL while an owned
descendant lives. A failed job drain retains the same pins and retry authority.
The absent optional argument preserves ordinary input/process behavior; no Public
provider or application launch callsite opts in automatically.

`tests/OwnedCoreDependencies` links the production classes and the preserved
official origin JSON. Five portable checks passed locally; the eight Windows
checks subsequently passed in the hosted run above. The latter use actual official DLL bytes as file data
only, never loaded as a module, and verify actual suspended/running synthetic-child file locks, mutation,
missing/reparse inputs, partial pin failure, descendant retention, cancelled
resume cleanup and the explicit no-dependency branch. The suite copies the
verified input once into its own fresh published application directory using
`CreateNew`; it deletes only that created copy during final cleanup. It neither
loads this AMD64 DLL into the x86 helper nor calls a Wintun API.

The intended hosted invocation is a self-contained `win-x86` publication of
`tests/OwnedCoreDependencies`, then its exact apphost with
`--wintun-input <verified official AMD64 member>`. Windows startup checks actual
process architecture. All thirteen cases and zero skips are required; output
contains fixed case/exception markers, never arbitrary paths or exception text.
Cleanup failure remains a nonzero result even after the summary was printed.

The retained process/read projects still compile against the same production
files, with local results 5/12 and 8/11 respectively (passes/explicit Windows
skips). WPF cross-compilation passed with no warnings or errors. Private logs and
input hashes are under `.local/evidence/cloud/owned-core-dependencies/`.

This slice proves file identity/lifetime only. It does not prove loader search
resolution, load all runtime dependencies, execute EasyTier, install a driver,
configure a NIC or grant Public admission. The later finite actual core/NIC probe
also requires an inert configuration, test containment and the newly compiled
combined core; those are separate evidence.
