//! Remove only this drone's dead-Spy tracking entries before native loss/linger.
//! Native B51FC0 otherwise refreshes an acquired lost target as active for BE4
//! seconds. Keep other actors, sources, ownership and the remaining native scan.
use super::drone_view::dead_spy_target;
use crate::equipment_native::{readable, word, writable};

#[derive(Clone, Copy, PartialEq, Eq)]
struct Context {
    owner: usize,
    source: usize,
    id: [u32; 2],
    data: usize,
    count: usize,
    capacity: usize,
    state: u32,
    role: u32,
}
unsafe fn context(drone: usize) -> Option<Context> {
    let state = word(drone, 0x910)? & 255;
    let role = word(drone, 0x4c)? >> 8 & 255;
    if word(drone, 0)? != 0x02a88f54
        || role == 2
        || !matches!(state, 1 | 2)
        || word(drone, 0x4b0)? & 0x80000000 != 0
        || word(drone, 0x5dc)? as i32 <= 0
    {
        return None;
    }
    let owner = word(drone, 0x8c)? as usize;
    if word(owner, 0)? != 0x02ad2abc {
        return None;
    }
    let source = word(drone, 0x2c0)? as usize;
    let id = [word(source, 0x70)?, word(source, 0x74)?];
    let data = word(drone, 0xbe8)? as usize;
    let count = word(drone, 0xbec)? as usize;
    let capacity = word(drone, 0xbf0)? as usize;
    if id == [0, 0] || count > 64 || capacity < count || capacity > 256 {
        return None;
    }
    if count != 0
        && (!readable(data, count * 8) || !writable(data, count * 8) || !writable(drone + 0xbec, 4))
    {
        return None;
    }
    Some(Context {
        owner,
        source,
        id,
        data,
        count,
        capacity,
        state,
        role,
    })
}

// true: continue the original scan; false: a native callback invalidated this
// scan's identity/state, so stop rather than call the engine on stale context.
pub(super) unsafe fn prune(
    drone: usize,
    mut cancel: impl FnMut(usize, u32, u32, usize) -> bool,
) -> bool {
    let Some(mut ctx) = context(drone) else {
        return true;
    };
    let mut index = 0;
    while index < ctx.count {
        let entry = ctx.data + index * 8;
        let Some(pawn) = word(entry, 0).map(|p| p as usize) else {
            return false;
        };
        let Some(timer) = word(entry, 4) else {
            return false;
        };
        if !dead_spy_target(pawn) {
            index += 1;
            continue;
        }
        if !cancel(pawn, ctx.id[0], ctx.id[1], ctx.owner) {
            return true;
        }
        // Cancellation can invoke native callbacks. Never keep an old array,
        // owner, source identity or drone life state across a reentrant reset.
        if context(drone) != Some(ctx)
            || word(entry, 0) != Some(pawn as u32)
            || word(entry, 4) != Some(timer)
            || !dead_spy_target(pawn)
        {
            return false;
        }
        let last = ctx.data + (ctx.count - 1) * 8;
        let (Some(a), Some(b)) = (word(last, 0), word(last, 4)) else {
            return false;
        };
        (entry as *mut u32).write_unaligned(a);
        ((entry + 4) as *mut u32).write_unaligned(b);
        ctx.count -= 1;
        ((drone + 0xbec) as *mut u32).write_unaligned(ctx.count as u32);
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Scene {
        body: Vec<u32>,
        owner: Vec<u32>,
        source: Vec<u32>,
        entries: Vec<u32>,
        dead: Vec<u32>,
        live: Vec<u32>,
    }
    impl Scene {
        fn new() -> Self {
            let mut s = Self {
                body: vec![0; 0xc00 / 4],
                owner: vec![0; 4],
                source: vec![0; 0x80 / 4],
                entries: vec![0; 4],
                dead: vec![0; 0x80 / 4],
                live: vec![0; 0x80 / 4],
            };
            s.body[0] = 0x02a88f54;
            s.body[0x910 / 4] = 1;
            s.body[0x5dc / 4] = 600;
            s.owner[0] = 0x02ad2abc;
            s.source[0x70 / 4] = 11;
            s.source[0x74 / 4] = 12;
            s.body[0x8c / 4] = s.owner.as_ptr() as u32;
            s.body[0x2c0 / 4] = s.source.as_ptr() as u32;
            s.body[0xbe8 / 4] = s.entries.as_ptr() as u32;
            s.body[0xbec / 4] = 2;
            s.body[0xbf0 / 4] = 2;
            s.dead[0] = 0x02ad3234;
            s.dead[0x60 / 4] = 0x800;
            s.live[0] = 0x02ad3234;
            s.entries.copy_from_slice(&[
                s.dead.as_ptr() as u32,
                0,
                s.live.as_ptr() as u32,
                2.0f32.to_bits(),
            ]);
            s
        }
    }
    #[test]
    fn only_corpse_is_cancelled_and_live_pending_entry_is_preserved() {
        let mut s = Scene::new();
        let p = s.body.as_mut_ptr() as usize;
        let old = s.entries.clone();
        let mut calls = 0;
        unsafe {
            assert_eq!(
                prune(p, |pawn, lo, hi, owner| {
                    calls += 1;
                    assert_eq!(pawn, s.dead.as_ptr() as usize);
                    assert_eq!((lo, hi), (11, 12));
                    assert_eq!(owner, s.owner.as_ptr() as usize);
                    true
                }),
                true
            );
        }
        assert_eq!(calls, 1);
        assert_eq!(s.body[0xbec / 4], 1);
        assert_eq!(s.entries[..2], old[2..]);
        unsafe {
            assert_eq!(
                prune(p, |_, _, _, _| panic!("duplicate cancellation")),
                true
            );
        }
    }
    #[test]
    fn uncertain_owner_role_source_or_array_is_not_modified() {
        for fault in 0..6 {
            let mut s = Scene::new();
            match fault {
                0 => s.body[0x4c / 4] = 2 << 8,
                1 => s.owner[0] = 0,
                2 => {
                    s.source[0x70 / 4] = 0;
                    s.source[0x74 / 4] = 0;
                }
                3 => s.body[0xbec / 4] = 65,
                4 => s.body[0xbf0 / 4] = 1,
                _ => s.body[0x910 / 4] = 3,
            }
            let before = s.entries.clone();
            unsafe {
                assert_eq!(
                    prune(s.body.as_ptr() as usize, |_, _, _, _| panic!(
                        "invalid context"
                    )),
                    true
                );
            }
            assert_eq!(s.entries, before);
        }
    }
    #[test]
    fn cancellation_failure_or_reentrant_identity_change_cannot_write_stale_array() {
        for change in 0..9 {
            let mut s = Scene::new();
            let before = s.entries.clone();
            let p = s.body.as_mut_ptr() as usize;
            let result = unsafe {
                prune(p, |_, _, _, _| {
                    match change {
                        0 => return false,
                        1 => s.body[0xbe8 / 4] = 0,
                        2 => s.body[0xbec / 4] = 0,
                        3 => s.source[0x70 / 4] = 99,
                        4 => s.dead[0x60 / 4] = 0,
                        5 => s.body[0x8c / 4] = 0,
                        6 => s.body[0x910 / 4] = 3,
                        7 => s.body[0x5dc / 4] = 0,
                        _ => s.body[0x4b0 / 4] = 0x80000000,
                    }
                    true
                })
            };
            assert_eq!(result, change == 0);
            assert_eq!(s.entries, before);
        }
    }
}
