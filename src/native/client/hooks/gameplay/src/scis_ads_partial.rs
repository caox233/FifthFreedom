//! Manual F7 read-only partial-ADS snapshot. No automatic capture.
//! No native getter, lazy evaluation, gameplay write, timer, allocation or ownership mutation.
//! Caller MUST invoke inside a reserved atomic trace-bundle closure, never before reservation.
//! A raw snapshot is NOT proof that native animation/render consumed this palette.
#[derive(Clone,Copy)]
pub struct Identity {
 pub valid:bool,pub core:u32,pub pawn:u32,pub pawn_generation:u32,
 pub controller:u32,pub controller_generation:u32,pub camera:u32,pub world:u32,pub mode:u32,
}
pub const OWNED:u32=1;pub const PALETTE:u32=2;pub const ELIGIBLE:u32=4;
pub const HEAD:u32=8;pub const GUN:u32=16;pub const COHERENT:u32=32;pub const FINITE:u32=64;
pub const CONSUMED:u32=128;
const M:u32=u32::MAX;
fn blank(serial:u32)->[u32;16]{let mut r=[M;16];r[0]=1;r[1]=serial;r}
// meta mask low bits describe palette; bits 8..21 describe actor/core value reads.
// Raw poses: head q[4],p[3], gun q[4],p[3]. Consumed head uses native_head_camera,
// so it must stay separate from this raw head quaternion.
pub fn capture(id:Identity,serial:u32,read:impl Fn(u32,u32)->Option<u32>)->[[u32;16];3]{
 let(mut meta,mut raw,mut pose)=(blank(serial),blank(serial),blank(serial));meta[2]=0;
 if !id.valid{return [meta,raw,pose];}
 let word=|p:u32,o:u32|if p>=0x10000&&p.checked_add(o).is_some(){read(p,o)}else{None};
 let owned=||[
  (id.core,0x24,id.pawn),(id.core,0x20,id.controller),(id.core,0x1c,id.camera),(id.core,0x2c,id.mode),
  (id.pawn,0x20,id.pawn_generation),(id.pawn,0x144,id.world),
  (id.controller,0x20,id.controller_generation),(id.controller,0x520,id.pawn),(id.controller,0x6a4,id.camera),
  (id.camera,0x61c,id.core),
 ].into_iter().all(|(p,o,v)|word(p,o)==Some(v));
 if !owned(){return [meta,raw,pose];}meta[2]|=OWNED;
 let result=(||->Option<()>{
  let hands=word(id.pawn,0x12ac)?;let generation=word(hands,0x20)?;
  meta[13]=hands;meta[14]=generation;
  if word(hands,0)!=Some(0x02a23634)||word(hands,0x310)!=Some(id.pawn)||word(hands,0x144)!=Some(id.world){return None;}
  // Capture scope/core placement regardless of ADS fraction or idle descriptor.
  // The gameplay caller must keep inspect_motion's original gates unchanged.
  let scope=word(id.pawn,0x1508).unwrap_or(0);
  let links=[(scope,0x9c),(scope,0xa0),(scope,0xa4),(scope,0xa8),(scope,0xac),(scope,0xb0),
   (id.core,0x6c),(id.core,0x70),(id.core,0x74),(id.core,0xbd8),(id.core,0xbdc),(id.core,0xbe0),
   (id.core,0x470),(id.core,0x474)];
  for(i,(p,o))in links.into_iter().enumerate(){if let Some(v)=word(p,o){pose[i+2]=v;meta[2]|=1<<(8+i);}}
  let instance=word(hands,0x1f0)?;meta[15]=instance;
  let component=word(instance,0xc0)?;meta[5]=component;
  let map=word(hands,0x430)?;meta[8]=map;
  let worker=word(0x3301820,0)?;let phase=word(worker,8)?;meta[3]=phase;
  let flags=word(component,0x14)?;meta[6]=flags;meta[2]|=PALETTE;
  // Native 1D21CA0 / 1CE27D0 select AC4 for 0..8/10, AC8 for 9/11.
  // Read only already-ready buffers: do not emulate native refresh on missing flags.
  let ac8=matches!(phase,9|11);
  let required=if ac8{0x00880000}else{0x00080000};
  if phase>11||flags&required!=required{return None;}meta[2]|=ELIGIBLE;
  let slot=if ac8{0xac8}else{0xac4};let buffer=word(component,slot)?;meta[9]=buffer;
  let head=word(map,14*4)?;let gun=word(map,13*4)?;meta[11]=head;meta[12]=gun;
  if head>=1024||gun>=1024||head==gun{return None;}
  for(index,bone)in [head,gun].into_iter().enumerate(){
   let at=buffer.checked_add(bone.checked_mul(48)?)?;let mut complete=true;
   for j in 0..7{if let Some(v)=word(at,j*4){raw[2+index*7+j as usize]=v;}else{complete=false;}}
   if complete{meta[2]|=if index==0{HEAD}else{GUN};}
  }
  meta[4]=word(worker,8).unwrap_or(M);meta[7]=word(component,0x14).unwrap_or(M);meta[10]=word(component,slot).unwrap_or(M);
  if owned()&&word(id.pawn,0x12ac)==Some(hands)&&word(hands,0x20)==Some(generation)
   &&word(hands,0x1f0)==Some(instance)&&word(instance,0xc0)==Some(component)&&word(hands,0x430)==Some(map)
   &&word(map,14*4)==Some(head)&&word(map,13*4)==Some(gun)
   &&meta[3]==meta[4]&&meta[6]==meta[7]&&meta[9]==meta[10]{meta[2]|=COHERENT;}
  if meta[2]&(HEAD|GUN)==HEAD|GUN&&raw[2..].iter().all(|v|f32::from_bits(*v).is_finite()){meta[2]|=FINITE;}
  Some(())
 })();let _=result;
 [meta,raw,pose]
}
#[cfg(test)]mod tests{
 use super::*;use std::{collections::HashMap,cell::Cell};
 fn data()->(Identity,HashMap<(u32,u32),u32>){
  let i=Identity{valid:true,core:0x10000,pawn:0x20000,pawn_generation:10,controller:0x30000,controller_generation:11,camera:0x40000,world:0x50000,mode:0x60000};
  let mut m=HashMap::from([((i.core,0x24),i.pawn),((i.core,0x20),i.controller),((i.core,0x1c),i.camera),((i.core,0x2c),i.mode),((i.pawn,0x20),10),((i.pawn,0x144),i.world),((i.controller,0x20),11),((i.controller,0x520),i.pawn),((i.controller,0x6a4),i.camera),((i.camera,0x61c),i.core),((i.pawn,0x12ac),0x70000),((0x70000,0),0x02a23634),((0x70000,0x20),12),((0x70000,0x310),i.pawn),((0x70000,0x144),i.world),((0x70000,0x1f0),0x80000),((0x80000,0xc0),0x90000),((0x70000,0x430),0xa0000),((0x3301820,0),0xb0000),((0xb0000,8),8),((0x90000,0x14),0x880000),((0x90000,0xac4),0xc0000),((0x90000,0xac8),0xd0000),((0xa0000,56),1),((0xa0000,52),2),((i.pawn,0x1508),0xe0000)]);
  for base in [0xc0000,0xd0000]{for b in [1,2]{for j in 0..7{m.insert((base+b*48,j*4),(base as f32+b as f32+j as f32).to_bits());}}}
  for o in [0x9c,0xa0,0xa4,0xa8,0xac,0xb0]{m.insert((0xe0000,o),o);}
  for o in [0x6c,0x70,0x74,0xbd8,0xbdc,0xbe0,0x470,0x474]{m.insert((i.core,o),o);}
  (i,m)
 }
 #[test]fn half_ads_and_exit_are_captured_without_idle_gate(){let(i,mut m)=data();for alpha in [0.,0.25,0.99,1.]{m.insert((i.core,0x46c),f32::to_bits(alpha));m.insert((i.pawn,0x948),if alpha==0.{0}else{0x400000});let r=capture(i,7,|p,o|m.get(&(p,o)).copied());assert_eq!(r[0][2]&0x7f,0x7f);assert_eq!(r[1][2],(0xc0000 as f32+1.).to_bits());}}
 #[test]fn worker_phase_selects_the_existing_buffer_without_getter(){let(i,mut m)=data();for phase in 0..=11{m.insert((0xb0000,8),phase);let r=capture(i,8,|p,o|m.get(&(p,o)).copied());assert_eq!(r[0][9],if matches!(phase,9|11){0xd0000}else{0xc0000});assert_ne!(r[0][2]&COHERENT,0);}}
 #[test]fn invalid_request_has_no_reads(){let(mut i,m)=data();i.valid=false;let n=Cell::new(0);let r=capture(i,9,|p,o|{n.set(n.get()+1);m.get(&(p,o)).copied()});assert_eq!(n.get(),0);assert_eq!(r[0][2],0);}
 #[test]fn invalid_palette_is_missing_not_zero(){let(i,mut m)=data();m.insert((0xb0000,8),12);let r=capture(i,10,|p,o|m.get(&(p,o)).copied());assert_eq!(r[0][2]&ELIGIBLE,0);assert_eq!(r[1][2..],[M;14]);}
 #[test]fn changed_phase_is_marked_incoherent(){let(i,m)=data();let n=Cell::new(0);let r=capture(i,11,|p,o|{if(p,o)==(0xb0000,8){n.set(n.get()+1);Some(if n.get()==1{8}else{9})}else{m.get(&(p,o)).copied()}});assert_eq!(r[0][2]&COHERENT,0);assert_eq!((r[0][3],r[0][4]),(8,9));}
 #[test]fn ac8_needs_both_ready_flags_ac4_does_not_need_ac8_bit(){let(i,mut m)=data();for phase in 0..=11{m.insert((0xb0000,8),phase);m.insert((0x90000,0x14),0x80000);let r=capture(i,15,|p,o|m.get(&(p,o)).copied());assert_eq!(r[0][2]&ELIGIBLE!=0,!matches!(phase,9|11));if matches!(phase,9|11){assert_eq!(r[1][2..],[M;14]);}}}
 #[test]fn unsupported_unsigned_phases_never_read_raw_bones(){let(i,mut m)=data();for phase in[12,99,u32::MAX]{m.insert((0xb0000,8),phase);let n=Cell::new(0);let r=capture(i,16,|p,o|{if (0xc0000..0xe0000).contains(&p){n.set(n.get()+1);}m.get(&(p,o)).copied()});assert_eq!(r[0][2]&ELIGIBLE,0);assert_eq!(n.get(),0);}}
 #[test]fn foreign_owner_is_not_followed(){let(i,mut m)=data();m.insert((i.core,0x24),0x12345678);let n=Cell::new(0);let r=capture(i,12,|p,o|{if p==0x70000{n.set(n.get()+1);}m.get(&(p,o)).copied()});assert_eq!(n.get(),0);assert_eq!(r[0][2],0);}
}
