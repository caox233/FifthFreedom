//! Suppress only the zero-opacity retail VSS ADS glass in EMF's
//! thermal-material pass. This is a pre-native-call gate: it never mutates
//! resources, shaders, samplers, visibility flags, or node/material tables.
//! `owned` must revalidate the current VSS scope lease, NOT the SC-IS-or-VSS AO guard.
const MIN: usize=0x10000;
fn ptr(read:&impl Fn(usize,usize)->Option<u32>,p:usize,o:usize)->Option<usize>{
 if p<MIN||p.checked_add(o)?.checked_add(3)?>u32::MAX as usize{return None;}
 let v=read(p,o)? as usize;(v>=MIN).then_some(v)
}
pub(super) fn skip(
 root:usize,render:usize,node:usize,surface:usize,
 read:impl Fn(usize,usize)->Option<u32>, owned:impl FnOnce(usize)->bool,
)->bool {
 let check=||->Option<bool>{
  if [root,render,node,surface].iter().any(|p|*p<MIN){return Some(false);}
  if read(render,0x233c)?!=1{return Some(false);}
  let view=ptr(&read,render,0x20d0)?;
  if read(view,0x340)?!=9||read(root,0)?!=0x02c06404{return Some(false);}
  let pass=ptr(&read,render,0x236c)?;
  if read(pass,0x14)? as usize!=render||read(pass,0)? as usize!=root{return Some(false);}
  let mesh=ptr(&read,node,0xf8)?;
  if read(mesh,0x14)?!=0x573dd314{return Some(false);}
  // Field provenance: native registration/ctors,160E916 factory key store;
  // Lens_02 asset has Alpha=0, unlike the ordinary gun-mounted Lens_03.
  if read(surface,0)?!=0x02c085b8||read(surface,0x14)?!=0x33d4c0ca
    ||((read(surface,0x16c)?>>8)&255)!=0{return Some(false);}
  let implementation=ptr(&read,surface,0x2c)?;
  if read(implementation,0)?!=0x02c08594{return Some(false);}
  let count=read(node,0x164)?;
  if count==0||count>32{return Some(false);}
  let table=ptr(&read,node,0x160)?;
  let mut found=0;
  for i in 0..count as usize {
   let offset=i.checked_mul(8)?;
   table.checked_add(offset)?.checked_add(7)?;
   if read(table,offset)? as usize==surface{found+=1;}
  }
  if found!=1{return Some(false);}
  // Recheck the fixed identities before the final owner-lease check. This is
  // fail-open on an observed transition, not a cross-thread lifetime fence.
  if read(node,0xf8)? as usize!=mesh||read(node,0x160)? as usize!=table
   ||read(node,0x164)?!=count||read(surface,0)?!=0x02c085b8
   ||read(surface,0x14)?!=0x33d4c0ca||read(surface,0x2c)? as usize!=implementation
   ||((read(surface,0x16c)?>>8)&255)!=0||read(implementation,0)?!=0x02c08594
   ||read(mesh,0x14)?!=0x573dd314||read(root,0)?!=0x02c06404
   ||read(render,0x20d0)? as usize!=view||read(render,0x236c)? as usize!=pass
   ||read(pass,0x14)? as usize!=render||read(pass,0)? as usize!=root
   ||read(view,0x340)?!=9||read(render,0x233c)?!=1{return Some(false);}
  Some(true)
 };
 check()==Some(true)&&owned(node)
}

#[cfg(test)]
mod tests {
 use super::*;
 use std::collections::HashMap;
 const ROOT:usize=0x10000; const RENDER:usize=0x20000;
 const NODE:usize=0x30000; const SURFACE:usize=0x40000;
 fn fixture()->HashMap<(usize,usize),u32> {
  HashMap::from([
   ((ROOT,0),0x02c06404),((RENDER,0x233c),1),((RENDER,0x20d0),0x50000),
   ((0x50000,0x340),9),((RENDER,0x236c),0x60000),
   ((0x60000,0),ROOT as u32),((0x60000,0x14),RENDER as u32),
   ((NODE,0xf8),0x70000),((0x70000,0x14),0x573dd314),
   ((SURFACE,0),0x02c085b8),((SURFACE,0x14),0x33d4c0ca),
   ((SURFACE,0x16c),0),((SURFACE,0x2c),0x80000),((0x80000,0),0x02c08594),
   ((NODE,0x164),1),((NODE,0x160),0x90000),((0x90000,0),SURFACE as u32),
  ])
 }
 fn check(m:&HashMap<(usize,usize),u32>,owned:bool)->bool {
  skip(ROOT,RENDER,NODE,SURFACE,|p,o|m.get(&(p,o)).copied(),|n|owned&&n==NODE)
 }
 #[test] fn exact_glass_only_and_every_missing_link_falls_back_to_native() {
  let mut m=fixture();assert!(check(&m,true));assert!(!check(&m,false));
  for key in m.keys().copied().collect::<Vec<_>>() {
   let old=m.remove(&key).unwrap();assert!(!check(&m,true),"missing {key:?}");m.insert(key,old);
  }
  for (key,v) in [((RENDER,0x233c),2),((0x50000,0x340),0),((0x50000,0x340),8),
   ((ROOT,0),0),((SURFACE,0),0x02c08a14),((SURFACE,0x14),0x40a8de2e),
   ((SURFACE,0x16c),100<<8),((0x80000,0),0x02c089f0),((0x70000,0x14),123),
   ((0x60000,0x14),0x21000),((0x60000,0),0x11000),((NODE,0x164),0),
   ((NODE,0x164),33),((0x90000,0),0x41000)] {
   let old=m.insert(key,v).unwrap();assert!(!check(&m,true),"changed {key:?}");m.insert(key,old);
  }
  m.insert((NODE,0x164),2);m.insert((0x90000,8),SURFACE as u32);
  assert!(!check(&m,true),"duplicate surface");
  m.insert((0x90000,8),0xa0000);assert!(check(&m,true));
 }
 #[test] fn closing_identity_changes_are_not_admitted() {
  let m=fixture();
  for key in [(NODE,0xf8),(NODE,0x160),(NODE,0x164),(SURFACE,0),(SURFACE,0x14),
   (SURFACE,0x2c),(SURFACE,0x16c),(0x80000,0),(0x70000,0x14),(ROOT,0),
   (RENDER,0x20d0),(RENDER,0x236c),(0x60000,0x14),(0x60000,0),
   (0x50000,0x340),(RENDER,0x233c)] {
   let reads=std::cell::Cell::new(0);
   let result=skip(ROOT,RENDER,NODE,SURFACE,|p,o| {
    let old=m.get(&(p,o)).copied()?;
    if (p,o)==key { let n=reads.get();reads.set(n+1);
     if n>0 {return Some(if key==(SURFACE,0x16c){1<<8}else{old^1});}
    } Some(old)
   },|_|true);
   assert!(!result,"transition {key:?}");
  }
 }
}
