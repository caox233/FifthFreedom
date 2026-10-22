//! Manual F7 added-data admission; no native reads and no wall clock of its own.
pub const MAX_PACKETS:u32=128; // 5*16*4 = 320 bytes each; <= 40 KiB raw payload/run.
#[derive(Default)]pub struct Window{owner:Option<[u32;7]>,last_partial:Option<u64>,pub used:u32}
impl Window{
 pub fn eligible(&mut self,rows:&[[u32;16];2])->bool{
  let[r,s]=rows;
  if r[0]!=1||s[0]!=1||r[1]!=s[1]||r[3]==0||s[2]&(1<<3|1<<4)!=(1<<3|1<<4){return false;}
  let owner=[r[6],r[7],r[8],r[9],r[10],r[11],r[13]];
  if self.owner!=Some(owner){self.owner=Some(owner);self.last_partial=None;}
  let frame=(r[4]as u64)|((r[5]as u64)<<32);let alpha=f32::from_bits(s[4]);
  if !alpha.is_finite()||!(0.0..=1.0).contains(&alpha){return false;}
  // Capture native partial entry/exit, and the zero-fraction entry frame.
  if (alpha>0.0&&alpha<1.0)||(alpha==0.0&&s[3]&0x400000!=0){self.last_partial=Some(frame);return true;}
  // Two subsequent frames (both PREVIEW/POST_HANDS), never a full steady stream.
  self.last_partial.and_then(|f|frame.checked_sub(f)).is_some_and(|delta|delta<=2)
 }
}
#[cfg(test)]mod tests{
 use super::*;
 fn rows(frame:u64,alpha:f32,ads:bool)->[[u32;16];2]{let mut r=[[0;16];2];r[0][0]=1;r[1][0]=1;r[0][1]=7;r[1][1]=7;r[0][3]=1;r[0][4]=frame as u32;r[0][5]=(frame>>32)as u32;r[1][2]=(1<<3)|(1<<4);r[1][3]=if ads{0x400000}else{0};r[1][4]=alpha.to_bits();r}
 #[test]fn no_steady_capture_partial_and_two_frame_tail_only(){let mut w=Window::default();assert!(!w.eligible(&rows(1,1.,true)));assert!(!w.eligible(&rows(2,0.,false)));assert!(w.eligible(&rows(3,0.,true)));assert!(w.eligible(&rows(4,0.5,true)));for f in[4,5,6]{assert!(w.eligible(&rows(f,1.,true)));}assert!(!w.eligible(&rows(7,1.,true)));assert!(w.eligible(&rows(8,0.5,false)));assert!(w.eligible(&rows(9,0.,false)));assert!(!w.eligible(&rows(11,0.,false)));}
 #[test]fn missing_fraction_serial_owner_generation_and_reverse_frame_do_not_borrow_tail(){let mut w=Window::default();assert!(w.eligible(&rows(20,0.5,true)));assert!(!w.eligible(&rows(19,1.,true)));let mut r=rows(21,1.,true);r[0][8]=11;assert!(!w.eligible(&r));for change in[0,1,2]{let mut r=rows(22,0.5,true);match change{0=>r[1][2]=0,1=>r[1][1]=8,_=>r[1][4]=f32::NAN.to_bits()}assert!(!w.eligible(&r));}}
}
