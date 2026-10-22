//! Passive VSS view witness reader. No hooks, keys, timers, logs or game writes.
//! The coordinator owns manual arming, five-second/eight-view limits and tokens.
//! Inputs are current native callback arguments, never F8 diagnostic caches.
//! CPU observations do not prove draw order, depth/stencil or displayed pixels.

const MIN: usize = 0x10000;
const INFO_SLOT: usize = 0x339e5a0;
const MAX_WORDS: usize = 32768;
const MAX_SCAN: u32 = 4096; // Diagnostic policy, not an engine array capacity.
const GUARD_RESERVE: usize = 256; // Leave room for the closing owner/route check.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Reject {
    Address, Budget, Unreadable, LocalPlayer, Owner, Scope, Route,
    UnsupportedMode, Unstable, Shape, ScanLimit, ForeignMaterial, Material,
}

pub(super) struct Reader<F> { read: F, remaining: usize, used: usize }
impl<F: FnMut(usize, usize) -> Option<u32>> Reader<F> {
    pub(super) fn new(read: F, limit: usize) -> Self {
        Self { read, remaining: limit.min(MAX_WORDS), used: 0 }
    }
    pub(super) fn remaining(&self) -> usize { self.remaining }
    pub(super) fn reads_used(&self) -> usize { self.used }
    fn word(&mut self, p: usize, o: usize) -> Result<u32, Reject> {
        let a = p.checked_add(o).ok_or(Reject::Address)?;
        if p < MIN || a.checked_add(3).filter(|v| *v <= u32::MAX as usize).is_none() {
            return Err(Reject::Address);
        }
        if self.remaining == 0 { return Err(Reject::Budget); }
        self.remaining -= 1; self.used += 1;
        (self.read)(p, o).ok_or(Reject::Unreadable)
    }
    fn ptr(&mut self, p: usize, o: usize) -> Result<usize, Reject> {
        let v = self.word(p, o)? as usize;
        if v < MIN { Err(Reject::Address) } else { Ok(v) }
    }
}
pub(super) type NativeReader = Reader<fn(usize, usize) -> Option<u32>>;
impl NativeReader {
    pub(super) fn native(limit: usize) -> Self { Self::new(native_word, limit) }
}
#[link(name = "kernel32")]
extern "system" {
    fn GetCurrentProcess() -> *mut core::ffi::c_void;
    fn ReadProcessMemory(process: *mut core::ffi::c_void, source: *const core::ffi::c_void,
        buffer: *mut core::ffi::c_void, size: usize, count: *mut usize) -> i32;
}
pub(super) fn native_word(p: usize, o: usize) -> Option<u32> {
    let address = p.checked_add(o)?;
    let (mut value, mut count) = (0u32, 0usize);
    let ok = unsafe { ReadProcessMemory(GetCurrentProcess(), address as *const _,
        &mut value as *mut _ as *mut _, 4, &mut count) };
    (ok != 0 && count == 4).then_some(value)
}

// Pointer-bearing types intentionally have neither Debug nor public fields.
// The coordinator may compare them, but must assign run-local tokens for logs.
#[derive(Clone, Copy, PartialEq, Eq)]
struct Owner {
    info: usize, world: usize, world_info: usize, pc: usize, pc_generation: u32,
    pawn: usize, pawn_generation: u32, level: usize, inventory: usize,
    weapon: usize, weapon_generation: u32, weapon_name: u32,
    scope: usize, scope_generation: u32, mesh: usize, node: usize,
}
#[derive(Clone, Copy, PartialEq, Eq)]
struct Route {
    pass: usize, render: usize, view: usize, scene: usize, root: usize,
    table: usize, shader: usize, mode: u32, view_index: u32,
}
#[derive(Clone, Copy, PartialEq, Eq)]
pub(super) struct Context { owner: Owner, route: Route }
impl Context {
    pub(super) fn same_owner(&self, other: &Self) -> bool { self.owner == other.owner }
    pub(super) fn mode(&self) -> u32 { self.route.mode }
    pub(super) fn view_index(&self) -> u32 { self.route.view_index }
    pub(super) fn matches_material(&self, root: usize, render: usize, node: usize) -> bool {
        self.route.root == root && self.route.render == render && self.owner.node == node
    }
}

