use super::*;

fn nodes(optic: bool) -> Vec<Node> {
    IDENTITIES.iter().enumerate().filter(|(i,_)| optic || *i != 1).map(|(i,(name,_,_))| {
        let mut n=Node{attrs:vec![None;KEYS.len()]};
        n.set(b"NetName",name);n.set(b"Type",b"Campaign");
        let old=[RETAIL_ID,3719584700,861641444,940275474,3818559795,1237274220,1743237676][i];
        n.set(b"NetID",old.to_string().as_bytes());
        n.set(b"Archetype",if i==0 {RETAIL_NAME} else {name.split(|b|*b==b'.').nth(1).unwrap()});
        n.set(b"SlotType",b"TwoHander");n.set(b"Cost",b"103750");
        n.set(b"Purchased",b"0");n.set(b"Unlocked",b"0");
        n.set(b"NameKey",b"LocID_CITEM_Sniper_Proto");n.set(b"Damage",b"0.85");n
    }).collect()
}
struct Record { bytes:Box<[u32]>, _name:CString }
struct Catalog { c:Box<[u32]>, records:Vec<Record>, roots:Vec<u32>, children:Vec<u32>, owned:Vec<Box<[u32]>> }
impl Catalog {
    fn new() -> Self {let mut f=Self{c:vec![0;16].into_boxed_slice(),records:vec![],roots:vec![],children:vec![],owned:vec![]};f.c[0]=super::super::STANDARD;f}
    fn refresh(&mut self) {
        for (off,ptr,len) in [(0x34,self.roots.as_ptr(),self.roots.len()),(0x14,self.children.as_ptr(),self.children.len())] {
            self.c[off/4]=ptr as u32;self.c[off/4+1]=len as u32;self.c[off/4+2]=len as u32;
        }
    }
    fn add(&mut self, nodes:&[Node]) {
        let start=self.records.len();let mut children=vec![];
        for (i,n) in nodes.iter().enumerate() {
            let name=CString::new(n.get(b"NetName").unwrap()).unwrap();
            let mut bytes=vec![0x55555555;0xb4/4].into_boxed_slice();
            let kind=if n.get(b"Type")==Some(b"Merc") {2} else {0};
            bytes[0]=if kind==2 {0x01000101} else {0x01000000}; // native availability projection only on MP copy
            bytes[0x10/4]=std::str::from_utf8(n.get(b"NetID").unwrap()).unwrap().parse().unwrap();
            bytes[0x18/4]=name.as_ptr() as u32;bytes[0x1c/4]=name.as_bytes_with_nul().len() as u32;bytes[0x20/4]=bytes[0x1c/4];
            bytes[0x3c/4]=kind;bytes[0x40/4]=if i==0 {1} else {11};
            bytes[0x88/4]=0;bytes[0x8c/4]=0;bytes[0x90/4]=0;
            let p=bytes.as_ptr() as u32;
            if i==0 {self.roots.push(p);} else {children.push(p);self.children.push(p);}
            self.records.push(Record{bytes,_name:name});
        }
        let list=children.into_boxed_slice();let root=&mut self.records[start].bytes;
        root[0x88/4]=list.as_ptr() as u32;root[0x8c/4]=list.len() as u32;root[0x90/4]=list.len() as u32;
        self.owned.push(list);self.refresh();
    }
    unsafe fn native_factory_fixture(&mut self, xml:usize) -> u8 {
        // Same thiscall virtual interface consumed by the retail factory.
        let get:Attr=std::mem::transmute(function(xml,0x60).unwrap());
        let first:CursorBool=std::mem::transmute(function(xml,0xdc).unwrap());
        let next:CursorNext=std::mem::transmute(function(xml,0x2c).unwrap());
        let parent:CursorBool=std::mem::transmute(function(xml,0xe0).unwrap());
        let tag:unsafe extern "thiscall" fn(usize)->*const u8=std::mem::transmute(function(xml,0x44).unwrap());
        assert_eq!(super::super::cbytes(tag(xml) as usize,32),Some(b"Weapon".as_slice()));
        let mut result=vec![snapshot_node(xml,get).unwrap()];
        assert_eq!(first(xml),1);
        loop {
            assert_eq!(super::super::cbytes(tag(xml) as usize,32),Some(b"Attachment".as_slice()));
            result.push(snapshot_node(xml,get).unwrap());if next(xml)!=0 {break;}
        }
        assert_eq!(parent(xml),1);
        assert!(result.iter().all(|n|n.get(b"Type")==Some(b"Merc")));
        assert_eq!(result[0].get(b"Archetype"),Some(RETAIL_NAME));
        self.add(&result);1
    }
}

