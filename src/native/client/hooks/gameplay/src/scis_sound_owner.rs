//! Exact MP SC-IS shot ownership across the retail asynchronous audio graph.
//! Actor::PlaySound returns a BOOL, not a request ID. Register the command before
//! native queue publication; carry instance generations through observed context
//! copies. Never select shared SVU assets, a recycled actor, or a numeric ID alone.
#[path = "scis_sound_owner_policy.rs"] mod policy;
#[path = "scis_sound_range.rs"] mod range;
use policy::{Bank, Processor, Token};
use crate::equipment_native::{patch, readable, signature, word, writable};
use retour::static_detour;
use std::{cell::Cell, sync::{Mutex, atomic::{AtomicBool, Ordering}}};

static READY: AtomicBool = AtomicBool::new(false);
static BANK: Mutex<Bank> = Mutex::new(Bank::new());
const PLAY: usize = 0x544000;
const PLAY_SLOT: usize = 0x2a85b7c + 0x368;
const RELEVANCE_SLOT: usize = 0x2a85b7c + 0x384;
const RELEVANCE: usize = 0x6b5500;
const COPY_CALL: usize = 0x1ea1d4f;
const PERSIST_COPY: usize = 0x1e919a0;
const SUBMIT_CALL: usize = 0xa96fd8;
const PERSIST_ORIGINAL: [u8;5] = [0xb9,0x17,0,0,0];
type Play = unsafe extern "thiscall" fn(usize,usize,u32,f32,u32,u32)->u32;
type Factory = unsafe extern "C" fn(usize,usize,usize,usize,usize,usize)->usize;
#[derive(Clone, Copy)] struct Intent { weapon: usize, event: usize }
#[derive(Clone, Copy)] struct Submission { asset: u32, source: [u32;3] }
thread_local! {
    static INTENT: Cell<Option<Intent>> = const { Cell::new(None) };
    static SUBMITTING: Cell<Option<Submission>> = const { Cell::new(None) };
    static EXECUTING: Cell<Option<Token>> = const { Cell::new(None) };
    static COPYING: Cell<Option<Token>> = const { Cell::new(None) };
    static CONSTRUCTING: Cell<Option<Token>> = const { Cell::new(None) };
}
fn bank<R>(f: impl FnOnce(&mut Bank)->R) -> Option<R> { BANK.lock().ok().map(|mut b|f(&mut b)) }
fn scope<T:Copy+'static,R>(key:&'static std::thread::LocalKey<Cell<Option<T>>>,value:Option<T>,run:impl FnOnce()->R)->R {
    struct Reset<T:Copy+'static>(&'static std::thread::LocalKey<Cell<Option<T>>>,Option<T>);
    impl<T:Copy+'static> Drop for Reset<T> { fn drop(&mut self) { self.0.with(|v|v.set(self.1)); } }
    let _reset=Reset(key,key.with(|v|v.replace(value))); run()
}
fn context(p:usize)->Option<Token> { bank(|b|b.contexts.get(&p).copied()).flatten() }
fn owner(p:usize)->Option<Token> { bank(|b|b.emitters.get(&p).copied()).flatten() }
fn call(site:usize,target:usize)->[u8;5] {
    let mut p=[0xe8,0,0,0,0];p[1..].copy_from_slice(&(target as u32).wrapping_sub(site as u32+5).to_le_bytes());p
}
fn persistent_patch()->[u8;5] { let mut p=call(PERSIST_COPY,persistent_copy as usize);p[0]=0xe9;p }

static_detour! {
    static Enqueue: unsafe extern "thiscall" fn(usize,usize)->u32;
    static Execute: unsafe extern "thiscall" fn(usize)->u32;
    static CommandDestroy: unsafe extern "thiscall" fn(usize,u32)->usize;
    static VoiceCreate: unsafe extern "thiscall" fn(usize,usize,u32,usize,usize,u32,usize,usize,usize)->usize;
    static VoiceDestroy: unsafe extern "thiscall" fn(usize);
}
static_detour! {
    static CopyTree: unsafe extern "thiscall" fn(usize,usize,usize,usize)->u32;
    static DelayedUpdate: unsafe extern "C" fn(usize,usize)->u32;
    static ReleaseRef: unsafe extern "thiscall" fn(usize);
    static LayerCreate: unsafe extern "thiscall" fn(usize,usize,usize,usize,usize)->usize;
    static EmitterCreate: unsafe extern "thiscall" fn(usize,usize,u32)->usize;
    static EmitterDestroy: unsafe extern "thiscall" fn(usize);
    static BindConsumer: unsafe extern "thiscall" fn(usize,usize);
}
static_detour! {
    static ProcessorCreate: unsafe extern "thiscall" fn(usize)->usize;
    static ProcessorDestroy: unsafe extern "thiscall" fn(usize,u32)->usize;
    static ProcessorBaseDestroy: unsafe extern "thiscall" fn(usize);
    static ProcessPcm: unsafe extern "thiscall" fn(usize,usize,usize,usize,usize)->u32;
    static Attenuation: unsafe extern "thiscall" fn(usize,usize)->usize;
}

