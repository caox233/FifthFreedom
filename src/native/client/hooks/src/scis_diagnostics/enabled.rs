use super::{DRAW_CONTINUITY,DRAW_CONTINUITY_END,ADS_LIFECYCLE_BEFORE,ADS_MOTION_STATE,ADS_MOTION,ADS_PARTIAL,ADS_PARTIAL_END,bundle_shape,NAMES,SECONDS,within_window,take_slot,CHAT_SEND,SOUND_REQUEST,ANIMATION_EVENT_BEFORE,ADS_PLACE_BEFORE};
use std::{sync::{Mutex,OnceLock,atomic::{AtomicBool,AtomicU8,AtomicU32,AtomicU64,Ordering}},time::Duration};
#[path="control.rs"] mod control;
use control::{Control,Event};
#[path="partial.rs"] mod partial;
#[derive(Clone,Copy,Default)]struct Slot{count:u32,sample:Option<(u32,[u32;16])>}
#[derive(Clone,Copy)]struct Session{run:u32,project:u8,start:u64}
struct Recorder{partial:partial::Window,session:Option<Session>,slots:Vec<Slot>,gate:Vec<(u32,u32)>,queued:Vec<u32>,rows:Vec<(usize,u32,[u32;16])>}
struct Batch{session:Session,slots:Vec<Slot>,rows:Vec<(usize,u32,[u32;16])>,dropped:u32,budget_dropped:u32}
impl Recorder{
 fn new()->Self{Self{partial:partial::Window::default(),session:None,slots:vec![Slot::default();NAMES.len()],gate:vec![(0,0);NAMES.len()],queued:vec![0;NAMES.len()],rows:Vec::with_capacity(NAMES.len()*32)}}
 fn begin(&mut self,s:Session){self.partial=partial::Window::default();self.session=Some(s);self.slots.fill(Slot::default());self.gate.fill((0,0));self.queued.fill(0);self.rows.clear();}
 fn drain(&mut self)->Option<Batch>{let session=self.session?;let slots=self.slots.clone();self.slots.fill(Slot::default());let rows=self.rows.drain(..).collect();self.queued.fill(0);Some(Batch{session,slots,rows,dropped:DROPPED.swap(0,Ordering::Relaxed),budget_dropped:BUDGET_DROPPED.swap(0,Ordering::Relaxed)})}
 fn allows(&self,id:usize)->bool{self.session.is_some_and(|s|id<NAMES.len()&&(match s.project {1|3=>id<CHAT_SEND,2=>id<CHAT_SEND||(DRAW_CONTINUITY..DRAW_CONTINUITY_END).contains(&id),4=>(CHAT_SEND..SOUND_REQUEST).contains(&id),5=>(SOUND_REQUEST..ANIMATION_EVENT_BEFORE).contains(&id),6=>(ANIMATION_EVENT_BEFORE..ADS_PLACE_BEFORE).contains(&id),7=>(ADS_PLACE_BEFORE..DRAW_CONTINUITY).contains(&id)||(ADS_LIFECYCLE_BEFORE..ADS_PARTIAL_END).contains(&id)||matches!(id,116..=118|121|124..=126),_=>false}))}
 // F7 needs adjacent transition frames, not the old first-16-rows sample.
 // Bound both per-second and queued rows; signal every capacity/budget rejection.
 fn limits(&self,id:usize)->(u32,u32){if self.session.is_some_and(|s|s.project==7||(s.project==2&&(DRAW_CONTINUITY..DRAW_CONTINUITY_END).contains(&id))){(256,512)}else{(16,32)}}
 fn room(&self,id:usize)->bool{self.allows(id)&&self.queued[id]<self.limits(id).1}
 fn push(&mut self,id:usize,second:u32,row:[u32;16]){self.queued[id]+=1;self.rows.push((id,second,row));}
 fn bundle<const N:usize>(&mut self,base:usize,second:u32,read:impl FnOnce()->[[u32;16];N])->Option<[[u32;16];N]>{
  if !bundle_shape(base,N)||(base..base+N).any(|id|!self.allows(id)){return None;}
  if (base..base+N).any(|id|!self.room(id)){BUDGET_DROPPED.fetch_add(1,Ordering::Relaxed);return None;}
  if !self.reserve(base,second){return None;}
  let rows=read();for(i,row)in rows.into_iter().enumerate(){self.push(base+i,second,row);}Some(rows)
 }
 fn motion(&mut self,second:u32,read:impl FnOnce()->[[u32;16];2],extra:impl FnOnce()->[[u32;16];5]){
  let Some(rows)=self.bundle(ADS_MOTION,second,read)else{return;};
  if !self.partial.eligible(&rows){return;}
  if self.partial.used>=partial::MAX_PACKETS{BUDGET_DROPPED.fetch_add(1,Ordering::Relaxed);return;}
  if self.bundle(ADS_PARTIAL,second,extra).is_some(){self.partial.used+=1;}
 }
 fn reserve(&mut self,id:usize,second:u32)->bool{
  if !self.allows(id){return false;}
  let limit=self.limits(id).0;
  if !self.room(id){BUDGET_DROPPED.fetch_add(1,Ordering::Relaxed);return false;}
  let(seen,count)=&mut self.gate[id];let ok=take_slot(second,seen,count,limit);
  if !ok{BUDGET_DROPPED.fetch_add(1,Ordering::Relaxed);}ok
 }
}
static DATA:OnceLock<Mutex<Recorder>>=OnceLock::new();
static PROJECT:AtomicU8=AtomicU8::new(0);
static ACTIVE:AtomicBool=AtomicBool::new(false);
static RUN:AtomicU32=AtomicU32::new(0);
static START:AtomicU64=AtomicU64::new(0);
static WORKER:AtomicU8=AtomicU8::new(0);
static DROPPED:AtomicU32=AtomicU32::new(0);
static BUDGET_DROPPED:AtomicU32=AtomicU32::new(0);
#[link(name="user32")]extern "system"{fn GetAsyncKeyState(key:i32)->i16;fn GetForegroundWindow()->usize;fn GetWindowThreadProcessId(window:usize,pid:*mut u32)->u32;}
#[link(name="kernel32")]extern "system"{fn GetCurrentProcessId()->u32;fn GetTickCount64()->u64;fn GetModuleHandleExW(flags:u32,address:*const u16,module:*mut usize)->i32;fn Beep(frequency:u32,duration:u32)->i32;}
fn now()->u64{unsafe{GetTickCount64()}}
pub(crate)fn active()->bool{ACTIVE.load(Ordering::Acquire)&&within_window(now().saturating_sub(START.load(Ordering::Relaxed)))}
pub(crate)fn project_active(p:u8)->bool{PROJECT.load(Ordering::Relaxed)==p&&active()}
fn record(id:usize,f:impl FnOnce(&mut Recorder,u32)){
 if !active(){return;}let run=RUN.load(Ordering::Relaxed);let Some(data)=DATA.get()else{return;};
 let Ok(mut d)=data.try_lock()else{DROPPED.fetch_add(1,Ordering::Relaxed);return;};
 let Some(s)=d.session else{return;};let elapsed=now().saturating_sub(s.start);
 if s.run!=run||!ACTIVE.load(Ordering::Acquire)||!within_window(elapsed)||!d.allows(id){return;}
 f(&mut d,(elapsed/1000)as u32+1);
}
pub(crate)fn hit(id:usize){record(id,|d,_|d.slots[id].count=d.slots[id].count.saturating_add(1));}
pub(crate)fn sample(id:usize,read:impl FnOnce()->[u32;16]){record(id,|d,second|{if d.slots[id].sample.is_none_or(|s|s.0!=second){d.slots[id].sample=Some((second,read()));}});}
pub(crate)fn trace(id:usize,read:impl FnOnce()->[u32;16]){record(id,|d,second|{if d.reserve(id,second){d.push(id,second,read());}});}
pub(crate)fn trace_bundle<const N:usize>(base:usize,read:impl FnOnce()->[[u32;16];N]){
 if !bundle_shape(base,N){return;}
 record(base,|d,second|{let _=d.bundle(base,second,read);});
}
// Same recorder/try_lock and fixed per-stage budgets as trace_bundle; no new worker.
// Existing two rows remain unchanged. Only admitted transition packets add five rows.
pub(crate)fn trace_motion_bundle(read:impl FnOnce()->[[u32;16];2],extra:impl FnOnce()->[[u32;16];5]){
 record(ADS_MOTION,|d,second|d.motion(second,read,extra));
}