fn owner<F: FnMut(usize, usize) -> Option<u32>>(
    r: &mut Reader<F>, named: &impl Fn(u32) -> bool,
) -> Result<Owner, Reject> {
    let info = r.ptr(INFO_SLOT, 0)?;
    let world = r.ptr(info, 0x144)?;
    if r.word(info, 0x330)? & 0x10 == 0 { return Err(Reject::Owner); }
    // Same retail 5863D0 -> 8B6970 -> 8C4ED0 selection as local_player.rs.
    let world_info = r.ptr(world, 0x6d0)?;
    let array = r.ptr(world_info, 0x36c)?;
    let count = r.word(world_info, 0x370)?;
    let cap = r.word(world_info, 0x374)?;
    if count == 0 || count > 32 || cap < count || cap > 128 { return Err(Reject::LocalPlayer); }
    let mut local = None;
    for i in 0..count as usize {
        let pc = r.word(array, i * 4)? as usize;
        if pc == 0 { continue; }
        if r.word(pc, 0x60)? & 0x40000 == 0 { continue; }
        if (r.word(pc, 0x4c)? >> 8) & 0xff == 2 { continue; }
        if r.ptr(pc, 0x144)? != world || local.is_some() { return Err(Reject::LocalPlayer); }
        local = Some(pc);
    }
    if r.ptr(world_info, 0x36c)? != array || r.word(world_info, 0x370)? != count
        || r.word(world_info, 0x374)? != cap { return Err(Reject::Unstable); }
    let pc = local.ok_or(Reject::LocalPlayer)?;
    let pawn = r.ptr(pc, 0x520)?;
    if r.word(pc, 0)? != 0x02ac53ec || r.word(pawn, 0)? != 0x02ad3234
        || r.word(pawn, 0x4c)? & 0xff00 == 0x200
        || r.ptr(pawn, 0x144)? != world || r.ptr(pawn, 0x938)? != pc {
        return Err(Reject::Owner);
    }
    let level = r.ptr(pawn, 0x148)?;
    let inventory = r.ptr(pawn, 0x1410)?;
    let weapon = r.ptr(pawn, 0x4b8)?;
    let weapon_name = r.word(weapon, 0x740)?;
    if r.ptr(inventory, 0x50)? != weapon || !named(weapon_name) { return Err(Reject::Owner); }
    let scope = r.ptr(pawn, 0x1508)?;
    if r.word(pawn, 0x948)? & 0x400000 == 0 || r.word(scope, 0)? != 0x02ab528c
        || r.ptr(scope, 0x144)? != world { return Err(Reject::Scope); }
    let mesh = r.ptr(scope, 0x1f4)?;
    let node = r.ptr(scope, 0x30c)?;
    Ok(Owner { info, world, world_info, pc, pc_generation: r.word(pc, 0x20)?,
        pawn, pawn_generation: r.word(pawn, 0x20)?, level, inventory, weapon,
        weapon_generation: r.word(weapon, 0x20)?, weapon_name, scope,
        scope_generation: r.word(scope, 0x20)?, mesh, node })
}
fn route<F: FnMut(usize, usize) -> Option<u32>>(r: &mut Reader<F>, pass: usize) -> Result<Route, Reject> {
    let render = r.ptr(pass, 0x14)?;
    if r.ptr(render, 0x236c)? != pass { return Err(Reject::Route); }
    let view = r.ptr(render, 0x20d0)?;
    let mode = r.word(view, 0x340)?;
    if mode != 0 && mode != 9 { return Err(Reject::UnsupportedMode); }
    let scene = r.ptr(render, 0x1c)?;
    let view_index = r.word(render, 0x2208)?;
    let root = r.ptr(pass, 0)?;
    let table = r.ptr(pass, 8)?;
    let shader = r.word(table, mode as usize * 4)? as usize;
    // EMF's exact class/root relation is verified. Mode zero is only a route
    // observation; do not invent the normal shader's vtable or material class.
    if mode == 9 && (r.word(shader, 0)? != 0x02c0965c || r.ptr(shader, 4)? != root
        || r.word(root, 0)? != 0x02c06404) { return Err(Reject::Route); }
    Ok(Route { pass, render, view, scene, root, table, shader, mode, view_index })
}
pub(super) fn bind<F: FnMut(usize, usize) -> Option<u32>>(
    r: &mut Reader<F>, pass: usize, is_vss: impl Fn(u32) -> bool,
) -> Result<Context, Reject> {
    let first = Context { owner: owner(r, &is_vss)?, route: route(r, pass)? };
    let second = Context { owner: owner(r, &is_vss)?, route: route(r, pass)? };
    if first != second { return Err(Reject::Unstable); }
    Ok(first)
}
fn revalidate<F: FnMut(usize, usize) -> Option<u32>>(
    r: &mut Reader<F>, context: &Context,
) -> Result<(), Reject> {
    let current = Context { owner: owner(r, &|n| n == context.owner.weapon_name)?,
        route: route(r, context.route.pass)? };
    if current == *context { Ok(()) } else { Err(Reject::Unstable) }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Membership { Present, Absent, Unknown(Reject) }
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct ArraySample { pub len: Option<u32>, pub membership: Membership }
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct NodeSnapshot {
    pub flags_lo: u32, pub flags_hi: u32, pub category: u8,
    pub resource_key: Option<u32>, pub sections: Option<u32>,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct QueueSnapshot {
    pub node: NodeSnapshot, pub primary: ArraySample, pub secondary: ArraySample,
    /// Native pass header offsets, in order: 18, 24, 9C, CC, D8.
    pub queues: [ArraySample; 5],
}
#[derive(Clone, Copy, PartialEq, Eq)]
struct Header { data: usize, len: u32, cap: u32 }
fn header<F: FnMut(usize, usize) -> Option<u32>>(r: &mut Reader<F>, h: usize) -> Result<Header, Reject> {
    let data = r.word(h, 0)? as usize;
    let len = r.word(h, 4)?;
    let cap = r.word(h, 8)?;
    if len > cap || cap > 65536 || (len != 0 && data < MIN) { return Err(Reject::Shape); }
    Ok(Header { data, len, cap })
}
fn membership<F: FnMut(usize, usize) -> Option<u32>>(
    r: &mut Reader<F>, h: usize, stride: usize, node: usize,
) -> ArraySample {
    let mut len = None;
    let scan = || -> Result<bool, Reject> {
        if r.remaining() < GUARD_RESERVE + 6 { return Err(Reject::Budget); }
        let before = header(r, h)?; len = Some(before.len);
        if before.len > MAX_SCAN { return Err(Reject::ScanLimit); }
        if before.len as usize > r.remaining().saturating_sub(GUARD_RESERVE + 3) {
            return Err(Reject::Budget);
        }
        let mut found = false;
        for i in 0..before.len as usize {
            // Never dereference non-target scene object pointers.
            if r.word(before.data, i * stride)? as usize == node { found = true; }
        }
        if header(r, h)? != before { return Err(Reject::Unstable); }
        Ok(found)
    };
    let membership = match { let mut scan = scan; scan() } {
        Ok(true) => Membership::Present, Ok(false) => Membership::Absent,
        Err(reason) => Membership::Unknown(reason),
    };
    ArraySample { len, membership }
}
fn array_at<F: FnMut(usize, usize) -> Option<u32>>(
    r: &mut Reader<F>, base: usize, offset: usize, stride: usize, node: usize,
) -> ArraySample {
    match base.checked_add(offset).filter(|p| *p <= u32::MAX as usize) {
        Some(header) => membership(r, header, stride, node),
        None => ArraySample { len: None, membership: Membership::Unknown(Reject::Address) },
    }
}
fn node_snapshot<F: FnMut(usize, usize) -> Option<u32>>(
    r: &mut Reader<F>, node: usize,
) -> Result<NodeSnapshot, Reject> {
    let flags_lo = r.word(node, 0x34)?;
    let flags_hi = r.word(node, 0x38)?;
    let category = (r.word(node, 0x210)? >> 24) as u8;
    let resource = r.word(node, 0xf8)? as usize;
    let (resource_key, sections) = if resource < MIN { (None, None) } else {
        (r.word(resource, 0x14).ok(), r.word(resource, 0x64).ok())
    };
    if r.word(node, 0xf8)? as usize != resource { return Err(Reject::Unstable); }
    Ok(NodeSnapshot { flags_lo, flags_hi, category, resource_key, sections })
}
pub(super) fn queues<F: FnMut(usize, usize) -> Option<u32>>(
    r: &mut Reader<F>, context: &Context,
) -> Result<QueueSnapshot, Reject> {
    revalidate(r, context)?;
    let node = node_snapshot(r, context.owner.node)?;
    let route = context.route;
    // Native 1755DF3/17560E9 choose per-view arrays. Four is a conservative
    // diagnostic index limit, NOT a claim about all engine view indices.
    // Unsupported indices are Unknown, never read the all-views arrays instead.
    let input = if route.view_index == u32::MAX { Some((0x70, 0xe8)) }
        else if route.view_index < 4 { Some((0xf4 + route.view_index as usize * 12,
            0x124 + route.view_index as usize * 12)) } else { None };
    let unknown = ArraySample { len: None, membership: Membership::Unknown(Reject::Shape) };
    let (primary, secondary) = if let Some((p, s)) = input {
        (array_at(r, route.scene, p, 4, context.owner.node),
            array_at(r, route.scene, s, 8, context.owner.node))
    } else { (unknown, unknown) };
    let queues = [0x18, 0x24, 0x9c, 0xcc, 0xd8].map(|o|
        array_at(r, route.pass, o, 4, context.owner.node));
    if node_snapshot(r, context.owner.node)? != node { return Err(Reject::Unstable); }
    revalidate(r, context)?;
    Ok(QueueSnapshot { node, primary, secondary, queues })
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct SelectedMaterial {
    pub key: u32, pub flags: u32, pub through_walls: u8,
    pub category_min: u8, pub category_max: u8, pub is_head: bool,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct MaterialSnapshot {
    pub stage: u32, pub category: u8, pub surface_category: Option<u8>,
    pub returned_zero: bool, pub selected: Option<SelectedMaterial>,
}
pub(super) fn after_material<F: FnMut(usize, usize) -> Option<u32>>(
    r: &mut Reader<F>, context: &Context, root: usize, render: usize,
    object: usize, surface: usize, returned: usize,
) -> Result<MaterialSnapshot, Reject> {
    // Foreign draws must not incur reads or consume a diagnostic read budget.
    if !context.matches_material(root, render, object) { return Err(Reject::ForeignMaterial); }
    revalidate(r, context)?;
    let stage = r.word(render, 0x233c)?;
    let category = (r.word(object, 0x210)? >> 24) as u8;
    let surface_category = if category == 255 {
        Some(((r.word(surface, 0x3c)? >> 16) & 0x7f) as u8)
    } else { None };
    // 16E2A10 returns selected ESI only after admission. A null return cannot
    // distinguish a null selector from stage rejection; DO NOT call 16E2000
    // again or derive the selected family by walking root+160.
    let selected = if returned == 0 { None } else {
        if r.word(returned, 0)? != 0x02c06404 { return Err(Reject::Material); }
        let key = r.word(returned, 0x14)?;
        let flags = r.word(returned, 0x18c)?;
        Some(SelectedMaterial { key, flags, through_walls: flags as u8,
            category_min: (flags >> 8) as u8, category_max: (flags >> 16) as u8,
            is_head: r.word(root, 0x160)? as usize == returned })
    };
    if r.word(render, 0x233c)? != stage || (r.word(object, 0x210)? >> 24) as u8 != category {
        return Err(Reject::Unstable);
    }
    if let Some(observed) = selected {
        if r.word(returned, 0)? != 0x02c06404 || r.word(returned, 0x14)? != observed.key
            || r.word(returned, 0x18c)? != observed.flags
            || (r.word(root, 0x160)? as usize == returned) != observed.is_head {
            return Err(Reject::Unstable);
        }
    }
    revalidate(r, context)?;
    Ok(MaterialSnapshot { stage, category, surface_category, returned_zero: returned == 0, selected })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;
    type Map = HashMap<usize, u32>;
    const INFO: usize = 0x10000;
    const WORLD: usize = 0x20000;
    const WI: usize = 0x30000;
    const PCS: usize = 0x40000;
    const PC: usize = 0x50000;
    const PAWN: usize = 0x60000;
    const INVENTORY: usize = 0x70000;
    const WEAPON: usize = 0x80000;
    const SCOPE: usize = 0x90000;
    const NODE: usize = 0xa0000;
    const RESOURCE: usize = 0xb0000;
    const PASS: usize = 0xc0000;
    const RENDER: usize = 0xd0000;
    const VIEW: usize = 0xe0000;
    const SCENE: usize = 0xf0000;
    const ROOT: usize = 0x100000;
    const TABLE: usize = 0x110000;
    const SHADER: usize = 0x120000;
    const ARRAY: usize = 0x130000;
    const SELECTED: usize = 0x140000;
    const SURFACE: usize = 0x150000;
    fn set(m: &mut Map, p: usize, o: usize, v: usize) { m.insert(p+o, v as u32); }
    fn array(m: &mut Map, h: usize, p: usize, len: usize, cap: usize) {
        set(m,h,0,p); set(m,h,4,len); set(m,h,8,cap);
    }
    fn fixture() -> Map {
        let mut m=Map::new();
        for (p,o,v) in [
            (INFO_SLOT,0,INFO),(INFO,0x144,WORLD),(INFO,0x330,0x10),
            (WORLD,0x6d0,WI),(WI,0x36c,PCS),(WI,0x370,1),(WI,0x374,1),(PCS,0,PC),
            (PC,0,0x02ac53ec),(PC,0x20,12),(PC,0x60,0x40000),(PC,0x4c,0),
            (PC,0x144,WORLD),(PC,0x520,PAWN),
            (PAWN,0,0x02ad3234),(PAWN,0x20,13),(PAWN,0x4c,0),(PAWN,0x144,WORLD),
            (PAWN,0x938,PC),(PAWN,0x148,WORLD+0x9000),(PAWN,0x1410,INVENTORY),
            (PAWN,0x4b8,WEAPON),(PAWN,0x1508,SCOPE),(PAWN,0x948,0x400000),
            (INVENTORY,0x50,WEAPON),(WEAPON,0x20,14),(WEAPON,0x740,124),
            (SCOPE,0,0x02ab528c),(SCOPE,0x20,15),(SCOPE,0x144,WORLD),
            (SCOPE,0x1f4,RESOURCE+0x1000),(SCOPE,0x30c,NODE),
            (NODE,0x34,0x48004),(NODE,0x38,0x20001040),(NODE,0x210,22<<24),
            (NODE,0xf8,RESOURCE),(RESOURCE,0x14,0x573dd314),(RESOURCE,0x64,2),
            (PASS,0,ROOT),(PASS,8,TABLE),(PASS,0x14,RENDER),
            (RENDER,0x236c,PASS),(RENDER,0x20d0,VIEW),(RENDER,0x1c,SCENE),
            (RENDER,0x2208,u32::MAX as usize),(RENDER,0x233c,7),(VIEW,0x340,9),
            (TABLE,9*4,SHADER),(TABLE,0,SHADER),(SHADER,0,0x02c0965c),(SHADER,4,ROOT),
            (ROOT,0,0x02c06404),(ROOT,0x160,SELECTED),
            (SELECTED,0,0x02c06404),(SELECTED,0x14,0x7a123b12),(SELECTED,0x18c,0xff1001),
            (SURFACE,0x3c,0x81220000),
        ] {set(&mut m,p,o,v);}
        for h in [SCENE+0x70,SCENE+0xe8,PASS+0x18,PASS+0x24,PASS+0x9c,PASS+0xcc,PASS+0xd8] {
            array(&mut m,h,0,0,0);
        }
        m
    }
    fn bound(m: &Map) -> Context {
        bind(&mut Reader::new(|p,o|m.get(&(p+o)).copied(),2000),PASS,|n|n==124).ok().unwrap()
    }
    #[test] fn fresh_local_player_and_exact_scope_chain() {
        let m=fixture(); let ctx=bound(&m);
        assert_eq!(ctx.mode(),9); assert_eq!(ctx.view_index(),u32::MAX);
        assert!(ctx.same_owner(&bound(&m)));
        let mut needed=Vec::new();
        let mut r=Reader::new(|p,o|{needed.push(p+o);m.get(&(p+o)).copied()},2000);
        assert!(bind(&mut r,PASS,|n|n==124).is_ok()); drop(r);
        needed.sort();needed.dedup();
        for a in needed { let mut missing=m.clone();missing.remove(&a);
            assert!(bind(&mut Reader::new(|p,o|missing.get(&(p+o)).copied(),2000),PASS,|n|n==124).is_err(),"missing {a:x}"); }
    }
    #[test] fn reject_wrong_faction_campaign_remote_proxy_and_ownership() {
        for (p,o,v) in [(INFO,0x330,0),(PAWN,0,0x02ad2abc),(PAWN,0x4c,0x200),
            (PC,0x60,0),(PC,0x4c,0x200),(PC,0x144,WORLD+4),(PC,0x520,0),
            (PAWN,0x938,PC+4),(INVENTORY,0x50,WEAPON+4),(WEAPON,0x740,123),
            (PAWN,0x948,0),(SCOPE,0,0),(SCOPE,0x144,WORLD+4),(SCOPE,0x1f4,0),
            (SCOPE,0x30c,0),(WI,0x370,33),(WI,0x374,129),(WI,0x374,0),
            (RENDER,0x236c,PASS+4),(VIEW,0x340,8),(SHADER,4,ROOT+4)] {
            let mut m=fixture();set(&mut m,p,o,v);
            assert!(bind(&mut Reader::new(|p,o|m.get(&(p+o)).copied(),2000),PASS,|n|n==124).is_err(),"{p:x}+{o:x}");
        }
    }
    #[test] fn multiple_locals_are_ambiguous_but_remote_is_not() {
        let mut m=fixture();array(&mut m,WI+0x36c,PCS,2,2);set(&mut m,PCS,4,PC+0x4000);
        set(&mut m,PC+0x4000,0x60,0);assert!(bound(&m).same_owner(&bound(&fixture())));
        set(&mut m,PC+0x4000,0x60,0x40000);set(&mut m,PC+0x4000,0x4c,0);set(&mut m,PC+0x4000,0x144,WORLD);
        assert!(bind(&mut Reader::new(|p,o|m.get(&(p+o)).copied(),2000),PASS,|n|n==124).is_err());
    }
    #[test] fn owner_generation_and_route_changes_invalidate_context() {
        let m=fixture();let ctx=bound(&m);
        for (p,o,v) in [(PC,0x20,120),(PAWN,0x20,130),(WEAPON,0x20,140),(SCOPE,0x20,150),
            (PAWN,0x148,WORLD+0xa000),(SCOPE,0x30c,NODE+4),(RENDER,0x2208,0)] {
            let mut changed=m.clone();set(&mut changed,p,o,v);
            assert!(queues(&mut Reader::new(|p,o|changed.get(&(p+o)).copied(),2000),&ctx).is_err());
        }
    }
    #[test] fn bind_cannot_mix_two_owner_generations() {
        let m=fixture();let mut hits=0;
        let result=bind(&mut Reader::new(|p,o|{let value=m.get(&(p+o)).copied();
            if p==PAWN&&o==0x20 {hits+=1;value.map(|v|v+hits)} else {value}},2000),PASS,|n|n==124);
        assert!(matches!(result,Err(Reject::Unstable)));
    }
    #[test] fn read_budget_is_hard_and_invalid_addresses_never_call_reader() {
        let mut calls=0;
        {let mut r=Reader::new(|_,_|{calls+=1;Some(1)},1);
        assert_eq!(r.word(0,0),Err(Reject::Address));assert_eq!(r.word(u32::MAX as usize,1),Err(Reject::Address));
        assert_eq!(r.word(INFO,0),Ok(1));assert_eq!(r.word(INFO,0),Err(Reject::Budget));
        assert_eq!(r.reads_used(),1);assert_eq!(r.remaining(),0);}
        assert_eq!(calls,1);
        assert!(matches!(bind(&mut Reader::new(|_,_|Some(0),0),PASS,|_|true),Err(Reject::Budget)));
    }
    #[test] fn native_reader_is_safe_and_budgeted() {
        let value=0xa123bcdeu32;let mut r=NativeReader::native(2);
        assert_eq!(r.word(&value as *const _ as usize,0),Ok(value));
        assert_eq!(r.word(0xfffffffc,0),Err(Reject::Unreadable));assert_eq!(r.remaining(),0);
    }
    #[test] fn normal_mode_does_not_claim_an_emf_shader_class() {
        let mut m=fixture();set(&mut m,VIEW,0x340,0);set(&mut m,SHADER,0,0x1111);
        let ctx=bound(&m);assert_eq!(ctx.mode(),0);assert!(ctx.same_owner(&bound(&fixture())));
        // Retail outer vision view returns before classification for mode zero.
        // A null shader table slot is normal, not a failed VSS material load.
        set(&mut m,TABLE,0,0);assert_eq!(bound(&m).mode(),0);
    }
    #[test] fn queues_distinguish_present_absent_and_unreadable() {
        let mut m=fixture();array(&mut m,SCENE+0x70,ARRAY,1,1);set(&mut m,ARRAY,0,NODE);
        array(&mut m,PASS+0x24,ARRAY+0x10,1,1); // data deliberately unreadable
        let ctx=bound(&m);let q=queues(&mut Reader::new(|p,o|m.get(&(p+o)).copied(),4000),&ctx).unwrap();
        assert_eq!(q.primary.membership,Membership::Present);
        assert_eq!(q.secondary.membership,Membership::Absent);
        assert_eq!(q.queues[0].membership,Membership::Absent);
        assert_eq!(q.queues[1].membership,Membership::Unknown(Reject::Unreadable));
        assert_eq!(q.node.category,22);assert_eq!(q.node.resource_key,Some(0x573dd314));
    }
    #[test] fn indexed_primary_and_eight_byte_secondary_use_actual_native_arrays() {
        let mut m=fixture();set(&mut m,RENDER,0x2208,1);
        array(&mut m,SCENE+0xf4+12,ARRAY,1,1);set(&mut m,ARRAY,0,NODE);
        array(&mut m,SCENE+0x124+12,ARRAY+0x20,2,2);
        set(&mut m,ARRAY+0x20,0,NODE+4);set(&mut m,ARRAY+0x20,4,0);set(&mut m,ARRAY+0x20,8,NODE);
        let ctx=bound(&m);let q=queues(&mut Reader::new(|p,o|m.get(&(p+o)).copied(),4000),&ctx).unwrap();
        assert_eq!(q.primary.membership,Membership::Present);assert_eq!(q.secondary.membership,Membership::Present);
    }
    #[test] fn unsupported_view_index_does_not_fall_back_to_all_views() {
        let mut m=fixture();set(&mut m,RENDER,0x2208,77);
        let ctx=bound(&m);let q=queues(&mut Reader::new(|p,o|m.get(&(p+o)).copied(),4000),&ctx).unwrap();
        assert_eq!(q.primary.membership,Membership::Unknown(Reject::Shape));
    }
    #[test] fn truncated_malformed_and_mutating_arrays_never_become_absent() {
        for (len,cap,reason) in [(4097,4097,Reject::ScanLimit),(2,1,Reject::Shape),(1,65537,Reject::Shape)] {
            let mut m=fixture();array(&mut m,SCENE+0x70,ARRAY,len,cap);
            let ctx=bound(&m);let q=queues(&mut Reader::new(|p,o|m.get(&(p+o)).copied(),4000),&ctx).unwrap();
            assert_eq!(q.primary.membership,Membership::Unknown(reason));
        }
        let mut m=fixture();array(&mut m,SCENE+0x70,ARRAY,1,1);set(&mut m,ARRAY,0,NODE);
        let ctx=bound(&m);let mut reads=0;
        let q=queues(&mut Reader::new(|p,o|{if p==SCENE+0x70&&o==0{reads+=1; if reads==2{return Some((ARRAY+4) as u32);}}
            m.get(&(p+o)).copied()},4000),&ctx).unwrap();
        assert_eq!(q.primary.membership,Membership::Unknown(Reject::Unstable));
    }
    #[test] fn oversized_read_budget_keeps_unknown_and_still_checks_current_owner() {
        let mut m=fixture();array(&mut m,SCENE+0x70,ARRAY,4096,4096);
        let ctx=bound(&m);
        let mut r=Reader::new(|p,o|m.get(&(p+o)).copied(),600);
        let q=queues(&mut r,&ctx).unwrap();
        assert_eq!(q.primary.membership,Membership::Unknown(Reject::Budget));
        assert_eq!(q.primary.len,Some(4096));assert!(r.reads_used()<600);
        assert_eq!(q.queues[0].membership,Membership::Absent);
    }
    #[test] fn foreign_material_rejects_without_reading_anything() {
        let ctx=bound(&fixture());let mut r=Reader::new(|_,_|panic!("foreign read"),1000);
        assert_eq!(after_material(&mut r,&ctx,ROOT,RENDER,NODE+4,SURFACE,SELECTED),Err(Reject::ForeignMaterial));
        assert_eq!(r.reads_used(),0);
    }
    #[test] fn native_return_controls_selected_material_without_selector_or_head_guess() {
        let mut m=fixture();let ctx=bound(&m);
        let q=after_material(&mut Reader::new(|p,o|m.get(&(p+o)).copied(),4000),&ctx,ROOT,RENDER,NODE,SURFACE,SELECTED).unwrap();
        assert_eq!(q.stage,7);assert!(!q.returned_zero);let selected=q.selected.unwrap();
        assert_eq!((selected.through_walls,selected.category_min,selected.category_max),(1,16,255));assert!(selected.is_head);
        m.remove(&(ROOT+0x160));m.remove(&SELECTED);
        let q=after_material(&mut Reader::new(|p,o|m.get(&(p+o)).copied(),4000),&ctx,ROOT,RENDER,NODE,0,0).unwrap();
        assert!(q.returned_zero);assert_eq!(q.selected,None);assert_eq!(q.surface_category,None);
    }
    #[test] fn category_255_reads_actual_surface_byte_only_when_needed() {
        let mut m=fixture();set(&mut m,NODE,0x210,255<<24);let ctx=bound(&m);
        let q=after_material(&mut Reader::new(|p,o|m.get(&(p+o)).copied(),4000),&ctx,ROOT,RENDER,NODE,SURFACE,0).unwrap();
        assert_eq!(q.surface_category,Some(0x22));
        assert!(after_material(&mut Reader::new(|p,o|m.get(&(p+o)).copied(),4000),&ctx,ROOT,RENDER,NODE,0,0).is_err());
    }
    #[test] fn material_change_between_reads_is_rejected() {
        let m=fixture();let ctx=bound(&m);let mut calls=0;
        let q=after_material(&mut Reader::new(|p,o|{let v=m.get(&(p+o)).copied();
            if p==SELECTED&&o==0x18c{calls+=1;v.map(|v|v+calls)}else{v}},4000),&ctx,ROOT,RENDER,NODE,SURFACE,SELECTED);
        assert_eq!(q,Err(Reject::Unstable));
    }
}