unsafe fn shot_intent(weapon:usize,event:usize)->Option<Intent> {
    if !READY.load(Ordering::Acquire) { return None; }
    let logic=word(weapon,0x748)? as usize;
    if !super::unsuppressed_copy(logic) || word(logic,0x28)!=Some(weapon as u32)
        || word(event,0)!=Some(0x029d5cb4) { return None; }
    let sound=word(logic,if word(logic,0xec)? & 1 != 0 {0x54}else{0x50})? as usize;
    if !super::sound_is(sound,super::SVU) { return None; }
    let end=[0x98,0xd4,0x10c,0x144,0x17c].into_iter()
        .any(|start|(0..9).any(|i|word(sound,start+i*4)==Some(event as u32)));
    end.then_some(Intent{weapon,event})
}
unsafe extern "thiscall" fn play(weapon:usize,event:usize,flags:u32,fade:f32,a:u32,b:u32)->u32 {
    // Call the original address, not a second detour: optional F5's existing
    // Actor::PlaySound detour remains in the chain. Fade is NOT gain.
    let native:Play=std::mem::transmute(PLAY);
    scope(&INTENT,shot_intent(weapon,event),||native(weapon,event,flags,fade,a,b))
}
unsafe extern "thiscall" fn relevance(actor:usize,event:usize,position:usize)->u32 {
    let native:unsafe extern "thiscall" fn(usize,usize,usize)->u32=std::mem::transmute(RELEVANCE);
    let exact=READY.load(Ordering::Acquire)&&INTENT.with(Cell::get)
        .is_some_and(|intent|intent.weapon==actor&&intent.event==event);
    range::relevance(actor,event,position,exact,||native(actor,event,position))
}
fn attenuation(subobject:usize,out:usize)->usize { unsafe {
    let token=if READY.load(Ordering::Acquire) {subobject.checked_sub(4).and_then(owner)}else{None};
    range::evaluate(subobject,out,token.map_or(0,|t|t.generation),||Attenuation.call(subobject,out))
} }
unsafe extern "C" fn submit(event_ref:usize,params:usize,actor:usize,event:usize)->u32 {
    // A96D20 resolved THIS exact UObject's backend event reference. The Send
    // function then reads *event_ref into command+18. A synchronous nearby
    // same-actor reload/impact must not inherit the shot merely from its actor.
    let submission=submission_for(event_ref,params,actor,event);
    let native:unsafe extern "C" fn(usize,usize)->u32=std::mem::transmute(0x1e16d90usize);
    // Keep the existing SEND detour (including drone callers) in the chain.
    scope(&SUBMITTING,submission,||native(event_ref,params))
}
unsafe fn submission_for(event_ref:usize,params:usize,actor:usize,event:usize)->Option<Submission> {
    let intent=INTENT.with(Cell::get).filter(|i|i.weapon==actor&&i.event==event);
    if READY.load(Ordering::Acquire)&&intent.is_some() {
        match (word(event_ref,0),word(params,0),word(params,4),word(params,8)) {
            (Some(asset),Some(kind),Some(body),Some(user)) if body==actor as u32 =>
                Some(Submission{asset,source:[kind,body,user]}),
            _=>None,
        }
    } else {None}
}
#[unsafe(naked)] unsafe extern "C" fn submit_call() {
    core::arch::naked_asm!(
        // Caller EBP is still A96D20's frame; EDI is its actual actor.
        "push dword ptr [ebp+0xc]","push edi",
        "push dword ptr [esp+0x10]","push dword ptr [esp+0x10]",
        "call {guard}","add esp,0x10","ret",guard=sym submit,
    );
}
fn enqueue(manager:usize,command:usize)->u32 { unsafe {
    let submission=SUBMITTING.with(Cell::get).filter(|s| READY.load(Ordering::Acquire)
        && word(manager,0)==Some(0x02ccebd8) && word(command,0)==Some(0x02cc8418)
        && word(command,0x18)==Some(s.asset)
        && (0..3).all(|i|word(command,0x20+i*4)==Some(s.source[i]))
        && word(command,0x28).is_some_and(|v|matches!(v,1|2))
        && word(command,0x2c).is_some_and(|v|v&5==5));
    let token=submission.and_then(|_|word(command,0x10)).and_then(|id|bank(|b|b.command(command,id)).flatten());
    if token.is_none() {bank(|b|{b.commands.remove(&command);});}
    // Native may execute and destroy this command on another thread before
    // this call returns. Never hold BANK, nor register after the native call.
    let result=Enqueue.call(manager,command);
    if result>=0x80000000 { bank(|b|{if b.commands.get(&command).copied()==token {b.commands.remove(&command);}}); }
    result
} }
fn execute(command:usize)->u32 { unsafe {
    let token=bank(|b|b.commands.get(&command).copied()).flatten();
    scope(&EXECUTING,token,||Execute.call(command))
} }
fn command_destroy(command:usize,flags:u32)->usize { unsafe {
    bank(|b|{b.commands.remove(&command);}); CommandDestroy.call(command,flags)
} }
fn voice_create(voice:usize,out:usize,kind:u32,event:usize,desc:usize,id:u32,state:usize,parent:usize,last:usize)->usize { unsafe {
    let token=if parent!=0 {context(parent)} else {EXECUTING.with(Cell::get).filter(|t|t.request==id)};
    bank(|b|{b.contexts.remove(&(voice+0x48));});
    let result=VoiceCreate.call(voice,out,kind,event,desc,id,state,parent,last);
    let valid=token.filter(|t| result==voice && word(out,0)==Some(0) && word(voice,0x5c)==Some(t.request));
    bank(|b|{Bank::put(&mut b.contexts,voice+0x48,valid);}); result
} }
fn voice_destroy(voice:usize) { unsafe { bank(|b|{b.contexts.remove(&(voice+0x48));}); VoiceDestroy.call(voice); } }
fn copy_tree(tree:usize,source:usize,a:usize,b:usize)->u32 { unsafe {
    scope(&COPYING,context(source),||CopyTree.call(tree,source,a,b))
} }
unsafe extern "C" fn copied_factory(out:usize,event:usize,a:usize,ctx:usize,b:usize,c:usize)->usize {
    let token=COPYING.with(Cell::get);
    let registered=bank(|state|Bank::put(&mut state.contexts,ctx,token)).unwrap_or(false);
    let native:Factory=std::mem::transmute(0x1ebd190usize);
    let result=native(out,event,a,ctx,b,c);
    if registered { bank(|state|{if state.contexts.get(&ctx).copied()==token {state.contexts.remove(&ctx);}}); }
    result
}
unsafe extern "C" fn register_delayed(object:usize,source:usize) {
    bank(|b|b.delayed_copy(object,source));
}
static PERSIST_RESUME:usize=0x1e919a5;
#[unsafe(naked)] unsafe extern "C" fn persistent_copy() {
    core::arch::naked_asm!(
        "pushfd","pushad","mov ebp,esp","sub esp,528","and esp,-16","fxsave [esp]",
        "push esi","push ebx","call {record}","add esp,8",
        "fxrstor [esp]","mov esp,ebp","popad","popfd",
        "mov ecx,0x17","jmp dword ptr [{resume}]",
        record=sym register_delayed,resume=sym PERSIST_RESUME,
    );
}
fn delayed_update(object:usize,source:usize)->u32 { unsafe {
    if word(object,0x20).is_some_and(|p|p!=0) { bank(|b|b.delayed_copy(object,source)); }
    DelayedUpdate.call(object,source)
} }
fn release_ref(reference:usize) { unsafe {
    // Only a registered container's exact base is a lifecycle event. Ordinary
    // resource references (including its +84 member) are left untouched.
    bank(|b|b.retire_delayed(reference)); ReleaseRef.call(reference)
} }
fn layer_create(layer:usize,out:usize,event:usize,ctx:usize,stop:usize)->usize { unsafe {
    scope(&CONSTRUCTING,context(ctx),||LayerCreate.call(layer,out,event,ctx,stop))
} }
fn emitter_create(emitter:usize,selector:usize,listener:u32)->usize { unsafe {
    bank(|b|{b.emitters.remove(&emitter);});
    let result=EmitterCreate.call(emitter,selector,listener);
    let token=CONSTRUCTING.with(Cell::get).filter(|_|result==emitter);
    bank(|b|{Bank::put(&mut b.emitters,emitter,token);}); result
} }
fn emitter_destroy(emitter:usize) { unsafe {
    bank(|b|{b.emitters.remove(&emitter);}); EmitterDestroy.call(emitter)
} }
fn processor_create(processor:usize)->usize { unsafe {
    bank(|b|{b.processors.remove(&processor);});
    let result=ProcessorCreate.call(processor);
    if result==processor && known_processor(processor) { bank(|b|b.processor_created(processor)); }
    result
} }
fn processor_destroy(processor:usize,flags:u32)->usize { unsafe {
    bank(|b|{b.processors.remove(&processor);}); ProcessorDestroy.call(processor,flags)
} }
fn processor_base_destroy(processor:usize) { unsafe {
    bank(|b|{b.processors.remove(&processor);}); ProcessorBaseDestroy.call(processor)
} }
unsafe fn known_processor(p:usize)->bool {word(p,0)==Some(0x02cd6610)&&word(p,0x24)==Some(0x02cd6608)}
fn bind_consumer(emitter:usize,descriptor:usize) { unsafe {
    if let Some(processor)=word(descriptor,8).and_then(|p|(p as usize).checked_sub(0x24)).filter(|p|known_processor(*p)) {
        // Before native publishes consumer into emitter+2c0. Every binder must
        // be eligible; an unknown binding permanently rejects this incarnation.
        bank(|b|b.bind(processor,emitter));
    }
    BindConsumer.call(emitter,descriptor)
} }
fn process_pcm(processor:usize,request:usize,read:usize,written:usize,extra:usize)->u32 { unsafe {
    let lease=if READY.load(Ordering::Acquire)&&known_processor(processor) {
        bank(|b|b.lease(processor)).flatten()
    } else {None};
    let requested=word(request,0); let output=word(request,4);
    let result=ProcessPcm.call(processor,request,read,written,extra);
    if let (Some(lease),Some(requested),Some(output))=(lease,requested,output) {
        post_pcm(processor,lease,result,requested,word(written,0),output as usize);
    }
    result
} }
unsafe fn post_pcm(processor:usize,lease:Processor,status:u32,requested:u32,produced:Option<u32>,out:usize) {
    let Some(channels)=word(processor,0x14) else{return;};
    let Some(stride)=word(processor,0x34) else{return;};
    let Some(count)=produced.and_then(|n|policy::pcm_length(status,requested,n,channels,stride)) else{return;};
    if count==0 || !known_processor(processor) || !writable(out,count*2) {return;}
    // Recheck the incarnation after the native call. Do not keep the registry
    // lock across any native operation, including producer or destructor calls.
    if !bank(|b|b.current(processor,lease)).unwrap_or(false) {return;}
    for i in 0..count {
        let p=(out+i*2) as *mut i16;
        p.write_unaligned(policy::pcm_sample(p.read_unaligned()));
    }
}

