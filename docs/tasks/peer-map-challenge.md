# Instance entry for exact-map Data challenges

Status: **ready-for-agent** — production membership integration is implemented
and locally verified on a distinct 701-file candidate. The new 12 checks and all
59 retained checks pass with zero failures or ignored cases. The successful
nonce59 recipe and all currently validated runtime identities remain unchanged.

The [additive source patches](../dependency-patches/easytier-2.6.4-peer-map-challenge/README.md)
connect the actual `Instance` peer manager and map to the existing authenticated
Data challenge. An explicit peer/connection selection returns a weak ticket;
double pointer membership is rechecked before and after asynchronous sending,
when consuming a response and when reading that response. The single original
deadline is not restarted by delayed polling, and cancellation releases only the
attempt's real nonce slot.

This closes the source-level gap where only historical `list_peer_conns`
protobuf metadata was publicly accessible. It does not open a generic strong
connection getter or infer a live connection from a name, address, old JSON
record, auth level or pipe heartbeat. The actual key and endpoint policy remain
explicit caller inputs. The method provides bounded current-membership samples,
not an atomic global snapshot or complete Instance/process/NIC ownership.

The positive fixture calls the production Instance entry over an in-memory Noise
connection. Removal, same-ID object replacement, retained old objects, delayed
send completion, cancellation, deadlines and nonce replacement are exercised
without starting core, a TUN/driver, a remote UDP service or a game. The existing
59 nonce/authentication/ingress checks passed against the same preserved final
test executable. A deliberate mutation removing both map-to-peer pointer checks
compiled and produced 11 passes and one assertion failure: replacing the peer
with a different object under the same ID must invalidate a response even when
that replacement contains the same connection. Restoring the exact source and
running its preserved executable returned 12/12. Applying, reversing and
reapplying both patches restored the complete 700-file baseline and exact
701-file tested tree; the new module disappeared on reversal. The
[receipt](../dependency-patches/easytier-2.6.4-peer-map-challenge/receipt.json)
binds patches, source, executable and logs. Windows compilation and integration
with the independently tested owned-read IPC require their own composed source
identity and execution evidence.

Ordinary Public startup stays closed. The next composed candidate must preserve
the existing successful nonce59 recipe, use an explicit new patch/source
identity, and keep owned process, retained NIC and peer/session observations
within the actual producer before any launcher/native admission is considered.
