//! The SC-IS render payload lives in PEC-Main.ass, not PEC-ADV.ass. Importing
//! the UObject under a bank missing that hash can leave an empty L3D resource.
//! Use the retail campaign bank only during exact equipment imports, then
//! restore the exact prior context (including no selected bank) before returning.
use crate::equipment_native::{readable, signature, word};
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
#[path = "scis_render_context.rs"]
mod context;
use context::Context;

static VERIFIED: AtomicBool = AtomicBool::new(false);
static LOGS: AtomicU32 = AtomicU32::new(0);
const CAMPAIGN: &[u8] = b"PEC-Main\0";
const MULTIPLAYER: &[u8] = b"PEC-ADV\0";
const UNSELECTED: &[u8] = b"\0";
type Select = unsafe extern "C" fn(*const u8, u32, u32, u32);
type Load = unsafe extern "C" fn(u32);
type Current = unsafe extern "C" fn() -> usize;

struct Native { prior_bank: usize, prior_flags: u32, prior_name: &'static [u8], active: bool }
impl Native {
    unsafe fn bank() -> usize {
        let f:Current=std::mem::transmute(0x1652a80usize);f()
    }
    unsafe fn selected(name: &[u8]) -> bool {
        let f:Current=std::mem::transmute(0x9c7820usize);let p=f();
        readable(p,name.len()) && std::slice::from_raw_parts(p as *const u8,name.len())==name
    }
    unsafe fn select(name:&[u8], flags:u32) {
        let f:Select=std::mem::transmute(0x9cc0b0usize);
        f(name.as_ptr(),0,(flags>>8)&0xff,(flags>>16)&0xff);
    }
}
impl Context for Native {
    fn enter_campaign(&mut self) -> bool {
        unsafe {
            if !VERIFIED.load(Ordering::Acquire) { return false; }
            let prior=Self::bank();
            // Equipment can resolve after the native preload scope has ended.
            // Empty name + null bank is a valid context, not a failed MP check.
            // Do not borrow an unrelated map bank or a nested campaign import.
            self.prior_name=if Self::selected(UNSELECTED) && prior==0 {
                UNSELECTED
            } else if Self::selected(MULTIPLAYER) && readable(prior,0x248) {
                MULTIPLAYER
            } else { return false; };
            let Some(flags)=word(0x339b3e8,0) else { return false; };
            self.prior_bank=prior;self.prior_flags=flags;self.active=true;
            // Mount via native AssetPack ownership. No UObject copy, manual
            // free, per-frame work, bank-pointer cache or asynchronous switch.
            Self::select(CAMPAIGN,0x100);
            let load:Load=std::mem::transmute(0x9cc1b0usize);load(0);
            let campaign=Self::bank();
            let loaded=campaign!=prior && readable(campaign,0x248)
                && *((campaign+0x244) as *const u8)!=0;
            if LOGS.fetch_add(1,Ordering::Relaxed)<12 {
                tracing::info!("SCBL_MOD_SCIS_RENDER_BANK stage=equipment-import campaign_ready={} prior_unselected={} native_ownership=true",loaded,self.prior_name==UNSELECTED);
            }
            if !loaded { self.restore(); }
            loaded
        }
    }
    fn restore(&mut self) {
        if !self.active { return; }
        self.active=false;
        unsafe {
            Self::select(self.prior_name,self.prior_flags);
            if Self::bank()!=self.prior_bank || !Self::selected(self.prior_name)
                || word(0x339b3e8,0)!=Some(self.prior_flags) {
                VERIFIED.store(false,Ordering::Release);
                tracing::error!("SCBL_MOD_SCIS_RENDER_BANK disabled reason=context-restore");
            }
        }
    }
}

pub(super) unsafe fn import<T>(enabled:bool, original:impl FnOnce()->T)->T {
    let mut native=Native{prior_bank:0,prior_flags:0,prior_name:UNSELECTED,active:false};
    context::import(&mut native,enabled,|_|original())
}
pub(super) unsafe fn campaign_loaded()->bool {
    let bank=Native::bank();
    VERIFIED.load(Ordering::Acquire) && Native::selected(CAMPAIGN)
        && readable(bank,0x248) && *((bank+0x244) as *const u8)!=0
}
pub(super) unsafe fn init()->bool {
    let ok=SIGNATURES.iter().all(|&(a,n,h)|signature(a,n,h));
    VERIFIED.store(ok,Ordering::Release);ok
}
pub(super) fn deinit() { VERIFIED.store(false,Ordering::Release); }
const SIGNATURES:&[(usize,usize,&str)]=&[
    (0x9cc0b0,251,"cfc2575bf38dbf298d5fa779d69e006c83ec340561428d7425f5a10994875c6c"),
    (0x9cc1b0,157,"12f99071ba642cb821fb034135bfe1a541e7e2bfd1c989104eb5256f37aadcf5"),
    (0x9c7820,20,"6cc56b3e2b3dc634fe56aeee97d618953d00ac2324a1789da4c66575f250464c"),
    (0x1652a80,6,"17d775a2c995999fb6de78c613efd4ea2c619039210d54c6cc2abac0f2dc4509"),
];
