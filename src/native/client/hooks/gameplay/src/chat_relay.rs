//! Master-only relay of ordinary retail ChatString receipts.
//! Keeps the engine message borrowed only during SendMasterUnicast, preserves
//! native serialization/queue ownership, and never copies or reads chat text.
use crate::equipment_native::{readable, signature, word};
use std::cell::Cell;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};

static READY: AtomicBool = AtomicBool::new(false);
static LOG_COUNT: AtomicU32 = AtomicU32::new(0);
thread_local! { static RELAYING: Cell<bool> = const { Cell::new(false) }; }
struct Guard;
impl Drop for Guard { fn drop(&mut self) { RELAYING.with(|v| v.set(false)); } }

type Descriptor = unsafe extern "stdcall" fn(*mut [u32; 2]) -> usize;
type Service = unsafe extern "C" fn() -> usize;
type Send = unsafe extern "C" fn(*mut i32, u32, u32, *const [u32; 2], usize, u32, u32, u32, u32) -> usize;

// Explicit native boundary is also used by the offline x86 ABI replay. Tests do
// not replace peer selection, lifecycle validation or the production call ABI.
#[derive(Clone, Copy)]
pub(super) struct Native {
    pub descriptor: Descriptor,
    pub service: Service,
    pub send: Send,
    pub namespace: usize,
}
#[derive(Clone, Debug, PartialEq, Eq)]
struct Session {
    manager: usize,
    session: usize,
    owner: u32,
    handle: u32,
    match_key: [u32; 2],
    namespace: [u32; 2],
    peers: Vec<u32>,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Outcome {
    pub state: &'static str,
    pub attempted: u32,
    pub positive_native_results: u32,
}
impl Default for Outcome {
    fn default() -> Self { Self { state: "ignored", attempted: 0, positive_native_results: 0 } }
}
// At most 16 fixed-format records per DLL lifetime. No peer IDs, names, message
// strings or keystrokes are accepted by this formatting boundary.
pub(super) fn summary(out: Outcome, team_only: bool) -> Option<String> {
    LOG_COUNT.fetch_update(Ordering::Relaxed, Ordering::Relaxed,
        |n| (n < 16).then_some(n + 1)).ok()?;
    Some(format!("SCBL_MOD_CHAT_RELAY state={} channel={} attempted={} positive_native_results={} remote_receipt=unconfirmed",
        out.state, if team_only { "team" } else { "global" }, out.attempted, out.positive_native_results))
}
unsafe fn ptr(p: usize, o: usize) -> Option<usize> {
    word(p, o).filter(|v| *v >= 0x10000).map(|v| v as usize)
}
unsafe fn byte(p: usize, o: usize) -> Option<u8> {
    let p = p.checked_add(o)?;
    readable(p, 1).then(|| (p as *const u8).read())
}
unsafe fn session(net: usize, namespace: usize) -> Option<Session> {
    if word(net, 0)? != 0x2af70fc || word(net, 0x14)? != 0 { return None; }
    let manager = ptr(net, 0x88)?;
    if ptr(manager, 0x2c0)? != net { return None; }
    let session = ptr(net, 0x50)?;
    let owner = word(net, 0x18)?;
    let handle = word(net, 0x6c)?;
    if owner == u32::MAX || handle == u32::MAX { return None; }
    let sentinel = session.checked_add(0x584)?;
    let mut node = ptr(session, 0x588)?;
    let mut nodes = Vec::new();
    let mut peers = Vec::new();
    while node != sentinel {
        if nodes.len() >= 32 || nodes.contains(&node) { return None; }
        nodes.push(node);
        let peer = ptr(node, 8)?;
        let id = word(peer, 0)?;
        if id == u32::MAX || peers.contains(&id) { return None; }
        peers.push(id);
        node = ptr(node, 4)?;
    }
    // Some native session views may enumerate remote peers only. The owner is
    // obtained from the host network object, not inferred from list membership.
    Some(Session { manager, session, owner, handle, peers,
        match_key: [word(net, 0x70)?, word(net, 0x74)?],
        // Native chat uses these globals, NOT the network object's match key.
        namespace: [word(namespace, 0)?, word(namespace, 4)?],
    })
}
unsafe fn ordinary_from(message: usize) -> Option<u32> {
    if !readable(message, 0x68) || word(message, 0)? != 0x2b18344
        || byte(message, 0x54)? == 255 { return None; }
    word(message, 4).filter(|v| *v != u32::MAX)
}
unsafe fn service_ready(native: Native) -> bool {
    let p = (native.service)();
    let Some(a) = ptr(p, 4) else { return false; };
    let Some(a) = ptr(a, 4) else { return false; };
    if word(a, 0x90).is_none() { return false; }
    let Some(q) = ptr(p, 8) else { return false; };
    let Some(vt) = ptr(q, 0x30) else { return false; };
    ptr(vt, 8).is_some()
}
pub(super) unsafe fn relay_with(net: usize, message: usize, native: Native) -> Outcome {
    let mut outcome = Outcome::default();
    if RELAYING.with(|v| v.replace(true)) { outcome.state = "reentrant"; return outcome; }
    let _guard = Guard;
    let Some(from) = ordinary_from(message) else { outcome.state = "not_ordinary"; return outcome; };
    let Some(start) = session(net, native.namespace) else { outcome.state = "context_rejected"; return outcome; };
    if from == start.owner || !start.peers.contains(&from) { outcome.state = "sender_rejected"; return outcome; }
    let mut descriptor = [0u32; 2];
    if (native.descriptor)(&mut descriptor) != (&mut descriptor as *mut _) as usize
        || descriptor[0] == 0 { outcome.state = "descriptor_rejected"; return outcome; }
    outcome.state = "no_other_peers";
    for &target in &start.peers {
        // Host already displays the receipt; the sender has a local echo.
        if target == start.owner || target == from { continue; }
        if ordinary_from(message) != Some(from)
            || session(net, native.namespace).as_ref() != Some(&start) {
            outcome.state = "context_changed"; break;
        }
        if !service_ready(native) { outcome.state = "service_unavailable"; break; }
        let mut result = 0i32;
        (native.send)(&mut result, start.handle, target, &descriptor, message,
            0, u32::MAX, start.namespace[0], start.namespace[1]);
        outcome.attempted += 1;
        outcome.positive_native_results += u32::from(result > 0);
        outcome.state = "native_attempted";
        // No retry, text cache, retained message pointer, or synthetic local UI.
    }
    outcome
}
pub(super) unsafe fn relay(net: usize, message: usize) {
    if !READY.load(Ordering::Acquire) { return; }
    let out = relay_with(net, message, Native {
        descriptor: std::mem::transmute::<usize, Descriptor>(0x10403d0),
        service: std::mem::transmute::<usize, Service>(0x206f300),
        send: std::mem::transmute::<usize, Send>(0x1fdd530),
        namespace: 0x33be390,
    });
    // Only ordinary host receipts use this bounded diagnostic. Reserved 255
    // control messages and replica-only receives neither log nor relay.
    if word(net, 0x14) == Some(0) && ordinary_from(message).is_some() {
        if let Some(line) = summary(out, byte(message, 0x64).is_some_and(|v| v != 0)) {
            tracing::info!("{}", line);
        }
    }
}
// Populated from the pinned retail DX11 image by the native replay generator.
const CONTRACTS: &[(usize, usize, &str)] = &[
    (0x10403d0, 123, "c6f2c133d3fe9b210f14a8a2df8da1c872ac6318f2ec80de9038ebdf9902f4e7"),
    (0x1fdd530, 210, "77e7d158ab3f50c7e19bff6e97952bcfb8f65c84c52c320fae13a636a235411d"),
    (0x1fd8220, 205, "e56e45f42aa1ee169337622181f4053851489e5780d373e63209d001f6d6488d"),
    (0x2064760, 33, "8f9781221d5853f073059f6d87e774a1369875bb7ad3041907afecf812024cc6"),
    (0x20618a6, 50, "72194a52fb5755525800404535841aade7aeb2bb8486c5d2c1fb8e44f774a627"),
    (0x2040350, 2233, "e85f950e9f0d044f496ae1cff74b03ef35214ac6631114ae29073a759634f473"),
    (0x1f97850, 15, "565e719d22ac126255e5385b008b8c0e7a32398d877a4a32b4e7e7c1179a1544"),
    (0x1fdb1b0, 66, "b95ae0c9d80dd15c81b64061d950f6e36bcd6d0fe09288b9afe191a0c2578c5f"),
    (0x7a1290, 580, "5bf328295f8ca55c0e50f5b8e29dee27b30a37d35fa8d3415fb28afa5b0ea577"),
    (0x105c100, 296, "2c8ca194330e7612c86cae04eab1e949e1ff51423c722b42ead14be8bcdcdee5"),
    (0x206f300, 122, "d14ea28b7312a1b53a7dd0885eae8680068cd87ed74cf1fc78ed6ee339556d89"),
];
pub(super) unsafe fn init() -> bool {
    let ready = !CONTRACTS.is_empty() && CONTRACTS.iter().all(|&(p,n,h)| signature(p,n,h));
    READY.store(ready, Ordering::Release);
    ready
}
pub(super) fn deinit() { READY.store(false, Ordering::Release); }