fn emit(batch:Option<Batch>){
 let Some(b)=batch else{return;};let s=b.session;
 for(id,slot)in b.slots.into_iter().enumerate(){if slot.count!=0||slot.sample.is_some(){
  tracing::info!("SCBL_SCIS_DIAG module={} project={} run={} stage={} count={} sample={:?}",env!("CARGO_PKG_NAME"),control::name(s.project),s.run,NAMES[id],slot.count,slot.sample);
 }}
 for(id,sec,data)in b.rows{tracing::info!("SCBL_SCIS_DIAG_TRACE module={} project={} run={} second={} stage={} values={:?}",env!("CARGO_PKG_NAME"),control::name(s.project),s.run,sec,NAMES[id],data);}
 if b.dropped!=0||b.budget_dropped!=0{tracing::info!("SCBL_SCIS_DIAG_DROPPED module={} project={} run={} lock_contention={} budget_or_capacity={}",env!("CARGO_PKG_NAME"),control::name(s.project),s.run,b.dropped,b.budget_dropped);}
}
fn beep(frequency:u32){if env!("CARGO_PKG_NAME")=="hooks"{unsafe{Beep(frequency,100);}}}
fn event(event:Event,time:u64){
 match event{
  Event::Start{run,project}=>{
   if let Some(data)=DATA.get(){if let Ok(mut d)=data.lock(){
    ACTIVE.store(false,Ordering::Release);d.begin(Session{run,project,start:time});DROPPED.store(0,Ordering::Relaxed);BUDGET_DROPPED.store(0,Ordering::Relaxed);
    PROJECT.store(project,Ordering::Relaxed);RUN.store(run,Ordering::Relaxed);START.store(time,Ordering::Relaxed);ACTIVE.store(true,Ordering::Release);
   }}
   tracing::info!("SCBL_SCIS_DIAG_START module={} project={} run={} duration_seconds={} manual_toggle=true",env!("CARGO_PKG_NAME"),control::name(project),run,SECONDS);beep(900);
  },
  Event::Stop{run,project,reason}=>{
   ACTIVE.store(false,Ordering::Release);
   let batch=DATA.get().and_then(|m|m.lock().ok()).and_then(|mut d|{let b=d.drain();d.session=None;b});
   let elapsed=batch.as_ref().map(|b|time.saturating_sub(b.session.start)).unwrap_or(0);emit(batch);
   tracing::info!("SCBL_SCIS_DIAG_DONE module={} project={} run={} reason={} elapsed_ms={} no_game_writes=true",env!("CARGO_PKG_NAME"),control::name(project),run,reason.name(),elapsed);beep(1200);
  },
  Event::Busy{project,requested}=>{tracing::info!("SCBL_SCIS_DIAG_BUSY module={} active={} requested={} action=stop_active_key_first",env!("CARGO_PKG_NAME"),control::name(project),control::name(requested));beep(450);},
 }
}
pub(crate)fn poll_key(){
 if WORKER.compare_exchange(0,1,Ordering::AcqRel,Ordering::Relaxed).is_err(){return;}
 // Pin this already loaded module, as before. No game launch or injection.
 let mut module=0;if unsafe{GetModuleHandleExW(5,poll_key as *const () as *const u16,&mut module)}==0{WORKER.store(3,Ordering::Release);return;}
 DATA.get_or_init(||Mutex::new(Recorder::new()));
 match std::thread::Builder::new().name("scis-diag-toggle".into()).spawn(||{
  let mut controller=Control::default();let mut last_flush=now();
  tracing::info!("SCBL_SCIS_DIAG_READY module={} keys=F1_F2_F3_F4_F5_F6_F7 same_key_stops=true safety_seconds={}",env!("CARGO_PKG_NAME"),SECONDS);
  loop{
   let time=now();let mut pid=0;let mut keys=0u8;
   unsafe{GetWindowThreadProcessId(GetForegroundWindow(),&mut pid);for i in 0..7{if GetAsyncKeyState(0x70+i)&(0x8000u16 as i16)!=0{keys|=1<<i;}}}
   let focused=pid==unsafe{GetCurrentProcessId()};
   if let Some(e)=controller.poll(keys,focused,time,SECONDS as u64*1000){event(e,time);last_flush=time;}
   if time.saturating_sub(last_flush)>=1000{emit(DATA.get().and_then(|m|m.lock().ok()).and_then(|mut d|d.drain()));last_flush=time;}
   std::thread::sleep(Duration::from_millis(20));
  }
 }){Ok(_)=>WORKER.store(2,Ordering::Release),Err(e)=>{WORKER.store(3,Ordering::Release);tracing::error!("SCBL_SCIS_DIAG_ERROR module={} error={}",env!("CARGO_PKG_NAME"),e);}}
}

