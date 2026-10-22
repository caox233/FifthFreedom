use super::*;
static TEST_LOCK:Mutex<()>=Mutex::new(());
thread_local! {static CAPTURE:Cell<Option<Submission>>=const{Cell::new(None)};}
unsafe extern "C" fn capture_send(_:usize,_:usize)->u32 {
    CAPTURE.with(|v|v.set(SUBMITTING.with(Cell::get)));0x12345678
}

#[test]
#[ignore = "requires local pinned offline callback graph DLL; never launches game"]
fn source_bound_callbacks_cross_native_async_graph() { unsafe {
    let _lock=TEST_LOCK.lock().unwrap();
    #[link(name="kernel32")] extern "system" {
        fn LoadLibraryA(path:*const u8)->usize;
        fn GetProcAddress(module:usize,name:*const u8)->usize;
    }
    let file=std::ffi::CString::new(std::env::var("SCBL_AUDIO_CALLBACK_FIXTURE").unwrap()).unwrap();
    let m=LoadLibraryA(file.as_ptr().cast());assert_ne!(m,0);
    let get:unsafe extern "C" fn(u32)->usize=std::mem::transmute(GetProcAddress(m,b"chain_get\0".as_ptr()));
    let callbacks:unsafe extern "C" fn(*const usize)=std::mem::transmute(GetProcAddress(m,b"chain_callbacks\0".as_ptr()));
    let send:unsafe extern "C" fn(u32)->u32=std::mem::transmute(GetProcAddress(m,b"chain_send\0".as_ptr()));
    macro_rules! bridge {($name:ident,$func:ident,$($arg:ident),*)=>{
        unsafe extern "C" fn $name($($arg:usize),*)->usize {$func($($arg),*) as usize}
    };}
    bridge!(c0,enqueue,a,b);bridge!(c1,execute,a);
    unsafe extern "C" fn c2(a:usize,b:usize)->usize {command_destroy(a,b as u32)}
    unsafe extern "C" fn c3(a:usize,b:usize,c:usize,d:usize,e:usize,f:usize,g:usize,h:usize,i:usize)->usize {voice_create(a,b,c as u32,d,e,f as u32,g,h,i)}
    unsafe extern "C" fn c4(a:usize){voice_destroy(a)}
    bridge!(c5,layer_create,a,b,c,d,e);
    unsafe extern "C" fn c6(a:usize,b:usize,c:usize)->usize {emitter_create(a,b,c as u32)}
    unsafe extern "C" fn c7(a:usize){emitter_destroy(a)}
    unsafe extern "C" fn c8(a:usize,b:usize){register_delayed(a,b)}
    bridge!(c9,delayed_update,a,b);
    unsafe extern "C" fn c10(a:usize){release_ref(a)}
    unsafe extern "C" fn c11(a:usize,b:usize){bind_consumer(a,b)}
    bridge!(c12,processor_create,a);
    unsafe extern "C" fn c13(a:usize,b:usize)->usize {processor_destroy(a,b as u32)}
    bridge!(c14,process_pcm,a,b,c,d,e);
    unsafe extern "C" fn observe(kind:usize,p:usize)->usize {bank(|b|match kind{
        0=>b.contexts.contains_key(&p),1=>b.commands.contains_key(&p),2=>b.emitters.contains_key(&p),3=>b.processors.contains_key(&p),_=>false
    }).unwrap_or(false) as usize}
    callbacks([c0 as usize,c1 as usize,c2 as usize,c3 as usize,c4 as usize,c5 as usize,c6 as usize,c7 as usize,c8 as usize,c9 as usize,c10 as usize,c11 as usize,c12 as usize,c13 as usize,c14 as usize,0,observe as usize,0,0,0].as_ptr());
    assert_eq!(get(0),1);
    macro_rules! native {($name:ident,$address:expr,$handler:ident,$ty:ty)=>{
        $name.initialize(std::mem::transmute::<usize,$ty>(get($address)),$handler).unwrap();
    };}
    native!(Enqueue,0x1e8e6e0,enqueue,unsafe extern "thiscall" fn(usize,usize)->u32);
    native!(Execute,0x1e19c90,execute,unsafe extern "thiscall" fn(usize)->u32);
    native!(CommandDestroy,0x1dee050,command_destroy,unsafe extern "thiscall" fn(usize,u32)->usize);
    native!(VoiceCreate,0x1e8ea40,voice_create,unsafe extern "thiscall" fn(usize,usize,u32,usize,usize,u32,usize,usize,usize)->usize);
    native!(VoiceDestroy,0x1e9f480,voice_destroy,unsafe extern "thiscall" fn(usize));
    native!(DelayedUpdate,0x1ebd590,delayed_update,unsafe extern "C" fn(usize,usize)->u32);
    native!(ReleaseRef,0x1e0d9d0,release_ref,unsafe extern "thiscall" fn(usize));
    native!(LayerCreate,0x1f56810,layer_create,unsafe extern "thiscall" fn(usize,usize,usize,usize,usize)->usize);
    native!(EmitterCreate,0x1eb9a90,emitter_create,unsafe extern "thiscall" fn(usize,usize,u32)->usize);
    native!(EmitterDestroy,0x1eaeef0,emitter_destroy,unsafe extern "thiscall" fn(usize));
    native!(BindConsumer,0x1e54980,bind_consumer,unsafe extern "thiscall" fn(usize,usize));
    native!(ProcessorCreate,0x1f34650,processor_create,unsafe extern "thiscall" fn(usize)->usize);
    native!(ProcessorDestroy,0x1f3d860,processor_destroy,unsafe extern "thiscall" fn(usize,u32)->usize);
    native!(ProcessPcm,0x1f28d80,process_pcm,unsafe extern "thiscall" fn(usize,usize,usize,usize,usize)->u32);
    READY.store(true,Ordering::Release);bank(Bank::clear);
    let submission=Submission{asset:0x778899,source:[2,0x12345678,1]};
    for eligible in [true,false,true,false] {
        scope(&SUBMITTING,eligible.then_some(submission),||assert_eq!(send(eligible as u32),0));
        assert!(bank(|b|b.commands.is_empty()&&b.contexts.is_empty()&&b.delayed.is_empty()&&b.emitters.is_empty()&&b.processors.is_empty()).unwrap());
    }
    READY.store(false,Ordering::Release);bank(Bank::clear);
    println!("SOURCE_BOUND_CALLBACKS real_os_worker_before_send_return=true requests_same_id_reused_address=4 delayed_copy_after_voice_destroy=true unknown_update_clears=true final_pcm=12000_or_8000 registry=ACTUAL_PRODUCT device=NOT_TESTED");
} }
#[unsafe(naked)] unsafe extern "C" fn submit_fixture(_:usize,_:usize,_:usize,_:usize,_:usize)->u32 {
    core::arch::naked_asm!(
        "push ebp","push edi","sub esp,0x10","mov ebp,esp",
        "mov edi,[esp+0x1c]","mov eax,[esp+0x20]","mov [ebp+0xc],eax",
        "mov ecx,[esp+0x2c]","push dword ptr [esp+0x28]","push dword ptr [esp+0x28]",
        "call ecx","add esp,8","add esp,0x10","pop edi","pop ebp","ret",
    );
}

