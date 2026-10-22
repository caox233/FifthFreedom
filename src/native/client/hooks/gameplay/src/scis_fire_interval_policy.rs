//! SC-IS MP cadence uses the native instance's last accepted-shot timestamp.
//! No input-edge timer, global weapon timestamp, or shared archetype mutation.
pub const INTERVAL_SECONDS: f32 = 1.45;

/// A synchronous firing-invocation witness, never retained across frames.
/// E557C0 writes +198 on its accepted-shot path; E566D0 otherwise still calls
/// BB9F50 after rejection. Do not substitute the outer request for acceptance.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(super) struct ShotCommit {
    logic: usize, weapon: usize, pawn: usize, hands: usize, pc: usize,
    world: usize, level: usize, clock: u32, stamp: u32,
}
impl ShotCommit {
    pub(super) fn capture(
        logic: usize, read: impl Fn(usize,usize)->Option<u32>,
        named: impl FnOnce(u32)->bool,
    )->Option<Self> {
        let world=read(0x339e5a0,0)? as usize;
        if read(world,0x330)?&0x10==0{return None;}
        let weapon=read(logic,0x28)? as usize;
        let props=read(logic,0x2c)? as usize;
        let pawn=read(weapon,0x8c)? as usize;
        let level=read(weapon,0x144)? as usize;
        let pc=read(pawn,0x938)? as usize;
        let hands=read(pawn,0x12ac)? as usize;
        if level<0x10000 || read(world,0x144)? as usize!=level
            || read(weapon,0)?!=0x02a85b7c || read(props,0)?!=0x02a176c4
            || read(weapon,0x748)? as usize!=logic || read(weapon,0x744)? as usize!=props
            || !named(read(weapon,0x740)?) || read(logic,0x1e4)?&0xff!=1
            || read(pawn,0)?!=0x02ad2abc || read(pawn,0x374)?&0xe0000!=0xa0000
            || read(pawn,0x4c)?&0xff00==0x200 || read(pawn,0x144)? as usize!=level
            || read(pawn,0x4b8)? as usize!=weapon
            || read(pc,0)?!=0x02ac53ec || read(pc,0x60)?&0x40000==0
            || read(pc,0x520)? as usize!=pawn || read(pc,0x144)? as usize!=level
            || read(hands,0)?!=0x02a23634 || read(hands,0x310)? as usize!=pawn
            || read(hands,0x318)?&2!=0 {return None;}
        let clock=read(level,0x5a0)?;let stamp=read(logic,0x198)?;
        if !f32::from_bits(clock).is_finite() || !f32::from_bits(stamp).is_finite(){return None;}
        Some(Self{logic,weapon,pawn,hands,pc,world,level,clock,stamp})
    }
    pub(super) fn owns(self,hands:usize,weapon:usize)->bool {
        self.hands==hands && self.weapon==weapon
    }
    pub(super) fn committed(self,read:impl Fn(usize,usize)->Option<u32>,named:impl FnOnce(u32)->bool)->bool {
        let Some(after)=Self::capture(self.logic,read,named)else{return false;};
        after.stamp!=self.stamp && after.stamp==self.clock
            && Self{stamp:self.stamp,..after}==self
    }
}

pub fn readiness(
    original: u8,
    logic: usize,
    read: impl Fn(usize, usize) -> Option<u32>,
    is_scis: impl FnOnce(u32) -> bool,
) -> u8 {
    if original == 0 { return original; }
    let context = (|| {
        let world = read(0x339e5a0, 0)? as usize;
        if read(world, 0x330)? & 0x10 == 0 { return None; }
        let weapon = read(logic, 0x28)? as usize;
        let props = read(logic, 0x2c)? as usize;
        let level = read(weapon, 0x144)? as usize;
        if level == 0 || read(world, 0x144)? as usize != level
            || read(weapon, 0)? != 0x02a85b7c || read(props, 0)? != 0x02a176c4
            || read(weapon, 0x748)? as usize != logic
            || read(weapon, 0x744)? as usize != props
            || !is_scis(read(weapon, 0x740)?) { return None; }
        let now = f32::from_bits(read(level, 0x5a0)?);
        let last = f32::from_bits(read(logic, 0x198)?);
        // A new world or corrupt/unreadable clock must not freeze a weapon.
        if !now.is_finite() || !last.is_finite() || last < 0.0 || now < last { return None; }
        Some(now - last)
    })();
    if context.is_some_and(|elapsed| elapsed < INTERVAL_SECONDS) { 0 } else { original }
}