#[test]
fn scis_campaign_original_survives_merc_registration() {
    for optic in [false,true] {
        let v=table();let source=nodes(optic);let mut xml=XmlCopy{table:v.as_ptr(),nodes:source.clone(),current:0};
        let mut f=Catalog::new();f.add(&source);let originals:Vec<_>=f.records.iter().map(|r|r.bytes.clone()).collect();
        let p=f.c.as_ptr() as usize;
        let ok=unsafe{copy_for_merc(p,&mut xml as *mut _ as usize,|x|f.native_factory_fixture(x))};
        let unchanged=f.records.iter().zip(&originals).all(|(r,b)|r.bytes==*b);
        println!("SCIS_CAMPAIGN original_kind={} original_unchanged={} children_unchanged={} merc_entries={} separate_children={} native_factory={ok}",f.records[0].bytes[0x3c/4],unchanged,unchanged,f.roots.len()-1,f.owned.len()==2);
        assert!(ok);assert!(unchanged);assert_eq!(f.roots.len(),2);assert_eq!(xml.current,0);
        for (before,after) in source.iter().zip(&xml.nodes) {assert_eq!(before.attrs,after.attrs);}
        let copy=&f.records[source.len()].bytes;assert_eq!(copy[0x3c/4],2);assert_eq!(copy[0x40/4],1);
        assert_ne!(copy[0x88/4],f.records[0].bytes[0x88/4]);assert_eq!(copy[0x10/4],COPY_ID);
        let count=f.records.len();
        assert!(!unsafe{copy_for_merc(p,&mut xml as *mut _ as usize,|_|panic!("duplicate factory call"))});
        assert_eq!(f.records.len(),count);
    }
}
#[test]
fn scis_campaign_copy_changes_only_catalog_identity_and_faction() {
    let original=nodes(true);let mut copy=original.clone();assert!(project(&mut copy));
    for (a,b) in original.iter().zip(&copy) {
        for key in KEYS {
            let key=&key[..key.len()-1];
            if ![b"NetName".as_slice(),b"NetID",b"Type"].contains(&key) {assert_eq!(a.get(key),b.get(key));}
        }
        assert_eq!(a.get(b"Type"),Some(b"Campaign".as_slice()));assert_eq!(b.get(b"Type"),Some(b"Merc".as_slice()));
    }
}
#[test]
fn scis_campaign_malformed_and_conflicting_ids_never_touch_original() {
    for bad in 0..6 {
        let v=table();let source=nodes(true);let mut xml=XmlCopy{table:v.as_ptr(),nodes:source.clone(),current:0};
        let mut f=Catalog::new();f.add(&source);
        match bad {
            0=>xml.nodes[1].set(b"NetName",b"foreign"),
            1=>xml.nodes[1].set(b"Type",b"Spy"),
            2=>xml.nodes.push(xml.nodes[1].clone()),
            3=>xml.nodes[1]=xml.nodes[2].clone(),
            4=>f.records[1].bytes[0x10/4]=COPY_ID,
            _=>f.records[0].bytes[0x3c/4]=2,
        }
        let before:Vec<_>=f.records.iter().map(|r|r.bytes.clone()).collect();
        assert!(!unsafe{copy_for_merc(f.c.as_ptr() as usize,&mut xml as *mut _ as usize,|_|panic!("invalid copy must not reach native factory"))});
        assert_eq!(xml.current,0);assert!(f.records.iter().zip(before).all(|(r,b)|r.bytes==b));
    }
}
#[test]
fn scis_campaign_native_failure_does_not_modify_original() {
    let v=table();let source=nodes(true);let mut xml=XmlCopy{table:v.as_ptr(),nodes:source.clone(),current:0};
    let mut f=Catalog::new();f.add(&source);let before:Vec<_>=f.records.iter().map(|r|r.bytes.clone()).collect();
    let mut calls=0;
    assert!(!unsafe{copy_for_merc(f.c.as_ptr() as usize,&mut xml as *mut _ as usize,|_|{calls+=1;0})});
    assert_eq!(calls,1);assert_eq!(xml.current,0);assert!(f.records.iter().zip(before).all(|(r,b)|r.bytes==b));
}