#[test]
#[ignore = "requires isolated fixed-address ABI fixture process; never launches game"]
fn native_submission_callsite_abi_binds_exact_event_and_preserves_result() { unsafe {
    let _lock=TEST_LOCK.lock().unwrap();
    #[link(name="kernel32")] extern "system" {
        fn VirtualAlloc(p:*mut std::ffi::c_void,n:usize,kind:u32,protect:u32)->*mut std::ffi::c_void;
        fn VirtualFree(p:*mut std::ffi::c_void,n:usize,kind:u32)->i32;
        fn GetCurrentProcess()->*mut std::ffi::c_void;
        fn FlushInstructionCache(h:*mut std::ffi::c_void,p:*const std::ffi::c_void,n:usize)->i32;
    }
    // This TEST PROCESS owns a private address reservation. No external process
    // is opened, started, or written. The real SEND detour is not installed here.
    let page=VirtualAlloc(0x1e10000 as _,0x10000,0x3000,0x40);assert_eq!(page as usize,0x1e10000);
    let mut stub=call(0x1e16d90,capture_send as usize);stub[0]=0xe9;
    std::ptr::copy_nonoverlapping(stub.as_ptr(),0x1e16d90 as *mut u8,5);
    assert_ne!(FlushInstructionCache(GetCurrentProcess(),0x1e16d90 as _,5),0);
    let intent=Intent{weapon:0x123456,event:0x654321};let asset=77u32;let source=[2u32,intent.weapon as u32,1];
    READY.store(true,Ordering::Release);
    scope(&INTENT,Some(intent),||{
        for event in [intent.event,intent.event+1,intent.event+2] {
            let result=submit_fixture(intent.weapon,event,&asset as *const u32 as usize,source.as_ptr() as usize,submit_call as usize);
            assert_eq!(result,0x12345678);let got=CAPTURE.with(Cell::get);
            assert_eq!(got.is_some(),event==intent.event);
            if let Some(got)=got{assert_eq!(got.asset,asset);assert_eq!(got.source,source);}
            assert!(SUBMITTING.with(Cell::get).is_none());
        }
    });
    READY.store(false,Ordering::Release);assert_ne!(VirtualFree(page,0,0x8000),0);
} }