#[cfg(test)] mod tests {
    use super::*;
    use std::collections::HashMap;
    fn fixture() -> HashMap<(usize,usize),u32> {
        HashMap::from([
            ((0x339e5a0,0),0x10000),((0x10000,0x330),0x10),((0x10000,0x144),0x50000),
            ((0x30000,0x28),0x20000),((0x30000,0x2c),0x40000),
            ((0x20000,0),0x02a85b7c),((0x20000,0x144),0x50000),
            ((0x20000,0x744),0x40000),((0x20000,0x748),0x30000),((0x20000,0x740),123),
            ((0x40000,0),0x02a176c4),((0x50000,0x5a0),100.5f32.to_bits()),
            ((0x30000,0x198),100f32.to_bits()),
        ])
    }
    #[test] fn scis_interval_uses_native_accepted_time_not_click_time() {
        let mut data=fixture();
        for (t,allow) in [(100.0,0),(100.1,0),(101.44,0),(101.46,1),(102.0,1)] {
            data.insert((0x50000,0x5a0),f32::to_bits(t));
            assert_eq!(readiness(1,0x30000,|p,o|data.get(&(p,o)).copied(),|n|n==123),allow);
        }
        assert_eq!(data[&(0x30000,0x198)],100f32.to_bits());
        assert_eq!(readiness(0,0x30000,|_,_|panic!("native rejection must win"),|_|true),0);
    }
    #[test] fn scis_interval_missing_context_campaign_other_weapon_preserve_native() {
        let data=fixture();
        for key in data.keys() {
            let mut bad=data.clone();bad.remove(key);
            assert_eq!(readiness(1,0x30000,|p,o|bad.get(&(p,o)).copied(),|n|n==123),1,"missing {key:?}");
        }
        for (key,value) in [((0x10000,0x330),0),((0x20000,0x740),124),((0x20000,0x748),0x60000),((0x10000,0x144),0x60000)] {
            let mut bad=data.clone();bad.insert(key,value);
            assert_eq!(readiness(1,0x30000,|p,o|bad.get(&(p,o)).copied(),|n|n==123),1);
        }
    }
    #[test] fn scis_interval_does_not_keep_history_across_world_or_weapon_lifetimes() {
        let mut data=fixture();
        for (now,last) in [(10.0,0.0),(0.5,100.0),(f32::NAN,100.0),(100.0,f32::INFINITY)] {
            data.insert((0x50000,0x5a0),now.to_bits());data.insert((0x30000,0x198),last.to_bits());
            assert_eq!(readiness(1,0x30000,|p,o|data.get(&(p,o)).copied(),|n|n==123),1);
        }
    }
    fn commit_fixture()->HashMap<(usize,usize),u32> {
        let mut m=fixture();
        for (k,v) in [
            ((0x20000,0x8c),0x60000),((0x30000,0x1e4),1),
            ((0x60000,0),0x2ad2abc),((0x60000,0x374),0xa0000),((0x60000,0x4c),0),
            ((0x60000,0x144),0x50000),((0x60000,0x4b8),0x20000),
            ((0x60000,0x938),0x70000),((0x60000,0x12ac),0x80000),
            ((0x70000,0),0x2ac53ec),((0x70000,0x60),0x40000),
            ((0x70000,0x520),0x60000),((0x70000,0x144),0x50000),
            ((0x80000,0),0x2a23634),((0x80000,0x310),0x60000),((0x80000,0x318),0),
        ]{m.insert(k,v);}
        m
    }
    #[test] fn shot_commit_requires_same_invocation_native_timestamp_and_owner() {
        let m=commit_fixture();
        let capture=|m:&HashMap<_,_>|ShotCommit::capture(0x30000,|p,o|m.get(&(p,o)).copied(),|n|n==123);
        let token=capture(&m).unwrap();
        assert!(token.owns(0x80000,0x20000));
        assert!(!token.owns(0x80000,0x20004));
        let committed=|m:&HashMap<_,_>|token.committed(|p,o|m.get(&(p,o)).copied(),|n|n==123);
        assert!(!committed(&m),"outer start is not an accepted shot");
        let mut accepted=m.clone();accepted.insert((0x30000,0x198),m[&(0x50000,0x5a0)]);
        assert!(committed(&accepted));
        for key in m.keys(){
            let mut missing=m.clone();missing.remove(key);assert!(capture(&missing).is_none(),"missing {key:?}");
            let mut missing=accepted.clone();missing.remove(key);assert!(!committed(&missing),"lost {key:?}");
        }
        for (key,value) in [
            ((0x60000,0x4c),0x200),((0x70000,0x60),0),((0x80000,0x318),2),
            ((0x30000,0x1e4),2),((0x20000,0x740),124),((0x10000,0x330),0),
            ((0x60000,0x12ac),0x80004),((0x60000,0x4b8),0x20004),
            ((0x50000,0x5a0),101f32.to_bits()),((0x30000,0x198),100.25f32.to_bits()),
            ((0x30000,0x198),f32::NAN.to_bits()),((0x50000,0x5a0),f32::INFINITY.to_bits()),
        ] {
            let mut changed=accepted.clone();changed.insert(key,value);
            assert!(!committed(&changed),"changed {key:?}");
        }
    }
}
