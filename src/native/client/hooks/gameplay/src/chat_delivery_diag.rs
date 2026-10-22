//! Opt-in transport boundary observer, never a chat relay or routing patch.
//! No text, FString buffer, sender identity, network address or save is read.
#![allow(dead_code)]
#[cfg(feature = "scis-runtime-diagnostics")]
mod native {
    use crate::equipment_native::{readable, signature, word};
    use crate::scis_diagnostics as diag;
    use retour::static_detour;
    use std::sync::atomic::{AtomicBool, Ordering};
    static ATTEMPTED: AtomicBool = AtomicBool::new(false);
    static READY: AtomicBool = AtomicBool::new(false);
    type SendFn = unsafe extern "thiscall" fn(usize,usize,usize,u32)->u8;
    type GateFn = unsafe extern "thiscall" fn(usize,usize,u32,u32,usize,usize,u32,u32,u32,u32,u32,u32)->usize;
    type TypedFn = unsafe extern "thiscall" fn(usize,usize);
    static_detour! {
        static Send: unsafe extern "thiscall" fn(usize,usize,usize,u32)->u8;
        static Gate: unsafe extern "thiscall" fn(usize,usize,u32,u32,usize,usize,u32,u32,u32,u32,u32,u32)->usize;
        static Typed: unsafe extern "thiscall" fn(usize,usize);
    }
    fn active()->bool { READY.load(Ordering::Acquire) && diag::active() }
    unsafe fn byte(p:usize,o:usize)->Option<u8>{
        let a=p.checked_add(o)?;
        readable(a,1).then(||(a as *const u8).read())
    }
    // Only the validated retail ChatString class. No payload follows this read.
    unsafe fn chat(p:usize)->bool {word(p,0)==Some(0x2b18344)}
    unsafe fn channel(p:usize)->u32 {
        match (byte(p,0x54),byte(p,0x64)) {
            (Some(255),Some(n)) if n!=0 => 3, // private MOD control, not user text
            (Some(_),Some(0))=>1,
            (Some(_),Some(_))=>2,
            _=>0,
        }
    }
    fn send(net:usize,name:usize,text:usize,only:u32)->u8 {
        unsafe {
            let watch=active();
            let role=if watch {word(net,0x14).map(|n|if n==0 {1}else{2}).unwrap_or(0)}else{0};
            let result=Send.call(net,name,text,only);
            if watch {
                diag::hit(diag::CHAT_SEND);
                diag::trace(diag::CHAT_SEND,||[1,role,u32::from(only!=0),u32::from(result),0,0,0,0,0,0,0,0,0,0,0,0]);
            }
            result // native AL, including native acceptance despite lower failure
        }
    }
    #[allow(clippy::too_many_arguments)]
    fn gate(manager:usize,out:usize,handle:u32,mode:u32,descriptor:usize,message:usize,destination:u32,a:u32,b:u32,lo:u32,hi:u32,c:u32)->usize {
        unsafe {
            // Private MOD heartbeat traffic must not exhaust user-chat row budget.
            let category=if active() && chat(message) {channel(message)}else{0};
            let watch=matches!(category,1|2);
            let result=Gate.call(manager,out,handle,mode,descriptor,message,destination,a,b,lo,hi,c);
            if watch {
                diag::hit(diag::CHAT_GATE);
                diag::trace(diag::CHAT_GATE,||{
                    let status=word(out,0);
                    [1,mode,category,status.unwrap_or(0),u32::from(status.is_some()),u32::from(result==out),0,0,0,0,0,0,0,0,0,0]
                });
            }
            result // exact pointer and output status; no retries or broadcasts
        }
    }
    fn typed(net:usize,message:usize){
        unsafe {
            let category=if active() && chat(message) {channel(message)}else{0};
            if matches!(category,1|2) {
                diag::hit(diag::CHAT_RECEIVE);
                diag::trace(diag::CHAT_RECEIVE,||[1,category,0,0,0,0,0,0,0,0,0,0,0,0,0,0]);
            }
            Typed.call(net,message); // exactly once, including non-chat messages
        }
    }
    pub(super) unsafe fn install_at(send_at:usize,gate_at:usize,typed_at:usize,fail_at:u32)->bool {
        if ATTEMPTED.swap(true,Ordering::AcqRel){return READY.load(Ordering::Acquire);}
        let result=(||{
            Send.initialize(std::mem::transmute::<usize,SendFn>(send_at),send).map_err(|_|())?;
            Gate.initialize(std::mem::transmute::<usize,GateFn>(gate_at),gate).map_err(|_|())?;
            Typed.initialize(std::mem::transmute::<usize,TypedFn>(typed_at),typed).map_err(|_|())?;
            if fail_at==1{return Err(());}
            Send.enable().map_err(|_|())?;
            if fail_at==2{return Err(());}
            Gate.enable().map_err(|_|())?;
            if fail_at==3{return Err(());}
            Typed.enable().map_err(|_|())?;
            Ok(())
        })();
        if result.is_err(){deinit();return false;}
        READY.store(true,Ordering::Release);true
    }
    pub(super) unsafe fn init(world:usize){
        if world!=0x589860 || ATTEMPTED.load(Ordering::Acquire){return;}
        // Init is after exposure bridge signature checks, before any chat input.
        let contracts=[
            (0x10b5110,319,"581ef3578c1fd05aa3f229bf89ff329f8bde8bce090fe9325daae99bda0183bc"),
            (0x2040350,2272,"f3e77e537c6c7e6255de3be7c0029eb0c241c5dc73569859d5e2f68f9e8eb962"),
            (0x104ee80,80,"3166ca744bfe6648a3601a6c815efa94d3302eb34f1066ea48ca4068a3db8de0"),
        ];
        if !contracts.iter().all(|&(p,n,h)|signature(p,n,h)) {
            ATTEMPTED.store(true,Ordering::Release);
            tracing::warn!("SCBL_CHAT_TRACE install_rejected=native_contract");return;
        }
        if install_at(0x10b5110,0x2040350,0x104ee80,0){
            tracing::info!("SCBL_CHAT_TRACE ready=true activation=F8 bounded_seconds=32 content_recording=false routing_changed=false");
        }else{tracing::warn!("SCBL_CHAT_TRACE install_rejected=detour routing_unchanged=true");}
    }
    pub(super) unsafe fn deinit(){
        READY.store(false,Ordering::Release);
        let _=Typed.disable();let _=Gate.disable();let _=Send.disable();
    }
}
pub(crate) unsafe fn init(world:usize){
    #[cfg(feature="scis-runtime-diagnostics")] native::init(world);
    #[cfg(not(feature="scis-runtime-diagnostics"))] let _=world;
}
pub(crate) unsafe fn deinit(){
    #[cfg(feature="scis-runtime-diagnostics")] native::deinit();
}
// Exposed only in isolated compile-time fixtures, never in distributed DLLs.
#[cfg(all(test,feature="scis-runtime-diagnostics"))]
pub(crate) unsafe fn fixture_install(s:usize,g:usize,t:usize,fail:u32)->bool {native::install_at(s,g,t,fail)}

#[cfg(test)]
mod tests {
    #[test]
    fn dedicated_transport_stages_do_not_reuse_graphics_slots(){
        use crate::scis_diagnostics as d;
        assert_eq!(d::NAMES[d::CHAT_SEND],"chat_send");
        assert_eq!(d::NAMES[d::CHAT_GATE],"chat_gate");
        assert_eq!(d::NAMES[d::CHAT_RECEIVE],"chat_receive");
        assert!(d::CHAT_SEND>=d::RETICLE_GPU_DETAIL+6);
        assert_eq!(d::CHAT_RECEIVE,d::CHAT_SEND+2);
    }
    #[test]
    fn unsupported_world_does_not_install_or_touch_game(){unsafe{super::init(0);super::deinit();}}
}