#[cfg(test)]mod tests{
 use super::*;
 #[test]fn sessions_reset_budgets_and_filter_projects(){
  let mut d=Recorder::new();d.begin(Session{run:1,project:1,start:0});
  for _ in 0..16{assert!(d.reserve(0,1));d.push(0,1,[1;16]);}
  assert!(!d.reserve(0,1));assert!(!d.reserve(CHAT_SEND,1));
  let first=d.drain().unwrap();assert_eq!(first.session.run,1);assert_eq!(first.rows.len(),16);assert!(!d.reserve(0,1));
  d.begin(Session{run:2,project:1,start:40});assert!(d.reserve(0,1));assert!(d.rows.is_empty());
  d.begin(Session{run:3,project:4,start:80});assert!(!d.reserve(0,1));assert!(d.reserve(CHAT_SEND,1));
  assert!(!d.reserve(SOUND_REQUEST,1));d.begin(Session{run:4,project:5,start:90});assert!(!d.reserve(0,1));assert!(!d.reserve(CHAT_SEND,1));assert!(d.reserve(SOUND_REQUEST,1));
  d.session=None;assert!(d.drain().is_none());assert!(!d.reserve(0,1));
 }
 #[test]fn row_capacity_never_crosses_stage_boundary(){
  let mut d=Recorder::new();d.begin(Session{run:1,project:2,start:0});
  for sec in 1..=2{for _ in 0..16{assert!(d.reserve(0,sec));d.push(0,sec,[0;16]);}}
  assert!(!d.reserve(0,3));assert!(d.reserve(1,3));assert!(!d.reserve(NAMES.len(),3));
 }
}

