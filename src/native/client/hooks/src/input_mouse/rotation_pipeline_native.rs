//! One enable gate for sub-integer view and actual shot propagation.
//! The native call always runs, even when identity/installation validation fails.
#[path="rotation_bone_direction.rs"] mod rotation_bone_direction;
use super::scis_diagnostics as diag;
use super::{memory::{patch,readable,word}, rotation_precision, rotation_precision_native,
    rotation_publication::Publication, rotation_publisher_bridge as publisher,
    rotation_view::{self,Published}, rotation_camera::{self,Composition}, rotation_patch_group};
pub(super) use rotation_patch_group::{Site,call,jump};
use std::{cell::RefCell,ffi::c_void,sync::{Mutex,atomic::{AtomicBool,AtomicU64,Ordering}}};

static READY:AtomicBool=AtomicBool::new(false);
static COMPOSED_FIRST:AtomicBool=AtomicBool::new(false);
static EPOCH:AtomicU64=AtomicU64::new(1);
static SITES:Mutex<Vec<Site>>=Mutex::new(Vec::new());
#[derive(Default)] struct RenderState {
    epoch:u64, scope:Publication, published:Option<Published>,
    compositions:[Composition;8], composing:Option<(usize,rotation_precision::Snapshot)>,
}
thread_local! {static RENDER:RefCell<RenderState>=RefCell::new(RenderState::default());}
fn render_state<T>(f:impl FnOnce(&mut RenderState)->T)->Option<T> {
    RENDER.try_with(|s|{let mut s=s.try_borrow_mut().ok()?;let epoch=EPOCH.load(Ordering::Acquire);
        if s.epoch!=epoch{*s=RenderState{epoch,..RenderState::default()};}Some(f(&mut s))}).ok().flatten()
}
#[link(name="kernel32")]extern "system" {
    fn VirtualAlloc(p:*mut c_void,n:usize,t:u32,protect:u32)->*mut c_void;
    fn VirtualProtect(p:*mut c_void,n:usize,protect:u32,old:*mut u32)->i32;
    fn GetCurrentProcess()->*mut c_void;
    fn FlushInstructionCache(process:*mut c_void,p:*const c_void,n:usize)->i32;
    fn GetModuleHandleExW(flags:u32,address:*const u16,module:*mut usize)->i32;
}
pub(super) fn active()->bool {READY.load(Ordering::Acquire)}
pub(super) unsafe fn allocate_code(make:impl FnOnce(u32)->Vec<u8>)->Option<usize> {
    let block=VirtualAlloc(std::ptr::null_mut(),4096,0x3000,4);
    if block.is_null(){return None;}
    let bytes=make(block as u32);
    if bytes.is_empty()||bytes.len()>4096{return None;}
    std::ptr::copy_nonoverlapping(bytes.as_ptr(),block as *mut u8,bytes.len());
    let mut old=0;
    if VirtualProtect(block,4096,0x20,&mut old)==0||FlushInstructionCache(GetCurrentProcess(),block,bytes.len())==0{return None;}
    Some(block as usize)
}
/// Called at the raw-input frame boundary and on focus/menu/identity reset.
/// No unfinished native scope or previously published camera crosses the frame.
pub(super) fn new_frame(){
    diag::poll_key();diag::hit(diag::INPUT_FRAME);
    EPOCH.fetch_add(1,Ordering::AcqRel);
    let _=render_state(|_|());
    super::rotation_aim_native::new_frame();
}
unsafe extern "system" fn begin(core:usize) {
    let token=if active(){rotation_precision_native::snapshot(core)}else{None};
    if token.is_some(){diag::hit(diag::PUBLISHER_BEGIN_TOKEN);}else{diag::hit(diag::PUBLISHER_BEGIN_MISSING);}
    let _=render_state(|s|{
        if !s.scope.busy(){s.published=None;}
        s.scope.begin(core,token);
        if let Some(i)=s.scope.slot(){s.compositions[i]=Composition::default();}
    });
}
unsafe extern "system" fn mark(core:usize,event:usize) {
    let _=render_state(|s|s.scope.mark(core,event));
}
unsafe extern "system" fn end(core:usize) {
    let current=if active(){rotation_precision_native::snapshot(core)}else{None};
    let _=render_state(|s|{
        let composed=s.scope.slot().map(|i|s.compositions[i]).unwrap_or_default();
        let token=s.scope.end(core,current);
        if token.is_some(){diag::hit(diag::PUBLISHER_END_TOKEN);}else{diag::hit(diag::PUBLISHER_END_MISSING);}
        if !s.scope.busy(){s.published=token.and_then(|t|Published::capture(t,Some(true),|p,o|word(p,o)));
            if let Some(p)=s.published.as_mut(){p.composed=composed.angles(p.token,p.rotation);
                if p.composed.is_some() && !COMPOSED_FIRST.swap(true,Ordering::Relaxed) {
                    tracing::info!("SCBL_CORE_MOUSE_CAMERA_COMPOSITION first_frame_accepted=true native_animation_preserved=true smoothing=false");
                }
            }
            if s.published.is_some(){diag::hit(diag::PUBLISHER_CAPTURED);}else{diag::hit(diag::PUBLISHER_CAPTURE_REJECT);}}
    });
}
// Only this call from the observed current-source publisher may start a precise
// head composition. Calls made by another pawn or another camera stay native.
unsafe extern "thiscall" fn compose(hands:usize,rotation:usize) {
    let context=if active(){render_state(|s|s.scope.current_input()).flatten()}else{None};
    let context=context.filter(|(_,t)|super::rotation_camera::owned_input(*t,hands,rotation,|p,o|word(p,o)));
    let saved=render_state(|s| {
        let old=s.composing;
        s.composing=context;
        if let Some((i,t))=context{s.compositions[i].bind(t);}
        (s.epoch,old)
    });
    let native:unsafe extern "thiscall" fn(usize,usize)=std::mem::transmute(0xafaa90usize);
    native(hands,rotation);
    if let Some((epoch,old))=saved {let _=render_state(|s|{if s.epoch==epoch{s.composing=old;}});}
}
// These three native calls are reached only inside AFA790. Even there,
// replacing translated-point subtraction requires the exact active publisher
// token, so preview/foreign/worker calls retain their original result.
unsafe extern "thiscall" fn head_direction(component:usize,out:usize,index:i32,input:usize)->usize {
    rotation_bone_direction::direction(component,out,index,input,||{
        if !active(){return None;}
        let token=render_state(|s|{
            let (i,t)=s.composing?;
            (s.scope.current_input()==Some((i,t))).then_some(t)
        }).flatten()?;
        (rotation_precision_native::snapshot(token.owner.core)==Some(token)).then_some(token)
    },|p,o|word(p,o),|p,n|readable(p,n),
        std::mem::transmute(0x1ce27d0usize),std::mem::transmute(0x6a25e0usize))
}
// Only AFA950 inside this owned composition may change the inverse actor basis.
unsafe extern "thiscall" fn camera_inverse(out:usize,p:i32,y:i32,r:i32)->usize {
    rotation_bone_direction::inverse_basis(out,[p,y,r],||{
        if !active(){return None;}
        let token=render_state(|s|{
            let (i,t)=s.composing?;
            (s.scope.current_input()==Some((i,t))).then_some(t)
        }).flatten()?;
        (rotation_precision_native::snapshot(token.owner.core)==Some(token)).then_some(token)
    },|p,o|word(p,o),|p,n|readable(p,n),std::mem::transmute(0x5355e0usize))
}
unsafe extern "thiscall" fn camera_input(out:usize,rotation:usize)->usize {
    let native:unsafe extern "thiscall" fn(usize,usize)->usize=std::mem::transmute(0x535200usize);
    let result=native(out,rotation);
    if !active() || result!=out || !readable(out,64) {return result;}
    let Some(r)=(||Some([word(rotation,0)? as i32,word(rotation,4)? as i32,word(rotation,8)? as i32]))()else{return result;};
    let matrix=render_state(|s| {
        let (i,t)=s.composing?;
        if s.scope.current_input()!=Some((i,t)){return None;}
        s.compositions[i].input(t,r)
    }).flatten();
    if let Some(m)=matrix{std::ptr::copy_nonoverlapping(m.as_ptr(),out as *mut f32,16);}
    result
}
unsafe extern "thiscall" fn camera_output(frame:usize,out:usize)->usize {
    let native:unsafe extern "thiscall" fn(usize,usize)->usize=std::mem::transmute(0x4f2510usize);
    let result=native(frame,out);
    if !active() || result!=out || !readable(frame,48) || !readable(out,12){return result;}
    let f=(frame as *const [f32;12]).read_unaligned();
    let r=(out as *const [i32;3]).read_unaligned();
    let _=render_state(|s| {
        let Some((i,t))=s.composing else{return;};
        if s.scope.current_input()==Some((i,t)){s.compositions[i].complete(t,f,r);}
    });
    result
}
unsafe fn render_angles(scene:usize)->Option<([f64;3],bool)> {
    let published=render_state(|s|{
        if s.scope.busy(){return None;}
        s.published
    }).flatten()?;
    let token=(rotation_precision_native::snapshot(published.token.owner.core)).or_else(||{diag::hit(diag::PUBLICATION_SNAPSHOT_REJECT);None})?;
    published.construction_angles(token,scene,|p,o|word(p,o)).map(|angles|(angles,published.composed.is_some()))
}
pub(super) unsafe fn scene_publication(scene:usize)->Option<(Published,rotation_precision::Snapshot)> {
    if !active() || super::rotation_aim_native::in_scope() { diag::hit(diag::PUBLICATION_INACTIVE_OR_AIM);return None; }
    let published=(render_state(|s|(!s.scope.busy()).then_some(s.published).flatten()).flatten()).or_else(||{diag::hit(diag::PUBLICATION_MISSING);None})?;
    let token=(rotation_precision_native::snapshot(published.token.owner.core)).or_else(||{diag::hit(diag::PUBLICATION_SNAPSHOT_REJECT);None})?;
    published.scene_angles(token,scene,|p,o|word(p,o))?;
    Some((published,token))
}
unsafe extern "system" fn view(scene:usize,output:usize) {
    if !active()||!readable(output,64){return;}
    // The scope-specific getter marks only this actual zero-origin aim scene.
    // A rejected aim scene must never fall through to a render-camera token.
    let aim=super::rotation_aim_native::in_scope();
    let (angles,kind)=if aim{
        (super::rotation_aim_native::view_angles(scene),3u32)
    }else{match render_angles(scene){Some((a,composed))=>(Some(a),if composed{2}else{1}),None=>(None,0)}};
    if let Some(angles)=angles {
        diag::hit(diag::VIEW_ACCEPTED);
        let aim=super::rotation_aim_native::in_scope();
        diag::trace(if aim{diag::VIEW_FRAME}else{diag::VIEW_RENDER_FRAME},||{let mut v=[0;16]; for i in 0..3{v[i]=word(scene,0x2e8+i*4).unwrap_or(0);v[i+3]=(angles[i] as f32).to_bits();}for i in 0..9{v[i+6]=word(output,i*4).unwrap_or(0);}v[15]=u32::from(aim);v});
        let values=rotation_view::matrix(angles);
        std::ptr::copy_nonoverlapping(values.as_ptr() as *const u8,output as *mut u8,64);
    }else{diag::hit(diag::VIEW_REJECTED);}
    // Record the value AFTER the precision overwrite, including rejected
    // frames. Epoch + scene join the context/basis rows; no inferred matrix.
    diag::trace(if aim{diag::FINAL_AIM_BASIS}else{diag::FINAL_RENDER_BASIS},||{
        let e=EPOCH.load(Ordering::Acquire);let mut v=[0;16];
        v[..4].copy_from_slice(&[e as u32,(e>>32)as u32,scene as u32,if angles.is_some(){kind}else{0}]);
        for (i,k) in [0,1,2,4,5,6,8,9,10].into_iter().enumerate(){v[4+i]=word(output,k*4).unwrap_or(0);}
        for i in 0..3{v[13+i]=word(scene,0x2e8+i*4).unwrap_or(0);}v
    });
    if !aim{diag::trace(diag::FINAL_RENDER_CONTEXT,||{
        let e=EPOCH.load(Ordering::Acquire);let mut v=[0;16];
        v[..4].copy_from_slice(&[e as u32,(e>>32)as u32,scene as u32,if angles.is_some(){kind}else{0}]);
        let a=angles.unwrap_or([f64::NAN;3]);for i in 0..3{v[4+i]=(a[i]as f32).to_bits();v[7+i]=word(scene,0x2a0+i*4).unwrap_or(0);}
        if let Some(p)=render_state(|s|s.published).flatten(){v[10]=p.camera as u32;v[11]=u32::from(p.composed.is_some());for i in 0..3{v[12+i]=p.rotation[i]as u32;}}
        v[15]=u32::from(active());v
    });}
}
unsafe fn plan()->Option<Vec<Site>> {
    let mut sites=Vec::new();
    let trampoline=allocate_code(|p|publisher::entry_trampoline(p,publisher::ENTRY+publisher::ENTRY_PIN.len() as u32))?;
    let wrapper=allocate_code(|p|publisher::scope_bridge(p,trampoline as u32,begin as *const () as u32,end as *const () as u32))?;
    sites.push(Site{address:publisher::ENTRY as usize,original:publisher::ENTRY_PIN.to_vec(),replacement:jump(publisher::ENTRY as usize,wrapper,publisher::ENTRY_PIN.len())});
    for s in [publisher::Site::Current,publisher::Site::Old,publisher::Site::Published] {
        let thunk=allocate_code(|p|publisher::marker_bridge(p,s,mark as *const () as u32,s.address()+s.pin().len() as u32))?;
        sites.push(Site{address:s.address() as usize,original:s.pin().to_vec(),replacement:jump(s.address() as usize,thunk,s.pin().len())});
    }
    let thunk=allocate_code(|p|rotation_precision::view_bridge(p,0x5355e0,view as *const () as u32,0x58feba))?;
    sites.push(Site{address:0x58feb5,original:vec![0xe8,0x26,0x57,0xfa,0xff],replacement:jump(0x58feb5,thunk,5)});
    for (at,target,replacement) in [
        (0xd60afe,0xafaa90,compose as *const () as usize),
        (0xafaaeb,0x535200,camera_input as *const () as usize),
        (0xafa98c,0x5355e0,camera_inverse as *const () as usize),
        (0xafab1f,0x4f2510,camera_output as *const () as usize),
        (0xafa832,0x1ce27d0,head_direction as *const () as usize),
        (0xafa8a8,0x1ce27d0,head_direction as *const () as usize),
        (0xafa8f2,0x1ce27d0,head_direction as *const () as usize),
    ] {sites.push(Site{address:at,original:call(at,target),replacement:call(at,replacement)});}
    sites.extend(super::rotation_aim_native::sites()?);
    Some(sites)
}
pub(super) unsafe fn init(world:usize) {
    if world!=0x589860||active(){return;}
    // Precision's native build gate must already have succeeded; no partial
    // final-view extension on an unknown binary or disabled input owner.
    if !rotation_precision_native::installed(){return;}
    if !rotation_bone_direction::native_matches(|p,bytes|readable(p,bytes.len())
        && std::slice::from_raw_parts(p as *const u8,bytes.len())==bytes){
        tracing::warn!("SCBL_CORE_MOUSE_VIEW enabled=false reason=head-direction-native");return;
    }
    let Ok(mut installed)=SITES.lock()else{return;};
    if !installed.is_empty(){return;}
    let mut module=0;
    if GetModuleHandleExW(5,view as *const () as *const u16,&mut module)==0{return;}
    let Some(sites)=plan()else{return;};
    if !rotation_patch_group::valid(&sites)||sites.iter().any(|s|!readable(s.address,s.original.len())
        ||std::slice::from_raw_parts(s.address as *const u8,s.original.len())!=s.original){
        tracing::warn!("SCBL_CORE_MOUSE_VIEW enabled=false reason=native-sites");return;
    }
    if !rotation_patch_group::publish(&sites,|p,a,b|patch(p,a,b)){
        *installed=sites;
        installed.retain(|s|!original_at(s));
        tracing::warn!("SCBL_CORE_MOUSE_VIEW enabled=false reason=publish");return;
    }
    *installed=sites;
    COMPOSED_FIRST.store(false,Ordering::Relaxed);
    new_frame();
    super::rotation_aim_native::set_active(true);
    READY.store(true,Ordering::Release);
    tracing::info!("SCBL_CORE_MOUSE_VIEW enabled=true exact_view_and_shot=true source_marker=true head_direction_precision=true smoothing=false");
}
pub(super) unsafe fn deinit(){
    READY.store(false,Ordering::Release);
    super::rotation_aim_native::set_active(false);
    new_frame();
    if let Ok(mut sites)=SITES.lock(){
        *sites=rotation_patch_group::restore(&sites,|p,a,b|patch(p,a,b));
        sites.retain(|s|!original_at(s));
    }
    // Generated code and this DLL stay pinned. In-flight native returns remain valid.
}
unsafe fn original_at(s:&Site)->bool {
    readable(s.address,s.original.len()) && std::slice::from_raw_parts(s.address as *const u8,s.original.len())==s.original
}
