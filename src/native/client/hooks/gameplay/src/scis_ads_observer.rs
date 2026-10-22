//! Manual F7 observations only. No animation, camera, input or motion mutation.
//! Missing reads use u32::MAX and a separate validity mask, never a guessed zero.
use crate::scis_diagnostics as diag;
use hooks_plugin_api::scope_motion::Request;
#[path="scis_ads_partial.rs"] mod partial;

fn fields<const N:usize>(mut row:[u32;16], mask_slot:usize, links:[(usize,usize,usize);N],
 read:&impl Fn(usize,usize)->Option<u32>)->[u32;16] {
 let mut mask=row[mask_slot];
 for (slot,p,o) in links {if let Some(value)=read(p,o){row[slot]=value;mask|=1<<slot;}else{row[slot]=u32::MAX;}}
 row[mask_slot]=mask;row
}
pub(super) fn lifecycle(core:usize,serial:u32,read:impl Fn(usize,usize)->Option<u32>)->[u32;16] {
 let pawn=read(core,0x24);let hands=pawn.and_then(|p|read(p as usize,0x12ac));
 let anim=hands.and_then(|h|read(h as usize,0x31c));
 let p=pawn.unwrap_or(0)as usize;let h=hands.unwrap_or(0)as usize;let a=anim.unwrap_or(0)as usize;
 let mut row=[1,serial,core as u32,pawn.unwrap_or(u32::MAX),u32::MAX,u32::MAX,u32::MAX,u32::MAX,hands.unwrap_or(u32::MAX),u32::MAX,anim.unwrap_or(u32::MAX),u32::MAX,u32::MAX,u32::MAX,u32::MAX,0];
 for (slot,present) in [(3,pawn.is_some()),(8,hands.is_some()),(10,anim.is_some())]{if present{row[15]|=1<<slot;}}
 fields(row,15,[(4,p,0x20),(5,p,0x948),(6,core,0x46c),(7,p,0x150c),(9,h,0x20),(11,a,0x30),(12,a,0x34),(13,a,0x50),(14,a,0x54)],&|p,o|if p>=0x10000{read(p,o)}else{None})
}

pub(crate) struct Motion {
 active:bool,r:Request,post:bool,serial:u32,stage:u32,idle:u32,bones:u32,output:Option<[f32;2]>,consumed:Option<[f64;14]>,
}
impl Motion {
 pub(crate) fn new(r:Request,post:bool)->Self {
  let active=diag::project_active(7);
  Self{active,r,post,serial:if active{super::seq()}else{0},stage:0,idle:u32::MAX,bones:u32::MAX,output:None,consumed:None}
 }
 pub(crate) fn stage(&mut self,stage:u32){self.stage=stage;}
 pub(crate) fn bones(&mut self,idle:bool,present:bool){self.idle=idle as u32;self.bones=present as u32;}
 pub(crate) fn consumed(&mut self,value:Option<[f64;14]>){if self.active{self.consumed=value;}}
 pub(crate) fn accepted(&mut self,value:[f32;2]){self.stage=7;self.output=Some(value);}
 fn rows(&self,read:impl Fn(usize,usize)->Option<u32>)->[[u32;16];2] {
  let r=self.r;let p=if self.stage!=0 && r.valid(){read(r.core as usize,0x24)}else{None};let h=p.and_then(|p|read(p as usize,0x12ac));let a=h.and_then(|h|read(h as usize,0x31c));
  let row=[1,self.serial,self.post as u32,self.stage,r.frame[0],r.frame[1],r.core,r.pawn,r.pawn_generation,r.controller,r.controller_generation,r.camera,r.mode,r.world,self.output.map_or(u32::MAX,|v|v[0].to_bits()),self.output.map_or(u32::MAX,|v|v[1].to_bits())];
  let mut native=[u32::MAX;16];native[0]=1;native[1]=self.serial;native[2]=u32::from(p.is_some())|(u32::from(h.is_some())<<1)|(u32::from(a.is_some())<<2);native[14]=self.idle;native[15]=self.bones;
  let p=p.unwrap_or(0)as usize;let h=h.unwrap_or(0)as usize;let a=a.unwrap_or(0)as usize;
  if self.stage!=0 && r.valid(){native=fields(native,2,[(3,p,0x948),(4,r.core as usize,0x46c),(5,p,0x150c),(6,h,0x318),(7,a,0x30),(8,a,0x34),(9,a,0x50),(10,a,0x54),(11,p,0x1508),(12,p,0x1510),(13,p,0x4b8)],&|p,o|if p>=0x10000{read(p,o)}else{None});}
  [row,native]
 }
 fn partial_rows(&self,read:impl Fn(usize,usize)->Option<u32>)->[[u32;16];5]{
  // No inspect_motion/full-ADS gate: Drop also executes at stage-1 early return.
  // This closure is called only after the F7 partial-window/cap/budget reservation.
  let r=self.r;
  let [mut palette,raw,placement]=partial::capture(partial::Identity{
   valid:self.stage!=0&&r.valid(),core:r.core,pawn:r.pawn,pawn_generation:r.pawn_generation,
   controller:r.controller,controller_generation:r.controller_generation,camera:r.camera,world:r.world,mode:r.mode,
  },self.serial,|p,o|read(p as usize,o as usize));
  let mut consumed=[[u32::MAX;16];2];
  for row in &mut consumed{row[0]=1;row[1]=self.serial;}
  if let Some(values)=self.consumed{
   // Preserve all f64 bits of the checked/converted poses actually consumed by Motion.
   for(i,value)in values.into_iter().enumerate(){let bits=value.to_bits();let k=(i%7)*2+2;
    consumed[i/7][k]=bits as u32;consumed[i/7][k+1]=(bits>>32)as u32;}
   palette[2]|=partial::CONSUMED;
  }
  [palette,raw,consumed[0],consumed[1],placement]
 }
}
impl Drop for Motion {fn drop(&mut self){
 if !self.active{return;}
 // The recorder checks F7 and reserves both rows before invoking this closure.
 // Therefore a closed/full window performs no added native reads.
 diag::trace_motion_bundle(
  ||self.rows(|p,o|unsafe{crate::equipment_native::word(p,o)}),
  ||self.partial_rows(|p,o|unsafe{crate::equipment_native::word(p,o)}));
}}