pub(super) unsafe fn init() {
    if READY.load(Ordering::Acquire){return;}
    if !PINS.iter().all(|&(p,n,h)|signature(p,n,h)) || word(PLAY_SLOT,0)!=Some(PLAY as u32)
        || word(RELEVANCE_SLOT,0)!=Some(RELEVANCE as u32) || !range::init() {
        tracing::error!("SCBL_SCIS_SOUND_OWNER enabled=false reason=signature");return;
    }
    macro_rules! install {($name:ident,$address:expr,$handler:ident,$ty:ty)=>{
        if $name.initialize(std::mem::transmute::<usize,$ty>($address),$handler).is_err() || $name.enable().is_err() {
            deinit();tracing::error!("SCBL_SCIS_SOUND_OWNER enabled=false reason=install hook={}",stringify!($name));return;
        }
    };}
    install!(Enqueue,0x1e8e6e0,enqueue,unsafe extern "thiscall" fn(usize,usize)->u32);
    install!(Execute,0x1e19c90,execute,unsafe extern "thiscall" fn(usize)->u32);
    install!(CommandDestroy,0x1dee050,command_destroy,unsafe extern "thiscall" fn(usize,u32)->usize);
    install!(VoiceCreate,0x1e8ea40,voice_create,unsafe extern "thiscall" fn(usize,usize,u32,usize,usize,u32,usize,usize,usize)->usize);
    install!(VoiceDestroy,0x1e9f480,voice_destroy,unsafe extern "thiscall" fn(usize));
    install!(CopyTree,0x1ea1be0,copy_tree,unsafe extern "thiscall" fn(usize,usize,usize,usize)->u32);
    install!(DelayedUpdate,0x1ebd590,delayed_update,unsafe extern "C" fn(usize,usize)->u32);
    install!(ReleaseRef,0x1e0d9d0,release_ref,unsafe extern "thiscall" fn(usize));
    install!(LayerCreate,0x1f56810,layer_create,unsafe extern "thiscall" fn(usize,usize,usize,usize,usize)->usize);
    install!(EmitterCreate,0x1eb9a90,emitter_create,unsafe extern "thiscall" fn(usize,usize,u32)->usize);
    install!(EmitterDestroy,0x1eaeef0,emitter_destroy,unsafe extern "thiscall" fn(usize));
    install!(BindConsumer,0x1e54980,bind_consumer,unsafe extern "thiscall" fn(usize,usize));
    install!(ProcessorCreate,0x1f34650,processor_create,unsafe extern "thiscall" fn(usize)->usize);
    install!(ProcessorDestroy,0x1f3d860,processor_destroy,unsafe extern "thiscall" fn(usize,u32)->usize);
    install!(ProcessorBaseDestroy,0x1f346d0,processor_base_destroy,unsafe extern "thiscall" fn(usize));
    install!(ProcessPcm,0x1f28d80,process_pcm,unsafe extern "thiscall" fn(usize,usize,usize,usize,usize)->u32);
    install!(Attenuation,0x1e84bd0,attenuation,unsafe extern "thiscall" fn(usize,usize)->usize);
    if !patch(SUBMIT_CALL,&call(SUBMIT_CALL,0x1e16d90),&call(SUBMIT_CALL,submit_call as usize))
        || !patch(COPY_CALL,&call(COPY_CALL,0x1ebd190),&call(COPY_CALL,copied_factory as usize))
        || !patch(PERSIST_COPY,&PERSIST_ORIGINAL,&persistent_patch())
        || !patch(PLAY_SLOT,&(PLAY as u32).to_le_bytes(),&(play as usize as u32).to_le_bytes())
        || !patch(RELEVANCE_SLOT,&(RELEVANCE as u32).to_le_bytes(),&(relevance as usize as u32).to_le_bytes()) {
        deinit();tracing::error!("SCBL_SCIS_SOUND_OWNER enabled=false reason=callsite-install");return;
    }
    READY.store(true,Ordering::Release);
    tracing::info!("SCBL_SCIS_SOUND_OWNER enabled=true amplitude_factor=1.5 range_factor=2 pcm=s16-saturating per_request_instance=true playback=unverified");
}
pub(super) unsafe fn deinit() {
    READY.store(false,Ordering::Release);
    let mine=(play as usize as u32).to_le_bytes();
    if readable(PLAY_SLOT,4)&&std::slice::from_raw_parts(PLAY_SLOT as *const u8,4)==mine {let _=patch(PLAY_SLOT,&mine,&(PLAY as u32).to_le_bytes());}
    let mine=(relevance as usize as u32).to_le_bytes();
    if readable(RELEVANCE_SLOT,4)&&std::slice::from_raw_parts(RELEVANCE_SLOT as *const u8,4)==mine {let _=patch(RELEVANCE_SLOT,&mine,&(RELEVANCE as u32).to_le_bytes());}
    for (site,installed,original) in [(SUBMIT_CALL,call(SUBMIT_CALL,submit_call as usize),call(SUBMIT_CALL,0x1e16d90)),(COPY_CALL,call(COPY_CALL,copied_factory as usize),call(COPY_CALL,0x1ebd190)),(PERSIST_COPY,persistent_patch(),PERSIST_ORIGINAL)] {
        if readable(site,5)&&std::slice::from_raw_parts(site as *const u8,5)==installed {let _=patch(site,&installed,&original);}
    }
    macro_rules! disable {($($name:ident),+)=>{$(if $name.is_enabled(){let _=$name.disable();})+};}
    disable!(Attenuation,ProcessPcm,ProcessorBaseDestroy,ProcessorDestroy,ProcessorCreate,BindConsumer,EmitterDestroy,EmitterCreate,LayerCreate,ReleaseRef,DelayedUpdate,CopyTree,VoiceDestroy,VoiceCreate,CommandDestroy,Execute,Enqueue);
    bank(Bank::clear);
}
// Generated from the exact locally inspected retail image; all checks run
// before the first hook is installed, including the two interior callsites.
include!("scis_sound_owner_pins.rs");
#[cfg(test)] #[path="scis_sound_owner_tests.rs"] mod tests;