#[cfg(test)]mod observer_tests{
 use super::*;
 #[test]fn projects_do_not_leak_and_f7_is_frame_bounded(){
  let mut d=Recorder::new();assert!(!d.allows(147));
  d.begin(Session{run:1,project:5,start:0});assert!(!d.allows(142));
  d.begin(Session{run:2,project:6,start:0});assert!(d.allows(142));assert!(d.allows(146));assert!(!d.allows(147));assert!(!d.allows(137));
  d.begin(Session{run:3,project:7,start:0});assert!(d.allows(150));assert!(d.allows(121));assert!(!d.allows(137));assert!(!d.allows(142));
  for _ in 0..256{assert!(d.reserve(147,1));d.push(147,1,[0;16]);}
  assert!(!d.reserve(147,1));let b=d.drain().unwrap();assert_eq!(b.rows.len(),256);assert!(b.budget_dropped>0);
  assert!(!d.reserve(147,1));assert!(d.reserve(147,2));
 }
 #[test]fn bundles_cannot_overrun_queued_capacity(){
  let mut d=Recorder::new();d.begin(Session{run:1,project:7,start:0});
  for sec in 1..=2{for _ in 0..256{assert!(d.reserve(147,sec));for id in 147..150{d.push(id,sec,[0;16]);}}}
  for id in 147..150{assert!(!d.room(id));}assert!(!d.reserve(147,3));assert!(d.reserve(150,3));
  assert_eq!(d.drain().unwrap().rows.len(),1536);assert!(d.room(147));
 }
}

