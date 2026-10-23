# Exact owned Windows core process primitive

Status: implemented; review and actual Windows synthetic-child execution pending.
Public remains unavailable. This slice runs no EasyTier core, overlay, driver or
game. Its tests execute a separately built synthetic helper only.

## Existing code and the missing boundary

`PublicReturnProcess` already duplicates an exact process handle and captures its
kernel PID, creation time and image path. It does not validate image bytes, own a
job, or implement parent-death cleanup. The new overload accepts the actual
`SafeProcessHandle` returned by process creation; the existing `Process` overload
and its error contract remain equivalent. The same fixed rejection factory is
now local to that identity helper so its production code can be linked into a
minimal process test without importing the token/session subsystem.

`SuspendedDiagnosticLaunch` serves the existing optional game diagnostic flow.
It reopens a process by PID and resumes it on disposal; it is not reused or
changed for this new ownership boundary. `HostRuntimeService` retains its LAN
behavior. No name-based donor process cleanup or default donor endpoints enter
this implementation.

## Actual creation and ownership

`OwnedCoreLaunchInputs` requires explicit canonical absolute image/configuration
paths and independent expected lengths/SHA-256 values. These are declared input
identities, not publisher verification, configuration-policy approval or runtime
license closure. File bytes are checked through retained read handles excluding
write/delete sharing. Every ancestor is opened from root outward, rejects
reparse points and excludes delete sharing before its descendant is opened.
This fixes the path used for creation rather than treating a returned path as a
hash proof. The image/configuration handles remain held until owned shutdown is
observed. Config contents and hashes are never logged or exposed by `ToString`.

`CreateSuspended` creates an unnamed, non-inheritable job with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. `PROC_THREAD_ATTRIBUTE_JOB_LIST` supplies
that exact job to `CreateProcessW`, together with `CREATE_SUSPENDED`, a fully
qualified application path and no inherited handles. There is no
create-then-assign window and no fallback that runs an uncontained child. An
unsupported API or incompatible inherited job fails closed; existing jobs are
not altered and no breakaway flag is used.

The raw returned process handle is retained and duplicated by
`PublicReturnProcess`. Membership in the exact job and kernel process/image
identity are checked before `ResumeAsync` permits execution. Creation and resume
both check cancellation; resume checks again after the native resume operation
and drains a cancelled attempt before throwing. A caller can prepare future
owner IPC between creation and resume, but this patch implements no such IPC or
admission capability.

The argument shape is fixed to `--config-file <absolute path>`,
`--disable-env-parsing`, `--console-log-level off`, `--file-log-level off`, matching
the pinned core's CLI. The environment is constructed from OS Windows/system
directory locations and the per-attempt configuration directory for TEMP/TMP.
It does not inherit `ET_*`, credentials, `FF_PUBLIC_RETURN_V1`, arbitrary PATH or
DLL search overrides. Input path/exception text is not included in diagnostics.
No shell, PID/name scan or external process adoption participates in creation.

## Shutdown and failure boundaries

Explicit stop terminates only the exact owned job, then observes both the exact
primary process exit and job `ActiveProcesses == 0` inside one five-second async
budget. Only then are the job, identity, thread, process and input handles
released. Failed termination/query/wait retains the handles so a later stop can
retry. Multiple observers share the drain operation. Parent death closes the
sole job handle and invokes the kernel's kill-on-close containment, including
children created by the synthetic process; no inherited job handle keeps it
alive.

A failed post-create validation still has the raw exact child handle. It closes
the job and, if that child is still alive, attempts termination of that handle
without relying on a successful membership query, then waits up to five seconds.
A failure to observe exit is reported as `OWNED_CORE_CLEANUP_FAILED`, never as a
successful cleanup. The pre-resume child has executed no descendant creation.
This exceptional branch is reviewed but not claimed as fault-injection tested.
Synchronous Windows kernel calls are not forcibly preempted by these async wait
budgets; hosted jobs have an independent outer deadline.

## Tests and remaining evidence

`tests/OwnedCoreProcess` directly links the three production files. Its synthetic
helper reads inert JSON test instructions through the real fixed `--config-file`
argument shape; this does **not** test EasyTier TOML parsing or networking.

Five portable cases reject malformed hashes, unbounded sizes, relative/aliased
paths, revealing representations and pre-cancelled starts. Twelve Windows cases
exercise actual suspended creation/identity/resume; image/config hash rejection;
held config/ancestor write-delete-rename boundaries; cancellation and disposal
before resume; natural exit; absence of ambient network/return environment;
reparse rejection; real parent termination killing both child and descendant;
and explicit stop killing its descendant while a foreign same-image helper
remains alive. The test's PID reopening is only for independent observation of
identities reported by these owned synthetic children, verified against creation
time; production ownership never uses it.

Actual local results: 5 passed / 12 explicitly Windows-only skipped. The retained
Public native-return suite passed 14 portable cases with 13 Windows-only skips,
and the WPF project cross-compiled with zero warnings/errors. These are not
Windows process execution results. The intended hosted lane publishes a private
x86 self-contained test helper, matching the launcher's process architecture,
then requires all 17 cases and zero skips. It does not execute the full core or
publish a runtime package.

The [actual ten-patch core build](../evidence/hosted-windows-easytier-23d5923.json)
still directly imports `packet.dll`. The concrete Public provider remains closed
until reviewed runtime inputs, actual owned-core IPC, authenticated peer state,
current source/route proof and existing native/game boundaries are connected.
This primitive and a synthetic child cannot discharge those separate gates.

Windows API references:

- [UpdateProcThreadAttribute / JOB_LIST](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)
- [Job object limits and kill-on-close](https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information)
- [Nested jobs](https://learn.microsoft.com/windows/win32/procthread/nested-jobs)
