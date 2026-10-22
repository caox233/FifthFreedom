//! Scope identity is a pawn generation, not an entire match. Only our local campaign optic skips
//! the native exit delay; native visibility and sound handlers remain responsible.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Owner {
    pub pawn: usize,
    pub generation: u32,
    pub world: usize,
}
pub(super) fn blocks_creation(old: Option<Owner>, current: Owner, owns_slot: bool) -> bool {
    owns_slot && old == Some(current)
}
pub(super) fn exit_delay(ours: bool, enabled: u32, delay: f32) -> f32 {
    if ours && enabled == 0 && delay.is_finite() && delay >= 0.0 { 0.0 } else { delay }
}
// AA6120 and E93DF0 obtain the native local controller from pawn+358,
// whereas possession and our weapon guards use pawn+938. Do not let DA6EF0
// commit scoped=true until the binder and viewport can consume the entry.
// Already-active scopes must always run the native tick, especially on exit.
pub(super) fn defer_entry(
    ours: bool,
    pawn: usize,
    read: impl Fn(usize, usize) -> Option<u32>,
) -> bool {
    if !ours || read(pawn, 0x948).is_none_or(|v| v & 0x400000 != 0) {
        return false;
    }
    let ready = || -> Option<bool> {
        let pc = read(pawn, 0x938)? as usize;
        Some(pc >= 0x10000
            && read(pawn, 0x358)? as usize == pc
            && read(pc, 0x60)? & 0x40000 != 0
            && read(pc, 0x354)? >= 0x10000)
    };
    ready() != Some(true)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn scis_entry_waits_for_native_controller_and_viewport_but_never_blocks_exit() {
        let pawn=0x10000; let pc=0x20000;
        let mut m=std::collections::HashMap::from([
            ((pawn,0x938),pc as u32),((pawn,0x358),pc as u32),
            ((pawn,0x948),0),((pc,0x60),0x40000),((pc,0x354),0x30000),
        ]);
        assert!(!defer_entry(true,pawn,|p,o|m.get(&(p,o)).copied()));
        for key in [(pawn,0x358),(pc,0x60),(pc,0x354)] {
            let old=m.insert(key,0).unwrap();
            assert!(defer_entry(true,pawn,|p,o|m.get(&(p,o)).copied()));
            assert!(!defer_entry(false,pawn,|p,o|m.get(&(p,o)).copied()));
            m.insert((pawn,0x948),0x400000);
            assert!(!defer_entry(true,pawn,|p,o|m.get(&(p,o)).copied()));
            m.insert((pawn,0x948),0);m.insert(key,old);
        }
    }
    #[test]
    fn scis_scope_lifecycle_and_exit_delay_guards() {
        let a=Owner{pawn:1,generation:2,world:3};
        assert!(blocks_creation(Some(a),a,true));
        assert!(!blocks_creation(Some(a),Owner{pawn:4,..a},true));
        assert!(!blocks_creation(Some(a),Owner{generation:5,..a},true));
        assert!(!blocks_creation(Some(a),a,false));
        assert!(!blocks_creation(None,a,false));
        assert_eq!(exit_delay(true,0,0.2),0.0);
        assert_eq!(exit_delay(false,0,0.2),0.2);
        assert_eq!(exit_delay(true,1,0.2),0.2);
        assert_eq!(exit_delay(true,0,-1.0),-1.0);
        assert!(exit_delay(true,0,f32::NAN).is_nan());
    }
}
