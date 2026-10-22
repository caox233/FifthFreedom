//! Local mouse precision at input conversion and the native post-mode round trip.
use super::scis_diagnostics as diag;
use super::{memory::{patch, signature, word}, raw_mouse::Identity, rotation_precision::{self as policy, Owner, Precision}};
use std::{cell::{Cell,RefCell}, ffi::c_void, sync::atomic::{AtomicBool,AtomicU64, AtomicUsize, Ordering}};

const SITES: [usize; 4] = [0xd1d3a8, 0xd1d3e0, 0xda4664, 0xda4807];
static READY: AtomicBool = AtomicBool::new(false);
static THUNKS: [AtomicUsize; 4] = [const { AtomicUsize::new(0) }; 4];
static FRAME:AtomicU64=AtomicU64::new(0);
thread_local! {
    static STATE: RefCell<Precision> = RefCell::new(Precision::default());
    static STATE_FRAME:Cell<u64>=const {Cell::new(0)};
}
fn current_frame()->Option<u64> {
    let frame=STATE_FRAME.try_with(Cell::get).ok()?;
    (frame!=0 && frame==FRAME.load(Ordering::Acquire)).then_some(frame)
}
#[link(name="kernel32")] extern "system" {
    fn VirtualAlloc(p:*mut c_void,n:usize,t:u32,protect:u32)->*mut c_void;
    fn VirtualProtect(p:*mut c_void,n:usize,protect:u32,old:*mut u32)->i32;
    fn FlushInstructionCache(process:*mut c_void,p:*const c_void,n:usize)->i32;
    fn GetCurrentProcess()->*mut c_void;
    fn GetModuleHandleExW(flags:u32,address:*const u16,module:*mut usize)->i32;
}