#[test]
fn scis_catalog_single_live_xml_pass_registers_both_slots() {
    fn node(attrs:&[(&[u8],&[u8])])->Node {
        let mut n=Node{attrs:vec![None;KEYS.len()]};
        for &(k,v) in attrs {n.set(k,v);}n
    }
    let source=vec![
    node(&[(b"Cost".as_slice(), b"103750".as_slice()), (b"TokenCost".as_slice(), b"0".as_slice()), (b"NameKey".as_slice(), b"LocID_CITEM_Sniper_Proto".as_slice()), (b"DescKey".as_slice(), b"LocID_CDESC_Sniper_Proto".as_slice()), (b"Purchased".as_slice(), b"0".as_slice()), (b"Unlocked".as_slice(), b"0".as_slice()), (b"NetName".as_slice(), b"Sniper_Proto".as_slice()), (b"NetID".as_slice(), b"625774215".as_slice()), (b"LabLevelReq".as_slice(), b"3".as_slice()), (b"IsStandardItem".as_slice(), b"1".as_slice()), (b"Type".as_slice(), b"Campaign".as_slice()), (b"SlotType".as_slice(), b"TwoHander".as_slice()), (b"Archetype".as_slice(), b"Sniper_Proto".as_slice()), (b"Damage".as_slice(), b"0.85".as_slice()), (b"Range".as_slice(), b"0.8".as_slice()), (b"Accuracy".as_slice(), b"0.75".as_slice()), (b"Handling".as_slice(), b"0.35".as_slice()), (b"CartridgeCapacity".as_slice(), b"8".as_slice()), (b"MaxInternalUpgrades".as_slice(), b"3".as_slice()), (b"CanHaveMultipleAmmoUpgrades".as_slice(), b"0".as_slice()), (b"Subcategory".as_slice(), b"SniperRifle".as_slice())]),
    node(&[(b"Cost".as_slice(), b"26500".as_slice()), (b"TokenCost".as_slice(), b"0".as_slice()), (b"NameKey".as_slice(), b"LocID_CITEM_Optic_HighPowerScope".as_slice()), (b"DescKey".as_slice(), b"LocID_CDESC_Optic_HighPowerScope".as_slice()), (b"Purchased".as_slice(), b"0".as_slice()), (b"Unlocked".as_slice(), b"1".as_slice()), (b"NetName".as_slice(), b"Sniper_Proto.Optic_HighPowerScope".as_slice()), (b"NetID".as_slice(), b"3719584700".as_slice()), (b"IsStandardItem".as_slice(), b"1".as_slice()), (b"Type".as_slice(), b"Campaign".as_slice()), (b"Archetype".as_slice(), b"Optic_HighPowerScope".as_slice()), (b"Damage".as_slice(), b"0".as_slice()), (b"Range".as_slice(), b"0".as_slice()), (b"Accuracy".as_slice(), b"0.1".as_slice()), (b"Handling".as_slice(), b"0".as_slice()), (b"Category".as_slice(), b"Optic".as_slice())]),
    node(&[(b"Cost".as_slice(), b"7900".as_slice()), (b"TokenCost".as_slice(), b"0".as_slice()), (b"NameKey".as_slice(), b"LocID_CITEM_Internal_MatchGradeTrigger".as_slice()), (b"DescKey".as_slice(), b"LocID_CDESC_Internal_MatchGradeTrigger".as_slice()), (b"Purchased".as_slice(), b"0".as_slice()), (b"Unlocked".as_slice(), b"1".as_slice()), (b"NetName".as_slice(), b"Sniper_Proto.Internal_MatchGradeTrigger".as_slice()), (b"NetID".as_slice(), b"861641444".as_slice()), (b"IsStandardItem".as_slice(), b"1".as_slice()), (b"Type".as_slice(), b"Campaign".as_slice()), (b"Archetype".as_slice(), b"Internal_MatchGradeTrigger".as_slice()), (b"Damage".as_slice(), b"0".as_slice()), (b"Range".as_slice(), b"0".as_slice()), (b"Accuracy".as_slice(), b"0".as_slice()), (b"Handling".as_slice(), b"0.1".as_slice()), (b"Category".as_slice(), b"Internal".as_slice())]),
    node(&[(b"Cost".as_slice(), b"8250".as_slice()), (b"TokenCost".as_slice(), b"0".as_slice()), (b"NameKey".as_slice(), b"LocID_CITEM_Internal_RecoilBufferAdj".as_slice()), (b"DescKey".as_slice(), b"LocID_CDESC_Internal_RecoilBufferAdj".as_slice()), (b"Purchased".as_slice(), b"0".as_slice()), (b"Unlocked".as_slice(), b"1".as_slice()), (b"NetName".as_slice(), b"Sniper_Proto.Internal_RecoilBufferAdj".as_slice()), (b"NetID".as_slice(), b"940275474".as_slice()), (b"IsStandardItem".as_slice(), b"1".as_slice()), (b"Type".as_slice(), b"Campaign".as_slice()), (b"Archetype".as_slice(), b"Internal_RecoilBufferAdj".as_slice()), (b"Damage".as_slice(), b"0".as_slice()), (b"Range".as_slice(), b"0".as_slice()), (b"Accuracy".as_slice(), b"0".as_slice()), (b"Handling".as_slice(), b"0.1".as_slice()), (b"Category".as_slice(), b"Internal".as_slice())]),
    node(&[(b"Cost".as_slice(), b"6600".as_slice()), (b"TokenCost".as_slice(), b"0".as_slice()), (b"NameKey".as_slice(), b"LocID_CITEM_Internal_WeightBalance".as_slice()), (b"DescKey".as_slice(), b"LocID_CDESC_Internal_WeightBalance".as_slice()), (b"Purchased".as_slice(), b"0".as_slice()), (b"Unlocked".as_slice(), b"1".as_slice()), (b"NetName".as_slice(), b"Sniper_Proto.Internal_WeightBalance".as_slice()), (b"NetID".as_slice(), b"3818559795".as_slice()), (b"IsStandardItem".as_slice(), b"1".as_slice()), (b"Type".as_slice(), b"Campaign".as_slice()), (b"Archetype".as_slice(), b"Internal_WeightBalance".as_slice()), (b"Damage".as_slice(), b"0".as_slice()), (b"Range".as_slice(), b"0".as_slice()), (b"Accuracy".as_slice(), b"0.05".as_slice()), (b"Handling".as_slice(), b"0.05".as_slice()), (b"Category".as_slice(), b"Internal".as_slice())]),
    node(&[(b"Cost".as_slice(), b"13200".as_slice()), (b"TokenCost".as_slice(), b"0".as_slice()), (b"NameKey".as_slice(), b"LocID_CITEM_Rifle_Ammo_MatchGrade".as_slice()), (b"DescKey".as_slice(), b"LocID_CDESC_Rifle_Ammo_MatchGrade".as_slice()), (b"Purchased".as_slice(), b"0".as_slice()), (b"Unlocked".as_slice(), b"1".as_slice()), (b"NetName".as_slice(), b"Sniper_Proto.Rifle_Ammo_MatchGrade".as_slice()), (b"NetID".as_slice(), b"1237274220".as_slice()), (b"IsStandardItem".as_slice(), b"1".as_slice()), (b"Type".as_slice(), b"Campaign".as_slice()), (b"Archetype".as_slice(), b"Rifle_Ammo_MatchGrade".as_slice()), (b"Damage".as_slice(), b"0".as_slice()), (b"Range".as_slice(), b"0.05".as_slice()), (b"Accuracy".as_slice(), b"0.1".as_slice()), (b"Handling".as_slice(), b"0".as_slice()), (b"Category".as_slice(), b"Ammo".as_slice())]),
    node(&[(b"Cost".as_slice(), b"9900".as_slice()), (b"TokenCost".as_slice(), b"0".as_slice()), (b"NameKey".as_slice(), b"LocID_CITEM_Rifle_Ammo_AP".as_slice()), (b"DescKey".as_slice(), b"LocID_CDESC_Rifle_Ammo_AP".as_slice()), (b"Purchased".as_slice(), b"0".as_slice()), (b"Unlocked".as_slice(), b"1".as_slice()), (b"NetName".as_slice(), b"Sniper_Proto.Rifle_Ammo_AP".as_slice()), (b"NetID".as_slice(), b"1743237676".as_slice()), (b"IsStandardItem".as_slice(), b"1".as_slice()), (b"Type".as_slice(), b"Campaign".as_slice()), (b"Archetype".as_slice(), b"Rifle_Ammo_AP".as_slice()), (b"Damage".as_slice(), b"0.15".as_slice()), (b"Range".as_slice(), b"0".as_slice()), (b"Accuracy".as_slice(), b"0".as_slice()), (b"Handling".as_slice(), b"0".as_slice()), (b"Category".as_slice(), b"Ammo".as_slice())]),
    ];
    #[repr(C)]
    struct OnePass { base:XmlCopy, entered:bool, passes:u32 }
    unsafe extern "thiscall" fn once(p:usize)->u8 {
        let r=&mut *(p as *mut OnePass);r.passes+=1;
        if r.entered {return 0;}r.entered=true;first(p)
    }
    let mut vt=table();vt[0xdc/4]=once as usize;
    let mut xml=OnePass{base:XmlCopy{table:vt.as_ptr(),nodes:source.clone(),current:0},entered:false,passes:0};
    let mut f=Catalog::new();
    let mut calls=0;
    let result=unsafe{register_with_copy(f.c.as_ptr() as usize,&mut xml as *mut _ as usize,|p|{
        calls+=1;
        let get:Attr=std::mem::transmute(function(p,0x60).unwrap());
        let first:CursorBool=std::mem::transmute(function(p,0xdc).unwrap());
        let next:CursorNext=std::mem::transmute(function(p,0x2c).unwrap());
        let parent:CursorParent=std::mem::transmute(function(p,0xe0).unwrap());
        let mut rows=vec![snapshot_node(p,get).unwrap()];
        if first(p)!=0 {loop {rows.push(snapshot_node(p,get).unwrap());if next(p)!=0 {break;}}parent(p);}
        f.add(&rows);1
    })};
    assert_eq!(result,1);assert_eq!(calls,2);assert_eq!(xml.passes,1);
    assert_eq!(xml.base.current,0);assert_eq!(f.roots.len(),2);
    assert_eq!(f.records[0].bytes[0x3c/4],0);
    assert_eq!(f.records[source.len()].bytes[0x3c/4],2);
    assert_eq!(f.records[source.len()].bytes[0x40/4],1);
    assert_eq!(f.owned[0].len(),6);assert_eq!(f.owned[1].len(),6);
    assert_eq!(source.iter().map(|n|n.attrs.clone()).collect::<Vec<_>>(),xml.base.nodes.iter().map(|n|n.attrs.clone()).collect::<Vec<_>>());
    println!("SCIS_CATALOG live_xml_passes=1 campaign=1 merc_secondary=1 merc_attachments=6 original_unchanged=true");
}