#[test]
fn sound_scopes_restore_unknown_nested_thread_and_unwind() {
    let token=Token{request:9,generation:33};
    scope(&EXECUTING,Some(token),||{
        scope(&EXECUTING,None,||assert!(EXECUTING.with(Cell::get).is_none()));
        assert_eq!(EXECUTING.with(Cell::get),Some(token));
        std::thread::spawn(||assert!(EXECUTING.with(Cell::get).is_none())).join().unwrap();
    });
    assert!(EXECUTING.with(Cell::get).is_none());
    let _=std::panic::catch_unwind(||scope(&EXECUTING,Some(token),||panic!("controlled unwind")));
    assert!(EXECUTING.with(Cell::get).is_none());
}

#[test]
fn same_actor_reload_impact_or_nested_event_cannot_inherit_shot() { unsafe {
    let _lock=TEST_LOCK.lock().unwrap();READY.store(true,Ordering::Release);
    let intent=Intent{weapon:0x123456,event:0x654321};let asset=77u32;
    let source=[2u32,intent.weapon as u32,1];
    scope(&INTENT,Some(intent),||{
        let valid=submission_for(&asset as *const u32 as usize,source.as_ptr() as usize,intent.weapon,intent.event).unwrap();
        assert_eq!(valid.asset,77);assert_eq!(valid.source,source);
        for other_event in [0,0x654322,0x654323] {
            assert!(submission_for(&asset as *const u32 as usize,source.as_ptr() as usize,intent.weapon,other_event).is_none());
        }
        assert!(submission_for(&asset as *const u32 as usize,source.as_ptr() as usize,intent.weapon+1,intent.event).is_none());
        scope(&INTENT,None,||assert!(submission_for(&asset as *const u32 as usize,source.as_ptr() as usize,intent.weapon,intent.event).is_none()));
    });
    READY.store(false,Ordering::Release);
} }

