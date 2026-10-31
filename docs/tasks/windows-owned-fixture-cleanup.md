# Windows owned fixture directory cleanup

Status: implemented; actual hosted Windows validation pending.

The Windows quick run
[39921420637](https://github.com/caox233/FifthFreedom/actions/runs/39921420637),
job `120307305674`, isolated the `PublicNativeReturn` case 15 failure to
`OWNED_DIRECTORY_CLEANUP`, with `UnauthorizedAccessException`. Its fixed facts
were `child_exited=YES`, `process_disposed=YES`, `config_disposed=YES`, and
`readonly_files=NO`. The final value describes files remaining after the failed
recursive deletion. It does not identify which external component held a file
or establish a particular Windows kernel/antivirus cause.

The preceding full candidate at `6c11c8b` also failed case 15, but its older
`CONFIG_ACK` label covered delivery, exit and cleanup. That run alone did not
locate the exception. The still older `0ac` unclassified failure remains without
a confirmed cause; neither one is retroactively claimed to have the same cause.

## Change

`tests/PublicNativeReturn/OwnedDirectoryCleanup.cs` retries deletion only for the
fresh directory created and owned by this fixture. On Windows the accepted error
codes are access denied (`5`), sharing violation (`32`) and lock violation (`33`).
The single deadline is two seconds, with at most 100 ms between attempts. Each
retry requires the recorded exact child exit and successful disposal of its
process handle and the configuration pipe. A persistent denial or another error
still fails, preserving the first cleanup exception. The enclosing case also
preserves an earlier test failure when cleanup subsequently fails.

The helper changes no ACL or file attributes, terminates no additional process,
and does not retry any pipe connection, ACK, identity or authentication operation.
Production launcher and native sources are unchanged.

## Verification

The existing 25 case numbers stay stable. Two Windows cases are appended:

- Case 26 holds an actual file with `FileShare.None`, starts cleanup, verifies
  that it remains pending, then releases that exact handle. Deletion must finish
  after more than one attempt.
- Case 27 keeps the file locked through a short deadline and requires a failure
  with the file still present. It also refuses retries when the caller reports
  that owned handles have not been released.

The normal Windows contract is now **27 passed, zero skipped**. Linux runs the
same 14 portable cases and explicitly skips the 13 Windows-only cases. The
separate native/managed interoperability contract remains three cases. Linux
compilation and portable results cannot establish Windows deletion behavior;
the next quick hosted run must validate both added cases and the original case
15. No complete package, game or GPU acceptance follows from these tests.
