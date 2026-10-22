//! VSS BasicScope lifetime only. Do not opt the Spy into SC-IS FP/zoom adapters.
pub(super) fn local_weapon(
    pawn: usize,
    read: impl Fn(usize, usize) -> Option<u32>,
    named: impl Fn(u32) -> bool,
) -> bool {
    let check = || -> Option<bool> {
        let info = read(0x339e5a0, 0)? as usize;
        let world = read(pawn, 0x144)? as usize;
        let pc = read(pawn, 0x938)? as usize;
        let inventory = read(pawn, 0x1410)? as usize;
        let weapon = read(pawn, 0x4b8)? as usize;
        Some(world >= 0x10000
            && read(info, 0x144)? as usize == world
            && read(info, 0x330)? & 0x10 != 0
            && read(pawn, 0)? == 0x02ad3234
            && read(pawn, 0x4c)? & 0xff00 != 0x200
            && read(pc, 0)? == 0x02ac53ec
            && read(pc, 0x60)? & 0x40000 != 0
            && read(pc, 0x520)? as usize == pawn
            && read(pc, 0x144)? as usize == world
            && read(inventory, 0x50)? as usize == weapon
            && weapon >= 0x10000
            && named(read(weapon, 0x740)?)
            && read(pawn, 0x148)? as usize >= 0x10000)
    };
    check() == Some(true)
}

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn vss_scope_ownership_fails_closed_for_every_missing_link() {
        let mut m=std::collections::HashMap::from([
            ((0x339e5a0,0),0x10000),((0x10000,0x144),0x20000),((0x10000,0x330),0x10),
            ((0x30000,0),0x02ad3234),((0x30000,0x144),0x20000),((0x30000,0x148),0x70000),
            ((0x30000,0x4c),0),((0x30000,0x938),0x40000),((0x30000,0x1410),0x50000),((0x30000,0x4b8),0x60000),
            ((0x40000,0),0x02ac53ec),((0x40000,0x60),0x40000),((0x40000,0x520),0x30000),
            ((0x40000,0x144),0x20000),((0x50000,0x50),0x60000),((0x60000,0x740),124),
        ]);
        let check=|m:&std::collections::HashMap<_,_>|local_weapon(0x30000,|p,o|m.get(&(p,o)).copied(),|n|n==124);
        assert!(check(&m));
        for key in m.keys().copied().collect::<Vec<_>>() {
            let value=m.remove(&key).unwrap();assert!(!check(&m),"missing {key:?}");m.insert(key,value);
        }
        for (key,value) in [((0x10000,0x330),0),((0x30000,0),0x02ad2abc),((0x30000,0x4c),0x200),
            ((0x40000,0x60),0),((0x40000,0x520),0x31000),((0x40000,0x144),0x21000),
            ((0x50000,0x50),0x61000),((0x60000,0x740),123),((0x30000,0x148),0)] {
            let old=m.insert(key,value).unwrap();assert!(!check(&m),"changed {key:?}");m.insert(key,old);
        }
    }
}