#[test]
#[ignore = "requires controlled offline name-service fixture; never launches game"]
fn shot_intent_requires_exact_mp_scis_graph_and_single_shot_event() { unsafe {
    let _lock=TEST_LOCK.lock().unwrap();
    #[link(name="kernel32")] extern "system" {
        fn VirtualAlloc(p:*mut std::ffi::c_void,n:usize,kind:u32,protect:u32)->*mut std::ffi::c_void;
        fn VirtualFree(p:*mut std::ffi::c_void,n:usize,kind:u32)->i32;
    }
    // Private test-process world pointer; does not read any external game state.
    let page=VirtualAlloc(0x3390000 as _,0x10000,0x3000,0x04);assert_eq!(page as usize,0x3390000);
    let mut world=[0u32;0x334/4];let mut weapon=[0u32;0x74c/4];let mut logic=[0u32;0xf0/4];
    let mut props=[0u32;0x84/4];let mut sound=[0u32;0x1a0/4];let event=[0x029d5cb4u32,0];let other=[0x029d5cb4u32,0];
    let w=weapon.as_mut_ptr() as usize;let l=logic.as_mut_ptr() as usize;let e=event.as_ptr() as usize;
    *(0x339e5a0 as *mut u32)=world.as_mut_ptr() as u32;world[0x330/4]=0x10;world[0x144/4]=0x50000;
    weapon[0]=0x02a85b7c;weapon[0x144/4]=0x50000;weapon[0x740/4]=0x123;weapon[0x744/4]=props.as_mut_ptr() as u32;weapon[0x748/4]=l as u32;
    props[0]=0x02a176c4;logic[0x28/4]=w as u32;logic[0x2c/4]=props.as_mut_ptr() as u32;
    sound[0]=0x02a0a5ec;sound[0x28/4]=0x456;sound[0x98/4]=e as u32;
    logic[0x50/4]=sound.as_mut_ptr() as u32;logic[0x54/4]=sound.as_mut_ptr() as u32;
    READY.store(true,Ordering::Release);
    assert!(shot_intent(w,e).is_some());
    for kind in [0u32,1] {logic[0xec/4]=kind;assert!(shot_intent(w,e).is_some());}
    world[0x330/4]=0;assert!(shot_intent(w,e).is_none());world[0x330/4]=0x10; // campaign
    for not_scis in [0u32,0x124,0x125] {weapon[0x740/4]=not_scis;assert!(shot_intent(w,e).is_none());}
    weapon[0x740/4]=0x123;
    assert!(shot_intent(w,other.as_ptr() as usize).is_none()); // reload / impact not an End event
    weapon[0x144/4]=0x60000;assert!(shot_intent(w,e).is_none());weapon[0x144/4]=0x50000;
    logic[0x28/4]=0;assert!(shot_intent(w,e).is_none());logic[0x28/4]=w as u32;
    sound[0x28/4]=0;assert!(shot_intent(w,e).is_none());
    READY.store(false,Ordering::Release);assert_ne!(VirtualFree(page,0,0x8000),0);
} }

