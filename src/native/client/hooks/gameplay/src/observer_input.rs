//! Synchronous main-DLL admission/input lease. This is not a second mouse hook.
//! Native addresses are identities only; each consumer must revalidate before use.
use hooks_plugin_api::observer::{InputFrame,ADMITTED,CAPTURE};
use crate::equipment_native::{readable,word};
use std::sync::{Mutex,atomic::{AtomicBool,Ordering}};

static INPUT: Mutex<Lease> = Mutex::new(Lease(None));
static PUBLISHED: AtomicBool=AtomicBool::new(false);
struct Lease(Option<InputFrame>);
impl Lease {
    fn publish(&mut self,mut frame:InputFrame)->bool {
        if let Some(previous)=self.0 {
            if same_context(previous,frame) && frame.now_ms>=previous.now_ms && frame.now_ms-previous.now_ms<100 {
                frame.mouse_x+=previous.mouse_x;frame.mouse_y+=previous.mouse_y;
            }
        }
        self.0=if frame.valid() && frame.flags==ADMITTED|CAPTURE
            && frame.mouse_x.abs()<=1_000_000. && frame.mouse_y.abs()<=1_000_000. {Some(frame)}else{None};
        self.0.is_some()
    }
    fn snapshot(&self,now:u64)->Option<InputFrame> {
        let f=self.0?;
        (now>=f.now_ms && now-f.now_ms<100).then_some(f)
    }
    fn take(&mut self,now:u64,expected:InputFrame,after:Option<u64>)->Option<InputFrame> {
        let mut f=self.snapshot(now)?;
        if !same_context(f,expected){return None;}
        // Several input callbacks can share one millisecond. Leave their counts
        // queued until the motion clock advances; repeated render passes get zero.
        if after.is_some_and(|last|f.now_ms<=last) {f.mouse_x=0.;f.mouse_y=0.;}
        else if let Some(stored)=self.0.as_mut(){stored.mouse_x=0.;stored.mouse_y=0.;}
        Some(f)
    }
}
fn same_context(a:InputFrame,b:InputFrame)->bool {
    a.world==b.world && a.controller==b.controller && a.generation==b.generation && a.viewport==b.viewport
}
#[link(name="kernel32")]
extern "system" {fn GetTickCount64()->u64;}

pub(crate) unsafe fn local_context(f:InputFrame)->bool {
    f.valid() && f.flags==ADMITTED|CAPTURE
        && crate::local_player::controller(f.world as usize)==Some(f.controller as usize)
        && word(f.controller as usize,0)==Some(0x02ac53ec)
        && word(f.controller as usize,0x20)==Some(f.generation)
        && word(f.controller as usize,0x354)==Some(f.viewport)
        && word(f.viewport as usize,0)==Some(0x02c4ea4c)
}
pub(crate) fn snapshot()->Option<InputFrame> {
    if !PUBLISHED.load(Ordering::Acquire){return None;}
    // Presentation lease only: copied and validated once per native input frame.
    // Do not rescan all controllers for every HUD widget. Native pointer writers
    // must still validate their own live identities immediately before writing.
    INPUT.try_lock().ok()?.snapshot(unsafe{GetTickCount64()})
}
pub(crate) fn active()->bool { snapshot().is_some() }
pub(crate) fn take(now:u64,expected:InputFrame,after:Option<u64>)->Option<InputFrame> {
    INPUT.try_lock().ok()?.take(now,expected,after)
}
pub(crate) fn clear() {
    PUBLISHED.store(false,Ordering::Release);
    if let Ok(mut lease)=INPUT.lock(){lease.0=None;}
    crate::observer_camera_runtime::pause();
}

#[no_mangle]
pub unsafe extern "system" fn scbl_observer_input_v1(frame:*const InputFrame,size:u32)->i32 {
    if size!=core::mem::size_of::<InputFrame>() as u32 || !readable(frame as usize,size as usize) {
        clear();return -1;
    }
    let f=frame.read_unaligned();
    if !f.valid() {clear();return -1;}
    if !crate::observer_hud::ready() || !crate::observer_camera_runtime::ready() || !crate::observer_pawn::ready()
        || f.flags!=ADMITTED|CAPTURE || !local_context(f)
        || GetTickCount64()<f.now_ms || GetTickCount64().saturating_sub(f.now_ms)>=100 {
        clear();return 0;
    }
    let Ok(mut lease)=INPUT.try_lock() else{return -2;};
    if !lease.publish(f) {PUBLISHED.store(false,Ordering::Release);return -1;}
    PUBLISHED.store(true,Ordering::Release);
    drop(lease); // Render takes Runtime -> INPUT. Never hold INPUT -> Runtime.
    if crate::observer_camera_runtime::is_presenting(f,GetTickCount64()){2}else{1}
}

