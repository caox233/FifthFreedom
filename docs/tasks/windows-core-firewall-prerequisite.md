# Actual Windows firewall readback prerequisite

Status: **ready-for-agent**. Actual fast probes have now identified a present-null
Package property. The narrowly corrected production predicate and its actual
Windows checks are under verification; full core/NIC execution remains separate.

## Retained diagnostic steps before the Package correction

Run `40371709889`, job `121743990739`, source `c42e0eb`, checkout merge
`64198de3d124a12152076754ec4cf88c6b6892f6`, compiled the thirteen-patch inert
core/CLI and passed the managed22 input contracts. The runtime adapter then
reported `FIREWALL_RULE_CHANGED` in Prepare and again during Restore. No managed
runtime or successful firewall Prepare record exists; driver inventory before
and after is clean and the unrelated-device hash is unchanged. This is not a
demonstrated core startup, NIC cleanup failure or successful firewall removal.

The official artifact `12673029377` ZIP is 51,869 bytes with independently checked
SHA256 `d48194e51f439fd08de1392cdac549c1eb67e367e888418bf22ba41c72f9074b`.
The old fixed error combined many predicates, so it cannot identify the rejected
field. No casing or Windows default-value explanation is assumed from this code.

The production PowerShell checker now retains a fixed field/store/direction label
and an optional path-case-equivalence boolean. It still uses every original
strict predicate. Raw path, rule name, filter values, policy, exception text and
environment contents remain private. The Python adapter validates those labels
before preserving the bounded observation even when the operation fails.

The independent `windows-core-firewall-prerequisite.yml` workflow requires a
disposable elevated Windows2025 GitHub runner. It copies the runner's Python
executable into a fresh named staging directory only as inert file data, creates
explicitly synthetic manifest identities and executes the actual production
Prepare/unarmed Restore functions. It never executes that image, builds/starts
EasyTier, loads a driver, launches a game or changes firewall profiles.

It deliberately does not Arm. No owned process/job exists, so it cannot fabricate
the exact-job-drain receipt required after Arm. The full runtime workflow retains
that separate boundary. Any unexpected readback prevents startup; a rule whose
ownership cannot be validated is retained for disposable-VM cleanup, not removed
under a fabricated success. Raw private manifests/plans and copied images are
never uploaded.

The diagnostic workflow can run alone on the narrowly named
`codex/firewall-diagnostic` development branch; this avoids rebuilding core merely
to discover a failed firewall field. Its own result is not core/NIC acceptance.
After the actual rejected field is observed, make a focused correction with
negative controls and rerun both prerequisite and full runtime as applicable.

Independent review additionally found that both the new prerequisite and the
existing runtime adapter assigned cleanup authority before exclusive control
directory creation succeeded. A rejected preexisting directory could therefore
cause Restore on a prior invocation. Normal execution now acquires cleanup
authority only after creating its own directory; explicit `--finalize` remains
the separate recovery path. Two concrete preexisting-plan/session regressions
failed on the prior implementation and pass after the correction, with prior
bytes unchanged and no Restore call. The focused adapter15 and prerequisite6
checks pass locally. This adjacent ownership defect is not identified as the
cause of the first hosted `FIREWALL_RULE_CHANGED` failure.


The first fast probe, run `40377415703` / job `121764159359`, at source
`991ec1b6d7ac25d0593e7c97ba69830e8fd6ad85` reproduced the failure precisely at
`APPLICATION_PACKAGE`, Inbound/PersistentStore, during both Prepare and Restore.
The preceding exact path check passed. Artifact `12677320024` is 572 bytes with
verified SHA256 `910d5c78f38794396dc35caea6f04a32f202eeda98b2681cbd152f8d390d89dd`.
A bounded package-property class (missing/null/empty/Any/SID/other) is now added to
identify the actual CIM representation before correcting its predicate; actual
values are never published. Path equality and all admission predicates remain
unchanged. This failed probe did not Arm, execute its image or start core/driver.

Microsoft's [New-NetFirewallRule Package parameter](https://learn.microsoft.com/en-us/powershell/module/netsecurity/new-netfirewallrule?view=windowsserver2025-ps#-package)
describes a Store application SID and default `None`. This supports checking the
actual unconfigured representation rather than assuming a literal `Any`; it
does not replace the bounded Windows observation required here.


## Actual package representation

The next actual run `40378390311`, job `121767626373`, at `74d1429`
confirmed the Package property is **present and null**, not missing, at both
Prepare and unarmed Restore. Official artifact `12674254739` has verified ZIP
SHA256 `65b6ffc0589e3061a7d9a7159a469dd05050e32c19ad22cf87086afff39c0b20`.
This is a bounded observed class, not a published provider value or policy dump.
The exact application path comparison passed and remains unchanged. A shared
production predicate accepts that observed default-None representation and the
retained exact literal Any, while rejecting missing/empty/non-string/case-variant
or specific-SID values. Actual PowerShell positive/negative contracts and all
other strict readbacks must pass in Windows before claiming the prerequisite
fixed. No core/NIC runtime success is inferred from a pure or unarmed probe.