// Test-only session entry; never starts a worker, polls keys or launches a game.
#[cfg(test)]static TEST_SESSION:Mutex<()>=Mutex::new(());
#[cfg(test)]pub(crate) fn test_record(project:u8,f:impl FnOnce())->Vec<(usize,u32,[u32;16])>{
    let _serial=TEST_SESSION.lock().unwrap();
    let time=now();let data=DATA.get_or_init(||Mutex::new(Recorder::new()));
    ACTIVE.store(false,Ordering::Release);data.lock().unwrap().begin(Session{run:999,project,start:time});
    PROJECT.store(project,Ordering::Relaxed);RUN.store(999,Ordering::Relaxed);START.store(time,Ordering::Relaxed);ACTIVE.store(true,Ordering::Release);
    f();ACTIVE.store(false,Ordering::Release);let mut d=data.lock().unwrap();let rows=d.drain().unwrap().rows;d.session=None;rows
}
#[cfg(test)]mod continuity_tests{
 use super::*;
 #[test]fn new_stage_is_f2_only_and_does_not_raise_old_limits(){
  let mut d=Recorder::new();for project in 1..=7{d.begin(Session{run:1,project,start:0});
   assert_eq!(d.allows(DRAW_CONTINUITY),project==2);assert_eq!(d.allows(ADS_LIFECYCLE_BEFORE),project==7);
   if project==2{assert_eq!(d.limits(0),(16,32));assert_eq!(d.limits(DRAW_CONTINUITY),(256,512));}
  }
 }
 #[test]fn bundle_shapes_are_exact_and_cannot_cross_new_project_ranges(){
  assert!(bundle_shape(DRAW_CONTINUITY,10));assert!(!bundle_shape(DRAW_CONTINUITY,9));assert!(!bundle_shape(DRAW_CONTINUITY+1,10));
  assert!(!bundle_shape(DRAW_CONTINUITY-1,2));assert!(!bundle_shape(ADS_MOTION_STATE,2));assert!(bundle_shape(163,2));assert!(!bundle_shape(128,9));
 }
 #[test]fn inactive_wrong_project_and_exhausted_budget_never_read_bundle(){
  assert!(test_record(0,||trace_bundle::<10>(DRAW_CONTINUITY,||panic!("inactive project"))).is_empty());
  assert!(test_record(7,||trace_bundle::<10>(DRAW_CONTINUITY,||panic!("F7 cannot read F2"))).is_empty());
  let reads=std::cell::Cell::new(0);let rows=test_record(2,||{
   for _ in 0..300{trace_bundle::<10>(DRAW_CONTINUITY,||{reads.set(reads.get()+1);[[0;16];10]});}
   trace_bundle::<9>(DRAW_CONTINUITY,||panic!("bad shape"));
  });assert_eq!(reads.get(),256);assert_eq!(rows.len(),2560);
 }
 #[test]fn capacity_and_drop_reporting_remain_bounded_and_atomic(){
  let mut d=Recorder::new();d.begin(Session{run:1,project:2,start:0});
  for sec in 1..=2{for _ in 0..256{assert!(d.reserve(DRAW_CONTINUITY,sec));for id in DRAW_CONTINUITY..DRAW_CONTINUITY_END{d.push(id,sec,[0;16]);}}}
  assert!(!d.reserve(DRAW_CONTINUITY,3));let b=d.drain().unwrap();assert_eq!(b.rows.len(),5120);assert!(b.budget_dropped>0);
  assert!(d.reserve(DRAW_CONTINUITY,3));assert_eq!(d.limits(0),(16,32));
 }
}

#[cfg(test)]mod partial_tests{
 use super::*;use std::cell::Cell;
 fn rows(frame:u32,alpha:f32)->[[u32;16];2]{let mut r=[[0;16];2];r[0][0]=1;r[1][0]=1;r[0][1]=7;r[1][1]=7;r[0][3]=1;r[0][4]=frame;r[1][2]=(1<<3)|(1<<4);r[1][3]=0x400000;r[1][4]=alpha.to_bits();r}
 #[test]fn hard_run_cap_survives_drains_and_new_run_resets(){
  let mut d=Recorder::new();d.begin(Session{run:1,project:7,start:0});let reads=Cell::new(0);let mut all=0;
  for i in 0..200{d.motion(i/50+1,||rows(i,0.5),||{reads.set(reads.get()+1);[[i;16];5]});if i%20==19{all+=d.drain().unwrap().rows.len();}}
  assert_eq!(reads.get(),128);assert_eq!(all,200*2+128*5);assert_eq!(d.partial.used,128);
  d.begin(Session{run:2,project:7,start:0});d.motion(1,||rows(200,0.5),||{reads.set(reads.get()+1);[[0;16];5]});assert_eq!(reads.get(),129);
 }
 #[test]fn rejected_packet_never_reads_extra_and_original_stream_continues(){
  let mut d=Recorder::new();d.begin(Session{run:1,project:7,start:0});
  d.motion(1,||rows(1,1.),||panic!("steady must not read"));assert_eq!(d.rows.len(),2);
  d.queued[ADS_PARTIAL]=512;d.motion(1,||rows(2,0.5),||panic!("full must not read"));assert_eq!(d.rows.len(),4);
  assert_eq!(d.partial.used,0);d.queued[ADS_PARTIAL]=0;d.gate[ADS_PARTIAL]=(1,256);
  d.motion(1,||rows(3,0.5),||panic!("budget must not read"));assert_eq!(d.partial.used,0);assert_eq!(d.rows.len(),6);
  d.begin(Session{run:2,project:2,start:0});d.motion(1,||panic!("wrong project base"),||panic!("wrong project extra"));
 }
 #[test]fn closed_and_wrong_project_do_not_call_either_closure(){
  assert!(test_record(0,||trace_motion_bundle(||panic!("closed base"),||panic!("closed extra"))).is_empty());
  assert!(test_record(2,||trace_motion_bundle(||panic!("F2 base"),||panic!("F2 extra"))).is_empty());
 }
}