#[cfg(test)]
mod tests {
    use super::*;
    fn frame()->InputFrame {InputFrame {now_ms:1000,world:0x10000,controller:0x20000,
        generation:3,viewport:0x30000,flags:ADMITTED|CAPTURE,..InputFrame::default()}}
    #[test] fn observer_input_keeps_all_counts_between_render_passes() {
        let mut lease=Lease(None);let mut f=frame();f.mouse_x=2.;f.mouse_y=-4.;
        assert!(lease.publish(f));f.now_ms=1001;f.mouse_x=3.;f.mouse_y=1.;
        assert!(lease.publish(f));let read=lease.snapshot(1001).unwrap();
        assert_eq!((read.mouse_x,read.mouse_y),(5.,-3.));
    }
    #[test] fn observer_input_consumes_once_and_retains_same_clock_counts_until_next_tick() {
        let mut lease=Lease(None);let mut f=frame();f.mouse_x=2.;lease.publish(f);
        assert_eq!(lease.take(1000,f,None).unwrap().mouse_x,2.);
        assert_eq!(lease.take(1000,f,Some(1000)).unwrap().mouse_x,0.);
        f.mouse_x=7.;lease.publish(f);
        assert_eq!(lease.take(1000,f,Some(1000)).unwrap().mouse_x,0.);
        f.now_ms=1001;f.mouse_x=11.;lease.publish(f);
        assert_eq!(lease.take(1001,f,Some(1000)).unwrap().mouse_x,18.);
        assert_eq!(lease.take(1001,f,Some(1001)).unwrap().mouse_x,0.);
        f.mouse_x=5.;lease.publish(f);let mut foreign=f;foreign.generation+=1;
        assert!(lease.take(1001,foreign,None).is_none());
        assert_eq!(lease.take(1001,f,None).unwrap().mouse_x,5.);
        f.mouse_x=9.;lease.publish(f);foreign.mouse_x=1.;lease.publish(foreign);
        assert_eq!(lease.take(1001,foreign,None).unwrap().mouse_x,1.);
        assert!(lease.take(1101,foreign,None).is_none());
    }
    #[test] fn observer_input_lease_expires_without_replaying_counts_after_focus_loss() {
        let mut lease=Lease(None);assert!(lease.publish(frame()));
        assert!(lease.snapshot(999).is_none());assert!(lease.snapshot(1099).is_some());
        assert!(lease.snapshot(1100).is_none());
        let mut f=frame();f.flags=ADMITTED;assert!(!lease.publish(f));
        assert!(lease.snapshot(1000).is_none());
        f=frame();f.now_ms=5000;assert!(lease.publish(f));assert_eq!(lease.snapshot(5000).unwrap().mouse_x,0.);
        assert!(!lease.publish(InputFrame::default()));assert!(lease.snapshot(5001).is_none());
    }
    #[test] fn observer_input_invalid_frame_revokes_previous_lease() {
        for fault in 0..5 {
            let mut lease=Lease(None);lease.publish(frame());let mut f=frame();
            match fault {0=>f.flags=CAPTURE,1=>f.controller=0,2=>f.generation=0,3=>f.mouse_x=f32::INFINITY,_=>f.abi=2};
            assert!(!lease.publish(f));assert!(lease.snapshot(1001).is_none());
        }
    }
    #[test] fn observer_input_native_context_rejects_reused_or_remote_controller() {
        let mut world=vec![0u32;0x700/4];let mut wi=vec![0u32;0x400/4];let mut pc=vec![0u32;0x800/4];
        let mut viewport=vec![0u32;0x940/4];let list=[pc.as_ptr() as u32];
        world[0x6d0/4]=wi.as_ptr() as u32;wi[0x36c/4]=list.as_ptr() as u32;wi[0x370/4]=1;wi[0x374/4]=1;
        pc[0]=0x02ac53ec;pc[0x20/4]=3;pc[0x144/4]=world.as_ptr() as u32;pc[0x60/4]=0x40000;
        pc[0x354/4]=viewport.as_ptr() as u32;viewport[0]=0x02c4ea4c;
        let f=InputFrame {world:world.as_ptr() as u32,controller:pc.as_ptr() as u32,viewport:viewport.as_ptr() as u32,..frame()};
        assert!(unsafe{local_context(f)});
        pc[0x20/4]=4;assert!(!unsafe{local_context(f)});pc[0x20/4]=3;
        pc[0x4c/4]=0x200;assert!(!unsafe{local_context(f)});pc[0x4c/4]=0;
        pc[0x60/4]=0;assert!(!unsafe{local_context(f)});
    }
}