// Optional offline x86 fixture: the DLL contains relocated, signature-pinned
// retail PCM code and a controlled decoded source, not the game process. This
// runs the ACTUAL product hook/registry/post-output implementation. Ordinary
// contributors do not need retail binaries for the portable policy tests.
#[test]
#[ignore = "requires local pinned offline PCM fixture DLL; never launches game"]
fn source_bound_pcm_output() { unsafe {
    let _lock=TEST_LOCK.lock().unwrap();
    #[link(name="kernel32")] extern "system" {
        fn LoadLibraryA(path:*const u8)->usize;
        fn GetProcAddress(module:usize,name:*const u8)->usize;
    }
    let file=std::env::var("SCBL_AUDIO_OWNER_FIXTURE").expect("fixture DLL path required");
    let path=std::ffi::CString::new(file).unwrap();let module=LoadLibraryA(path.as_ptr().cast());assert_ne!(module,0);
    let get:unsafe extern "C" fn(u32)->usize=std::mem::transmute(GetProcAddress(module,b"fixture_get\0".as_ptr()));
    let reset:unsafe extern "C" fn(u32,u32,u32,i16)=std::mem::transmute(GetProcAddress(module,b"fixture_reset\0".as_ptr()));
    get(0); // initialize private relocation bank
    ProcessPcm.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize,usize,usize,usize,usize)->u32>(get(1)),process_pcm).unwrap();
    BindConsumer.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize,usize)>(get(6)),bind_consumer).unwrap();
    READY.store(true,Ordering::Release);
    let mut checks=0;
    // Overlapping SC-IS tails may share a native processor. Every binding is
    // individually eligible, so the final block needs one boost, not one per
    // request. A later unknown binding invalidates that whole incarnation.
    reset(1,1,8,8000);
    let processor=get(2);let out=get(3);
    let mut emitters=[[0u32;0x5d8/4];3];
    let pointers=emitters.each_mut().map(|e|e.as_mut_ptr() as usize);
    bank(|b|{
        b.clear();b.processor_created(processor);
        for i in 0..2 {let t=b.command(100+i,500+i as u32).unwrap();Bank::put(&mut b.emitters,pointers[i],Some(t));}
    });
    let descriptor=[0u32,0,(processor+0x24) as u32,0,0];
    let request=[8u32,out as u32,0];let mut read=0u32;let mut wrote=0u32;
    for &e in &pointers[..2] {bind_consumer(e,descriptor.as_ptr() as usize);}
    assert!(bank(|b|b.lease(processor)).flatten().is_some(),"both exact SC-IS shots remain eligible");
    for _ in 0..3 {
        assert_eq!(process_pcm(processor,request.as_ptr() as usize,&mut read as *mut u32 as usize,&mut wrote as *mut u32 as usize,0),0);
        assert_eq!(*(out as *const i16),12000);checks+=1;
    }
    bind_consumer(pointers[2],descriptor.as_ptr() as usize);
    assert!(bank(|b|b.lease(processor)).flatten().is_none());
    bind_consumer(pointers[0],descriptor.as_ptr() as usize);
    assert_eq!(process_pcm(processor,request.as_ptr() as usize,&mut read as *mut u32 as usize,&mut wrote as *mut u32 as usize,0),0);
    assert_eq!(*(out as *const i16),8000);checks+=1;
    bank(|b|b.processor_created(processor));
    bind_consumer(pointers[2],descriptor.as_ptr() as usize);
    bind_consumer(pointers[0],descriptor.as_ptr() as usize);
    bind_consumer(pointers[1],descriptor.as_ptr() as usize);
    assert!(bank(|b|b.lease(processor)).flatten().is_none());
    assert_eq!(process_pcm(processor,request.as_ptr() as usize,&mut read as *mut u32 as usize,&mut wrote as *mut u32 as usize,0),0);
    assert_eq!(*(out as *const i16),8000);checks+=1;
    println!("SHARED_ELIGIBLE_PCM exact_requests=2 output=12000 multiplier_once=true unknown_first_or_last=8000");
    for input in [1u32,2] {for output in [1u32,2] {
        if input==2&&output==1 {continue;}
        for frames in [1u32,8,16] {for sample in [0i16,8000,24000,32767] {
            reset(input,output,frames,sample);
            let processor=get(2);let out=get(3);let emitter=get(5);
            bank(|b|b.clear());let request=[frames,out as u32,0];let mut read=99u32;let mut wrote=99u32;
            let invoke=|read:&mut u32,wrote:&mut u32|process_pcm(processor,request.as_ptr() as usize,read as *mut u32 as usize,wrote as *mut u32 as usize,0);
            assert_eq!(invoke(&mut read,&mut wrote),0);assert_eq!(wrote,frames);
            let native=std::slice::from_raw_parts(out as *const i16,(frames*output) as usize).to_vec();
            let token=bank(|b|{let t=b.command(123,456).unwrap();Bank::put(&mut b.emitters,emitter,Some(t));b.processor_created(processor);t}).unwrap();
            let descriptor=[0u32,0,(processor+0x24) as u32,0,0];
            bind_consumer(emitter,descriptor.as_ptr() as usize);
            assert_eq!(bank(|b|b.lease(processor).unwrap().owner).flatten(),Some(token));
            for _ in 0..4 { // native overwrites the reused buffer before every post-gain
                assert_eq!(invoke(&mut read,&mut wrote),0);
                let actual=std::slice::from_raw_parts(out as *const i16,native.len());
                assert_eq!(actual,native.iter().copied().map(policy::pcm_sample).collect::<Vec<_>>());
                if input==1&&output==1&&frames==8&&sample==8000&&checks%7==0 {
                    println!("SOURCE_BOUND_PCM input_channels=1 output_channels=1 frames=8 native_sample={} product_sample={} amplitude_factor=1.5",native[0],actual[0]);
                }
                checks+=1;
            }
            bank(|b|{b.emitters.remove(&emitter);});
            bind_consumer(emitter,descriptor.as_ptr() as usize);
            assert!(bank(|b|b.lease(processor)).flatten().is_none());
            assert_eq!(invoke(&mut read,&mut wrote),0);
            assert_eq!(std::slice::from_raw_parts(out as *const i16,native.len()),native);
            // A new processor incarnation must not resurrect the old output lease.
            bank(|b|b.processor_created(processor));
            assert_eq!(invoke(&mut read,&mut wrote),0);
            assert_eq!(std::slice::from_raw_parts(out as *const i16,native.len()),native);
            assert_eq!(get(4),1); // output guards; no processor/source fields boosted
            checks+=3;
        }}
    }}
    READY.store(false,Ordering::Release);bank(Bank::clear);
    println!("PRODUCT_PCM_NATIVE checks={checks} owned=1.5_saturated unknown=unchanged reused_buffer=nonaccumulating output_guards=unchanged device=NOT_TESTED");
} }
