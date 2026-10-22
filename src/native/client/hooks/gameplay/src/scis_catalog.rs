//! Independent Merc catalog copy; original campaign records are never reclassified.
use crate::equipment_native::{readable, word};
use std::ffi::CString;
pub(super) const RETAIL_NAME: &[u8] = b"Sniper_Proto";
const RETAIL_ID: u32 = 625774215;
pub(super) const COPY_NAME: &[u8] = b"SCBL_Merc_SCIS";
const COPY_ID: u32 = native_net_id(COPY_NAME);
// CB64A0 resolves a saved/displayed NetName through 430B60 before CAD660.
// Retail uses uppercase ASCII, seed 0 and no final XOR, NOT zlib.crc32.
// Its table uses the forward 04c11db7 polynomial, but the accumulator shifts
// right (verified 430B60 + table 031077B8); reflected CRC32 is also incorrect.
// Generate the paired ID from its name so registration and consumers agree.
pub(super) const fn native_net_id(name: &[u8]) -> u32 {
    let mut crc = 0u32;
    let mut i = 0;
    while i < name.len() {
        let b = name[i];
        let upper = if b >= b'a' && b <= b'z' { b - 32 } else { b };
        let mut entry = ((crc ^ upper as u32) & 0xff) << 24;
        let mut bit = 0;
        while bit < 8 {
            entry = (entry << 1) ^ if entry & 0x80000000 != 0 { 0x04c11db7 } else { 0 };
            bit += 1;
        }
        crc = (crc >> 8) ^ entry;
        i += 1;
    }
    crc
}
// Separate catalog IDs, shared original Archetype. Native factory owns all strings and children.
const IDENTITIES: &[(&[u8], &[u8], u32)] = &[
    (RETAIL_NAME, COPY_NAME, COPY_ID),
    (b"Sniper_Proto.Optic_HighPowerScope", b"SCBL_Merc_SCIS.Optic_HighPowerScope", native_net_id(b"SCBL_Merc_SCIS.Optic_HighPowerScope")),
    (b"Sniper_Proto.Internal_MatchGradeTrigger", b"SCBL_Merc_SCIS.Internal_MatchGradeTrigger", native_net_id(b"SCBL_Merc_SCIS.Internal_MatchGradeTrigger")),
    (b"Sniper_Proto.Internal_RecoilBufferAdj", b"SCBL_Merc_SCIS.Internal_RecoilBufferAdj", native_net_id(b"SCBL_Merc_SCIS.Internal_RecoilBufferAdj")),
    (b"Sniper_Proto.Internal_WeightBalance", b"SCBL_Merc_SCIS.Internal_WeightBalance", native_net_id(b"SCBL_Merc_SCIS.Internal_WeightBalance")),
    (b"Sniper_Proto.Rifle_Ammo_MatchGrade", b"SCBL_Merc_SCIS.Rifle_Ammo_MatchGrade", native_net_id(b"SCBL_Merc_SCIS.Rifle_Ammo_MatchGrade")),
    (b"Sniper_Proto.Rifle_Ammo_AP", b"SCBL_Merc_SCIS.Rifle_Ammo_AP", native_net_id(b"SCBL_Merc_SCIS.Rifle_Ammo_AP")),
];
// Shared copy mechanism; only identities/faction differ. Native factories still
// allocate and own every record. Each caller supplies a closed, pinned schema.
pub(super) struct CopySpec {
    pub source: &'static [u8],
    pub source_id: u32,
    pub identities: &'static [(&'static [u8], &'static [u8], u32)],
    pub faction: &'static [u8],
    pub kind: u32,
    pub required: u64,
    pub minimum: usize,
}
const SPEC: CopySpec = CopySpec {
    source: RETAIL_NAME, source_id: RETAIL_ID, identities: IDENTITIES,
    faction: b"Merc", kind: 2, required: 0b1111101, minimum: 6,
};
// All XML keys read by pinned CF2470, CEBA70, CC9D60 and CB6CC0.
const KEYS: &[&[u8]] = &[
    b"Archetype\0", b"SlotType\0", b"Damage\0", b"Range\0", b"Accuracy\0",
    b"Handling\0", b"CartridgeCapacity\0", b"MaxInternalUpgrades\0",
    b"CanHaveMultipleAmmoUpgrades\0", b"Subcategory\0", b"Category\0",
    b"NetID\0", b"NetName\0", b"Type\0", b"IsStandardItem\0",
    b"Cost\0", b"TokenCost\0", b"NameKey\0", b"DescKey\0", b"Purchased\0",
    b"Unlocked\0", b"DlcID\0", b"EarlyAccessID\0", b"UnlocksArchetype\0",
    b"RestrictChest\0", b"LabLevelReq\0", b"MissionReq\0", b"BlackMarketLvl\0",
    b"PrestigeLevelReq\0",
];
type Attr = unsafe extern "thiscall" fn(usize, *const u8) -> *const u8;
type CursorBool = unsafe extern "thiscall" fn(usize) -> u8;
type CursorNext = unsafe extern "thiscall" fn(usize) -> i32;
type CursorParent = unsafe extern "thiscall" fn(usize);
#[derive(Clone)]
struct Node { attrs: Vec<Option<CString>> }
impl Node {
    fn get(&self, key: &[u8]) -> Option<&[u8]> {
        let i = KEYS.iter().position(|k| &k[..k.len()-1] == key)?;
        self.attrs[i].as_ref().map(|v| v.as_bytes())
    }
    fn set(&mut self, key: &[u8], value: &[u8]) {
        let i = KEYS.iter().position(|k| &k[..k.len()-1] == key).expect("fixed attribute");
        self.attrs[i] = Some(CString::new(value).expect("validated attribute"));
    }
}
unsafe fn function(xml: usize, offset: usize) -> Option<usize> {
    let vt = word(xml, 0)? as usize; let f = word(vt, offset)? as usize;
    readable(f, 1).then_some(f)
}
unsafe fn snapshot_node(xml: usize, get: Attr) -> Option<Node> {
    let mut attrs = Vec::with_capacity(KEYS.len());
    for key in KEYS {
        let p = get(xml, key.as_ptr()) as usize;
        attrs.push(if p == 0 { None } else { Some(CString::new(super::cbytes(p, 4096)?).ok()?) });
    }
    Some(Node { attrs })
}
#[cfg(test)]
unsafe fn snapshot(xml: usize) -> Option<Vec<Node>> {
    let get: Attr = std::mem::transmute(function(xml, 0x60)?);
    let first: CursorBool = std::mem::transmute(function(xml, 0xdc)?);
    let next: CursorNext = std::mem::transmute(function(xml, 0x2c)?);
    let parent: CursorParent = std::mem::transmute(function(xml, 0xe0)?);
    let root = snapshot_node(xml, get)?;
    if root.get(b"NetName") != Some(RETAIL_NAME) || root.get(b"Type") != Some(b"Campaign") { return None; }
    if first(xml) == 0 { return None; }
    // Restore the original cursor on every rejection after entering children.
    // No XML attributes or original catalog records are written.
    let result = (|| {
        let mut nodes = vec![root];
        loop {
            if nodes.len() == IDENTITIES.len() { return None; }
            nodes.push(snapshot_node(xml, get)?);
            if next(xml) != 0 { break; }
        }
        Some(nodes)
    })();
    parent(xml); // Retail ignores this method's return; verify the cursor instead.
    if super::xml_attr(xml, b"NetName\0") != Some(RETAIL_NAME) { return None; }
    result
}
fn project_for(nodes: &mut [Node], spec: &CopySpec) -> bool {
    if spec.identities.len()>64 || !(spec.minimum..=spec.identities.len()).contains(&nodes.len()) { return false; }
    let mut seen = 0u64; let mut mapping = Vec::new();
    for (index, node) in nodes.iter().enumerate() {
        let Some(i) = spec.identities.iter().position(|(old, _, _)| node.get(b"NetName") == Some(*old)) else { return false; };
        if (index == 0) != (i == 0) || seen & (1 << i) != 0 || node.get(b"Type") != Some(b"Campaign") { return false; }
        seen |= 1 << i; mapping.push(i);
    }
    if seen & spec.required != spec.required { return false; }
    for (node, i) in nodes.iter_mut().zip(mapping) {
        let (_, name, id) = spec.identities[i];
        node.set(b"NetName", name); node.set(b"NetID", id.to_string().as_bytes()); node.set(b"Type", spec.faction);
    }
    true
}
#[cfg(test)]
fn project(nodes: &mut [Node]) -> bool { project_for(nodes, &SPEC) }
// The five XML virtual methods used by the four native factories. Factories
// consume synchronously, copying strings into their own allocated records.
#[repr(C)]
struct XmlCopy { table: *const usize, nodes: Vec<Node>, current: usize }
unsafe extern "thiscall" fn attr(p: usize, key: *const u8) -> *const u8 {
    let copy = &*(p as *const XmlCopy);
    let Some(key) = super::cbytes(key as usize, 64) else { return std::ptr::null(); };
    let Some(i) = KEYS.iter().position(|k| &k[..k.len()-1] == key) else { return std::ptr::null(); };
    copy.nodes[copy.current].attrs[i].as_ref().map_or(std::ptr::null(), |v| v.as_ptr() as *const u8)
}
unsafe extern "thiscall" fn node_name(p: usize) -> *const u8 {
    if (*(p as *const XmlCopy)).current == 0 { b"Weapon\0".as_ptr() } else { b"Attachment\0".as_ptr() }
}
unsafe extern "thiscall" fn first(p: usize) -> u8 {
    let copy = &mut *(p as *mut XmlCopy);
    if copy.current != 0 || copy.nodes.len() <= 1 { return 0; } copy.current = 1; 1
}
unsafe extern "thiscall" fn next(p: usize) -> i32 {
    let copy = &mut *(p as *mut XmlCopy);
    if copy.current == 0 || copy.current + 1 == copy.nodes.len() { return 1; } copy.current += 1; 0
}
unsafe extern "thiscall" fn parent(p: usize) -> u8 {
    let copy = &mut *(p as *mut XmlCopy);
    if copy.current == 0 { return 0; } copy.current = 0; 1
}
fn table() -> [usize; 57] {
    let mut v = [0; 57]; v[0x60/4] = attr as usize; v[0x44/4] = node_name as usize;
    v[0xdc/4] = first as usize; v[0x2c/4] = next as usize; v[0xe0/4] = parent as usize; v
}
unsafe fn identity(p: usize, id: u32, name: &[u8], kind: u32) -> bool {
    word(p, 0x10) == Some(id) && super::name(p) == Some(name) && word(p, 0x3c) == Some(kind)
}
unsafe fn copy_nodes(catalog: usize, mut nodes: Vec<Node>, spec: &CopySpec, mut factory: impl FnMut(usize) -> u8) -> Result<(), &'static str> {
    if word(catalog, 0) != Some(super::STANDARD) { return Err("catalog-kind"); }
    let Some(roots) = super::vector(catalog, 0x34) else { return Err("root-array"); };
    if roots.iter().filter(|&&p| identity(p, spec.source_id, spec.source, 0)).count() != 1 { return Err("campaign-record"); }
    let Some(existing) = super::vector(catalog, 0x14) else { return Err("attachment-array"); };
    // Refuse duplicate/partial/colliding registration instead of overwriting.
    for &p in roots.iter().chain(existing.iter()) {
        if spec.identities.iter().any(|(_, name, id)| word(p, 0x10) == Some(*id) || super::name(p) == Some(*name)) { return Err("identity-collision"); }
    }
    if !project_for(&mut nodes, spec) { return Err("snapshot-schema"); }
    let vtable = table(); let mut copy = XmlCopy { table: vtable.as_ptr(), nodes, current: 0 };
    let ok = factory(&mut copy as *mut _ as usize) != 0;
    let copied = super::vector(catalog, 0x34).is_some_and(|v| v.iter().any(|&p| identity(p, spec.identities[0].2, spec.identities[0].1, spec.kind)));
    if !ok {Err("native-factory")} else if !copied {Err("copy-not-registered")} else {Ok(())}
}
#[cfg(test)]
unsafe fn copy_nodes_for_merc(catalog: usize, nodes: Vec<Node>, factory: impl FnMut(usize) -> u8) -> Result<(), &'static str> {
    copy_nodes(catalog, nodes, &SPEC, factory)
}
#[cfg(test)]
#[path = "scis_campaign_tests.rs"]
mod tests;

