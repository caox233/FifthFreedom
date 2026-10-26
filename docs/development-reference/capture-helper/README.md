# Historical Capture helper source reference

This is a recovered historical source handoff, not a production replacement or
new capture feature. No binary was rebuilt or executed for this handoff. The
existing embedded Capture binary remains unchanged.

## Identity and evidence boundary

- `capture-helper.cpp`: 16,267 bytes, SHA256
  `bed779d4302b68e4397a8e3fe9afddfa886c9b96c8667a4a9279def85e11a43b`.
- Existing embedded `SCBL.LAN.Capture.exe`: SHA256
  `d34d192bb97a379c4b063c24f7cea6789005b9e5a5584404b701dfd4e7533a11`.
- Historical local manifests associate this source and binary with the retained
  helper. That association is not proof of reproducible or equivalent behavior.
- A complete source read found no embedded credentials, personal account data,
  user-specific absolute paths, or recorded session contents. Private provenance
  records, runtime logs, dumps and captures are deliberately not included.

## Historical build recipe (not rerun here)

Build in an x86 MSVC developer shell. The recorded toolchain is MSVC
14.44.35207 with Windows SDK 10.0.26100. The single translation unit declares
`dbghelp.lib` and `bcrypt.lib`; it also uses the Windows SDK and C++ standard
library.

```text
cl.exe /nologo /EHsc /std:c++17 /MT /Od /Oy- /Zi capture-helper.cpp /Fe:SCBL.LAN.Capture.exe /Fo:capture-helper.obj /Fd:capture-helper.compile.pdb /link /machine:x86 /INCREMENTAL:NO /DEBUG /PDB:capture-helper.pdb
```

Debug paths, timestamps and toolchain variations may change output bytes.
Do not claim binary identity from the manifest or from a successful compile.

## Cloud continuation

Verify the source hash, inspect the entrypoint and dependencies, then establish a
clean Windows build and focused fixture tests. Reconcile the existing capture
source/provenance task with this reference without clearing the distinct retail
fixture, missing asset, distribution or live acceptance gates. Integrate a
replacement into production only after explicit parity and lifecycle checks.
Do not automatically launch a game or perform diagnostic capture. Dump contents
can be sensitive and must remain private.