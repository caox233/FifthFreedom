# Restore managed GitHub access and resume reviewed development

Status: **ready-for-human** for the platform identity repair only. Development
permission has already been granted; routine code, dependency, branch, PR and
candidate-build work does not require another approval.

The previously working official GitHub API began returning HTTP 401. Normal Git
push and remote-ref reads then failed with exit 128 because a username could not
be obtained. Credential values were neither printed nor copied. A single ordinary
public API read, without an Authorization header and with the existing network
configuration unchanged, also returned 401. No alternative credential, network
bypass or repeated retry loop was used.

The last successfully pushed and verified source branch is
`fe6e3cd5992888273bada9c7810a264cf8d54630`; the knowledge branch is
`b809b4a6c8efac0c63627630f0ac3fb8413939a9`, which binds source
`6954eea73f6cd7ee7766ba34efc823ac85d2a319`. Later reviewed commits are retained
locally. An unpushed commit is not available from the existing GitHub PR.

Official environment status still reports the running managed instance. Its
listed capabilities do not provide credential refresh, Goal scheduling or
environment publication. Retained network diagnostics were not found. Tool
discovery found no GitHub connector or official credential-refresh operation.
These are limits of the exposed platform interfaces, not missing user permission.
The required external action is to restore this cloud session's GitHub identity
and repository authorization through the platform's supported controls. No token
needs to be pasted into chat. The exact recovery UI is not exposed by these tools.

The saved environment draft remains revision 19, bound to source
`6954eea73f6cd7ee7766ba34efc823ac85d2a319` and knowledge
`b809b4a6c8efac0c63627630f0ac3fb8413939a9`. It requires publication; no publication or fresh-task
restore was verified. Local unpublished revisions must not replace its repository
pins. The current turn can execute without the owner's computer; no persistent
Goal or unlimited execution after the turn is promised.

## Resume after actual access is restored

1. Preserve the local development branches and inspect their current commits and
   worktree status. Fetch normally and compare the remote branches with the last
   verified revisions; preserve any intervening changes. Do not force-push or
   reset away reviewed work.
2. Push the reviewed source branch and verify its exact remote commit. Inspect
   the existing draft PR and its actual checks before dispatching additional
   candidates. PR path filters compare the cumulative PR diff and therefore do
   not guarantee that documentation updates on this long PR avoid another run.
3. Read the real results of first Windows composition run `39947362637`, detailed
   kernel matrix `39947934755` and observer-policy candidate `39947934694`.
   They were still running at the last successful observation. Authentication
   failure proves neither that they stopped nor that they passed. Two attempted
   duplicate-run cancellations were not completed and must not be recorded as
   successful.
4. Run the new independent 71-contract Linux builder, dependency-file 13-case and
   owned-read 22-case Windows suites. Complete and independently review the inert
   Windows core/NIC lane before its first execution. Verify the exact source, official artifact digest, complete
   fixed receipts and actual cleanup. Prior 59, 19 and 17-case successes do not
   satisfy these new checks. Linux compilation does not satisfy Windows execution.
5. Re-run the appropriate native/overlay composition with the query-exit fix,
   observer policy and bounded kernel diagnostics. Retain the earlier
   `QUERY_IMAGE_CHANGED` and eight-of-nine-phase failures. A reproduced source
   race is not proof of their unique cause.
6. Push the independently reviewed source-bound DevDocs snapshot and verify its
   remote revision. Update both draft PR descriptions. Only then refresh the
   environment draft through the official interface, preserving unrelated
   configuration and distinguishing draft save from publication/restoration.

Full migration remains the six ordered goals in `PROJECT-STATE.json`. The held
SAV/META privacy inputs, retail dependencies, Capture source handoff, exact ASI
source/rebuild and remaining live gameplay evidence stay open. Ordinary Public
admission, whole-game network confinement and complete Windows packaging remain
unverified. Accepted F3, MAC-11 HUD, Chinese/world-camera and VSS EMF results are
preserved. No game, capture, production deployment or formal Release is started
by this recovery procedure.

Local recovery bundles, when produced, include only explicit reviewed public Git
branches and are checked by an independent offline restore. They exclude ignored
private evidence and build caches. A bundle retained in this same cloud workspace
is not a verified off-machine durable backup; its receipt must say whether a
remote push or external preservation actually occurred.