// Retail consumes the live XML cursor once. Record the same nodes during that
// pass, not by trying to traverse the already-consumed children a second time.
#[repr(C)]
struct RecordingXml {
    table: *const usize,
    original: usize,
    nodes: Vec<Node>,
    valid: bool,
    limit: usize,
}
unsafe fn capture_node(p: usize) {
    let r = &mut *(p as *mut RecordingXml);
    if !r.valid { return; }
    if r.nodes.len() >= r.limit { r.valid=false;return; }
    let get:Attr=std::mem::transmute(function(r.original,0x60).unwrap());
    match snapshot_node(r.original,get) {
        Some(n)=>r.nodes.push(n), None=>r.valid=false,
    }
}
unsafe extern "thiscall" fn record_attr(p:usize,key:*const u8)->*const u8 {
    let x=(*(p as *const RecordingXml)).original;
    let f:Attr=std::mem::transmute(function(x,0x60).unwrap());f(x,key)
}
unsafe extern "thiscall" fn record_name(p:usize)->*const u8 {
    let x=(*(p as *const RecordingXml)).original;
    let f:unsafe extern "thiscall" fn(usize)->*const u8=std::mem::transmute(function(x,0x44).unwrap());f(x)
}
unsafe extern "thiscall" fn record_first(p:usize)->u8 {
    let x=(*(p as *const RecordingXml)).original;
    let f:CursorBool=std::mem::transmute(function(x,0xdc).unwrap());let result=f(x);
    if result!=0 {capture_node(p);}result
}
unsafe extern "thiscall" fn record_next(p:usize)->i32 {
    let x=(*(p as *const RecordingXml)).original;
    let f:CursorNext=std::mem::transmute(function(x,0x2c).unwrap());let result=f(x);
    if result==0 {capture_node(p);}result
}
unsafe extern "thiscall" fn record_parent(p:usize) {
    let x=(*(p as *const RecordingXml)).original;
    let f:CursorParent=std::mem::transmute(function(x,0xe0).unwrap());f(x)
}
pub(super) unsafe fn register_with_spec(catalog:usize,xml:usize,spec:&CopySpec,mut factory:impl FnMut(usize)->u8)->u8 {
    if ![0x60,0x44,0xdc,0x2c,0xe0].iter().all(|&off|function(xml,off).is_some()) {
        return factory(xml);
    }
    let mut vt=[0usize;57];
    vt[0x60/4]=record_attr as usize;vt[0x44/4]=record_name as usize;
    vt[0xdc/4]=record_first as usize;vt[0x2c/4]=record_next as usize;vt[0xe0/4]=record_parent as usize;
    let mut recording=RecordingXml{table:vt.as_ptr(),original:xml,nodes:vec![],valid:true,limit:spec.identities.len()};
    capture_node(&mut recording as *mut _ as usize);
    let result=factory(&mut recording as *mut _ as usize);
    let count=recording.nodes.len();
    let valid=recording.valid && recording.nodes.first().is_some_and(|n|
        n.get(b"NetName")==Some(spec.source) && n.get(b"Type")==Some(b"Campaign"));
    let outcome=if result==0 {Err("native-campaign-factory")}
        else if !valid {Err("snapshot-invalid")}
        else {copy_nodes(catalog,recording.nodes,spec,|p|factory(p))};
    if spec.source == RETAIL_NAME {
        tracing::info!("SCBL_MOD_SCIS catalog_copied={} xml_nodes={} snapshot_valid={} reason={} faction=Merc menu=TwoHander campaign=preserved xml_passes=1 identity=native-name-hash",outcome.is_ok(),count,valid,outcome.err().unwrap_or("ok"));
    } else {
        tracing::info!("SCBL_MOD_VSS catalog_copied={} xml_nodes={} snapshot_valid={} reason={} faction=Spy menu=TwoHander campaign=preserved xml_passes=1 identity=native-name-hash",outcome.is_ok(),count,valid,outcome.err().unwrap_or("ok"));
    }
    result
}
#[cfg(test)]
unsafe fn copy_for_merc(catalog:usize,xml:usize,factory:impl FnMut(usize)->u8)->bool {
    let Some(nodes)=snapshot(xml) else{return false;};
    copy_nodes_for_merc(catalog,nodes,factory).is_ok()
}

pub(super) unsafe fn register_with_copy(catalog:usize,xml:usize,factory:impl FnMut(usize)->u8)->u8 { register_with_spec(catalog,xml,&SPEC,factory) }
