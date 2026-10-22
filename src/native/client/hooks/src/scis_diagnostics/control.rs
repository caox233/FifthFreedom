//! Pure edge-triggered capture controller, shared by both diagnostic modules.
#[derive(Clone,Copy,Debug,PartialEq,Eq)]
pub(super) enum Reason { Manual, Limit }
impl Reason { pub(super) fn name(self)->&'static str { match self { Self::Manual=>"manual",Self::Limit=>"safety_limit" } } }
#[derive(Clone,Copy,Debug,PartialEq,Eq)]
pub(super) enum Event { Start{run:u32,project:u8}, Stop{run:u32,project:u8,reason:Reason}, Busy{project:u8,requested:u8} }
#[derive(Default)]
pub(super) struct Control { down:u8,focused:bool,active:Option<(u32,u8,u64)>,serial:u32 }
impl Control {
 pub(super) fn poll(&mut self,down:u8,focused:bool,now:u64,limit_ms:u64)->Option<Event> {
  let rising=(down&127)&!self.down;let was_focused=self.focused;
  self.down=down&127;self.focused=focused;
  if let Some((run,project,start))=self.active {
   if now.saturating_sub(start)>=limit_ms {self.active=None;return Some(Event::Stop{run,project,reason:Reason::Limit});}
  }
  // Background keys and a key already held when focus returns never arm capture.
  if !focused||!was_focused||rising.count_ones()!=1{return None;}
  let requested=rising.trailing_zeros()as u8+1;
  if let Some((run,project,_))=self.active {
   if project==requested {self.active=None;Some(Event::Stop{run,project,reason:Reason::Manual})}
   else {Some(Event::Busy{project,requested})}
  } else {
   self.serial=self.serial.checked_add(1)?;
   self.active=Some((self.serial,requested,now));Some(Event::Start{run:self.serial,project:requested})
  }
 }
}
pub(super) fn name(project:u8)->&'static str {match project {1=>"static_ads",2=>"slow_mouse",3=>"zoom_alignment",4=>"chat_delivery",5=>"shot_sound",6=>"fire_animation",7=>"ads_transition",_=>"invalid"}}

#[cfg(test)]mod tests {
 use super::*;
 #[test]fn toggle_release_repeat_and_no_overlap(){
  let mut c=Control::default();assert_eq!(c.poll(0,true,0,120000),None);
  assert_eq!(c.poll(1,true,1,120000),Some(Event::Start{run:1,project:1}));
  for t in 2..1000 {assert_eq!(c.poll(1,true,t,120000),None);}
  assert_eq!(c.poll(0,true,1000,120000),None);
  assert_eq!(c.poll(2,true,1001,120000),Some(Event::Busy{project:1,requested:2}));
  c.poll(0,true,1002,120000);
  assert_eq!(c.poll(1,true,1003,120000),Some(Event::Stop{run:1,project:1,reason:Reason::Manual}));
  c.poll(0,true,1004,120000);
  assert_eq!(c.poll(1,true,1005,120000),Some(Event::Start{run:2,project:1}));
 }
 #[test]fn focus_chords_and_timeout_do_not_restart_held_key(){
  let mut c=Control::default();c.poll(0,true,0,120000);
  assert_eq!(c.poll(3,true,1,120000),None);c.poll(0,true,2,120000);
  assert_eq!(c.poll(4,false,3,120000),None);assert_eq!(c.poll(4,true,4,120000),None);
  c.poll(0,true,5,120000);
  assert_eq!(c.poll(8,true,6,120000),Some(Event::Start{run:1,project:4}));
  assert_eq!(c.poll(0,false,7,120000),None); // receiving endpoint can remain in background
  assert_eq!(c.poll(8,false,120005,120000),None);
  assert_eq!(c.poll(8,false,120006,120000),Some(Event::Stop{run:1,project:4,reason:Reason::Limit}));
  assert_eq!(c.poll(8,true,120007,120000),None);assert_eq!(c.poll(8,true,120008,120000),None);
 }
}