fn inspect(id: Identity, read: impl Fn(usize,usize)->Option<u32>) -> Option<(Owner,bool)> {
    let core=read(id.mode,4)? as usize;
    let pc=read(core,0x20)? as usize;
    let world=read(pc,0x144)? as usize;
    let generation=read(pc,0x20)?;
    let device=read(pc,0x4d8)? as usize;
    let mouse_active=read(device,0x28)? & 4!=0;
    if core<0x10000 || pc<0x10000 || world<0x10000 || generation==0
        || read(core,0x2c)? as usize!=id.mode || read(core,0x24)? as usize!=id.pawn
        || read(id.pawn,0x938)? as usize!=pc || read(id.pawn,0x144)? as usize!=world
        || read(pc,0x520)? as usize!=id.pawn || read(pc,0x354)? as usize!=id.viewport
        || read(pc,0x60)? & 0x40000==0 {
        return None;
    }
    Some((Owner{viewport:id.viewport,window:id.window,pawn:id.pawn,mode:id.mode,core,world,generation},mouse_active))
}
// Called only by the sole raw-input owner, after its foreground/UI/possession
// gates and frame-boundary accounting. Focus loss, menus, OB, absolute input
// recovery and changed pawn/camera discard the fraction. The native mouse bit
// can be zero between reports: suspend correction without discarding ownership.
pub(super) fn prepare(id:Option<Identity>) {
    super::rotation_pipeline_native::new_frame();
    let previous=FRAME.fetch_add(1,Ordering::AcqRel);
    let frame=previous.wrapping_add(1);
    let owner=if READY.load(Ordering::Acquire) {
        id.and_then(|id| inspect(id,|p,o|unsafe{word(p,o)}))
    } else {None};
    let _=STATE.try_with(|s|{if let Ok(mut s)=s.try_borrow_mut(){
        if STATE_FRAME.try_with(Cell::get).ok()!=Some(previous) {s.bind(None);}
        s.bind_frame(owner.map(|(o,_)|o),owner.is_some_and(|(_,active)|active));
        let _=STATE_FRAME.try_with(|f|f.set(frame));
    }});
}
pub(super) fn installed()->bool {READY.load(Ordering::Acquire)}
/// Return only the same locally captured input owner and the current native
/// post-mode targets. Menu/focus/possession gates are reset by prepare(None).
pub(super) unsafe fn snapshot(core:usize)->Option<policy::Snapshot> {
    if !READY.load(Ordering::Acquire) { return None; }
    let frame=current_frame()?;
    let mode=word(core,0x2c)? as usize;
    let targets=[word(core,0xb8)? as i32,word(core,0xbc)? as i32];
    let mut token=STATE.try_with(|s|s.try_borrow().ok()?.snapshot(core,mode,targets)).ok().flatten()?;
    token.frame=frame;
    let o=token.owner;
    let id=Identity { viewport:o.viewport,window:o.window,pawn:o.pawn,mode:o.mode };
    let (current,_)=inspect(id,|p,o|word(p,o))?;
    (current==o && current_frame()==Some(frame)).then_some(token)
}
unsafe extern "system" fn correct(mode:usize, output:usize, axis:usize, sample:*mut f64) {
    if !READY.load(Ordering::Acquire)||current_frame().is_none() {return;}
    // sample points into our bridge's aligned stack FXSAVE image, never an
    // external game object. The native code already owns output and mode.
    let value=sample.read_unaligned();
    diag::hit(diag::PRECISION_CONVERT);
    let _=STATE.try_with(|s|{if let Ok(mut s)=s.try_borrow_mut(){sample.write_unaligned(s.correct(mode,output,axis,value));}});
}
unsafe extern "system" fn correct_final(core:usize, axis:usize, sample:*mut f64) {
    if !READY.load(Ordering::Acquire) || current_frame().is_none() || axis > 1 { return; }
    // Original code at this seam has already applied native mode constraints.
    // B4/A4 is the duration actually consumed, not a guessed input setting.
    let Some(mode)=word(core,0x2c) else { return; };
    let Some(target)=word(core,0xb8+axis*4) else { return; };
    let Some(duration)=word(core,if axis==0 {0xb4} else {0xa4}) else { return; };
    let value=sample.read_unaligned();
    diag::hit(diag::PRECISION_CONVERT);
    let _=STATE.try_with(|s|{if let Ok(mut s)=s.try_borrow_mut(){
        if !s.owns_mode(core,mode as usize) { return; }
        let corrected=s.roundtrip(core,axis,target as i32,f32::from_bits(duration),value);
        diag::sample(diag::PRECISION_ROUNDTRIP,||[axis as u32,target,duration,(value as f32).to_bits(),(corrected as f32).to_bits(),FRAME.load(Ordering::Relaxed) as u32,0,0,0,0,0,0,0,0,0,0]);
        sample.write_unaligned(corrected);
    }});
}
fn jump(site:usize,to:usize,len:usize)->Vec<u8> {
    let mut b=vec![0x90;len];b[0]=0xe9;b[1..5].copy_from_slice(&(to as u32).wrapping_sub(site as u32+5).to_le_bytes());b
}
fn originals(index:usize)->&'static [u8] {
    match index {0=>&policy::PITCH,1=>&policy::YAW,2=>&policy::FINAL_PITCH,_=>&policy::FINAL_YAW}
}
pub(super) unsafe fn init(world:usize) {
    if world!=0x589860 || READY.load(Ordering::Acquire) {return;}
    if !signature(0xd1d280,0x175,"e9f288f57d76190fd021b2c81e2fc49a3303487ba518bb573b64392dcc7b470c")
        || !signature(0xda4030,0x7fa,"865f56ff4962d002789565fd455b2639aeb5ce32cb8e3a95da5af33bbcfba8d1") {
        tracing::warn!("SCBL_CORE_MOUSE_PRECISION enabled=false reason=native-signature");return;
    }
    let mut module=0;
    if GetModuleHandleExW(5,correct as *const () as *const u16,&mut module)==0 {return;}
    for axis in 0..4 {
        let block=VirtualAlloc(std::ptr::null_mut(),128,0x3000,4);
        if block.is_null(){deinit();return;}
        THUNKS[axis].store(block as usize,Ordering::Release);
        let resume=(SITES[axis]+originals(axis).len()) as u32;
        let code=if axis<2 {
            policy::bridge(block as u32,correct as *const () as u32,resume,axis as u8)
        } else {
            policy::final_bridge(block as u32,correct_final as *const () as u32,resume,(axis-2) as u8)
        };
        std::ptr::copy_nonoverlapping(code.as_ptr(),block as *mut u8,code.len());
        let mut old=0;
        if VirtualProtect(block,128,0x20,&mut old)==0 || FlushInstructionCache(GetCurrentProcess(),block,code.len())==0
            || !patch(SITES[axis],originals(axis),&jump(SITES[axis],block as usize,originals(axis).len())) {
            deinit();tracing::warn!("SCBL_CORE_MOUSE_PRECISION enabled=false reason=bridge");return;
        }
    }
    READY.store(true,Ordering::Release);
    tracing::info!("SCBL_CORE_MOUSE_PRECISION enabled=true retained_fractional_rotator=true post_mode_roundtrip=true native_sensitivity=true smoothing=false");
}
pub(super) unsafe fn deinit() {
    READY.store(false,Ordering::Release);prepare(None);
    for axis in 0..4 {
        let thunk=THUNKS[axis].load(Ordering::Acquire);
        if thunk!=0 {let _=patch(SITES[axis],&jump(SITES[axis],thunk,originals(axis).len()),originals(axis));}
    }
    // Keep pinned bridge storage until process exit, including failed installs.
}

#[cfg(test)] mod tests {
    use super::*;use std::collections::HashMap;
    #[test] fn precision_requires_current_local_mouse_owner_and_world() {
        let id=Identity{viewport:0x10000,window:0x20000,pawn:0x30000,mode:0x40000};
        let m=HashMap::from([((id.mode,4),0x50000),((0x50000,0x20),0x60000),((0x50000,0x2c),id.mode as u32),((0x50000,0x24),id.pawn as u32),
            ((0x60000,0x144),0x70000),((0x60000,0x20),1),((0x60000,0x4d8),0x80000),((id.pawn,0x938),0x60000),((id.pawn,0x144),0x70000),
            ((0x60000,0x520),id.pawn as u32),((0x60000,0x354),id.viewport as u32),((0x60000,0x60),0x40000),((0x80000,0x28),4)]);
        let check=|m:&HashMap<_,_>|inspect(id,|p,o|m.get(&(p,o)).copied());assert!(check(&m).is_some());
        for key in m.keys(){let mut bad=m.clone();bad.remove(key);assert!(check(&bad).is_none(),"missing {key:?}");}
        for (key,value) in [((0x60000,0x60),0),((0x60000,0x20),0),((id.pawn,0x144),0x90000),((0x60000,0x520),0),((0x50000,0x2c),0)] {
            let mut bad=m.clone();bad.insert(key,value);assert!(check(&bad).is_none());
        }
        let mut idle=m.clone();idle.insert((0x80000,0x28),0);
        let moving=check(&m).unwrap();let idle=check(&idle).unwrap();
        assert_eq!(moving.0,idle.0);assert!(moving.1);assert!(!idle.1);
    }
}