#[test]
fn scis_native_catalog_identity_invariant() {
    assert_eq!(native_net_id(b"Sniper_Proto"),RETAIL_ID);
    assert_eq!(COPY_ID,1025642083);
    for &(old,name,id) in IDENTITIES {
        assert_eq!(native_net_id(name),id);
        assert_ne!(native_net_id(old),id);
        assert_eq!(native_net_id(&name.to_ascii_uppercase()),id);
    }
}

#[test]
#[ignore = "retail x86 name hash, catalogue lookup and attachment-page replay"]
fn scis_native_name_lookup_stats_attachments_and_inventory_identity() {
    use std::ffi::c_void;
    #[link(name="kernel32")] extern "system" {
        fn LoadLibraryW(p:*const u16)->*mut c_void;
        fn GetProcAddress(m:*mut c_void,p:*const u8)->*mut c_void;
    }
    unsafe {
        let path=std::env::var("SCBL_SCIS_IDENTITY_FIXTURE").unwrap();
        let path:Vec<u16>=path.encode_utf16().chain(Some(0)).collect();
        let dll=LoadLibraryW(path.as_ptr());assert!(!dll.is_null());
        let init:unsafe extern "C" fn()->u32=std::mem::transmute(GetProcAddress(dll,b"init_replay\0".as_ptr()));
        let hash:unsafe extern "C" fn(*const u8)->u32=std::mem::transmute(GetProcAddress(dll,b"native_hash\0".as_ptr()));
        let lookup:unsafe extern "C" fn(usize,*const u8)->usize=std::mem::transmute(GetProcAddress(dll,b"native_lookup\0".as_ptr()));
        let attachment:unsafe extern "C" fn(usize,*const u8,*mut f32)->u32=std::mem::transmute(GetProcAddress(dll,b"native_attachment\0".as_ptr()));
        assert_eq!(init(),1);
        assert_eq!(hash(b"Sniper_Proto\0".as_ptr()),RETAIL_ID);
        let source=nodes(true);
        for n in &source {
            assert_eq!(hash(CString::new(n.get(b"NetName").unwrap()).unwrap().as_ptr() as *const u8),std::str::from_utf8(n.get(b"NetID").unwrap()).unwrap().parse::<u32>().unwrap());
        }
        let mut copy=source.clone();assert!(project(&mut copy));
        let mut f=Catalog::new();f.add(&source);f.add(&copy);
        // Fixed native-layout fixture, with the retail XML's actual stat values.
        // Registration itself is covered separately; these are its consumers.
        for root in [0,source.len()] {
            for (off,v) in [(0x94,0.85f32),(0x98,0.8),(0x9c,0.75),(0xa0,0.35)] {f.records[root].bytes[off/4]=v.to_bits();}
            f.records[root].bytes[0xa4/4]=8;f.records[root].bytes[0xa8/4]=3;
            for i in 1..source.len() {
                for off in [0x8c,0x90,0x94,0x98] {f.records[root+i].bytes[off/4]=0f32.to_bits();}
            }
            f.records[root+2].bytes[0x98/4]=0.1f32.to_bits();
        }
        let original=f.records[..source.len()].iter().map(|r|r.bytes.clone()).collect::<Vec<_>>();
        let cp=f.c.as_ptr() as usize;
        let found=lookup(cp,CString::new(COPY_NAME).unwrap().as_ptr() as *const u8);
        let children=IDENTITIES.iter().skip(1).filter(|(_,name,_)|lookup(cp,CString::new(*name).unwrap().as_ptr() as *const u8)!=0).count();
        let mut stats=[0f32;4];
        let click_ok=attachment(found,b"Internal_MatchGradeTrigger\0".as_ptr(),stats.as_mut_ptr())==1;
        let capacity=word(found,0xa4).unwrap_or(0);
        let slots=word(found,0xa8).unwrap_or(0);
        let secondary=word(found,0x40)==Some(1) && word(found,0x3c)==Some(2);
        println!("SCIS_NATIVE_REPLAY parent_found={} capacity={} upgrade_slots={} children_found={} attachment_read_ok={} secondary_identity={} campaign_unchanged={}",found!=0,capacity,slots,children,click_ok,secondary,f.records[..source.len()].iter().zip(&original).all(|(a,b)|a.bytes==*b));
        assert_ne!(found,0,"Native name-to-NetID lookup loses the registered weapon");
        assert_eq!((capacity,slots,children,click_ok,secondary),(8,3,6,true,true));
        assert_eq!(word(found,0x94),Some(0.85f32.to_bits()));assert_eq!(stats,[0.0,0.0,0.0,0.1]);
        for n in &copy {assert_eq!(hash(CString::new(n.get(b"NetName").unwrap()).unwrap().as_ptr() as *const u8),std::str::from_utf8(n.get(b"NetID").unwrap()).unwrap().parse::<u32>().unwrap());}
        assert!(f.records[..source.len()].iter().zip(original).all(|(a,b)|a.bytes==b));
    }
}
