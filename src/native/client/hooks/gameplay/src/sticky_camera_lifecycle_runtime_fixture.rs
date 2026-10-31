//! Test-only access to the production whistle boundary. No hook is enabled and
//! no retail image is loaded. The original leaf is a synthetic thiscall sink.
use super::*;

pub(crate) unsafe fn initialize(original: unsafe extern "thiscall" fn(usize), sender: unsafe extern "thiscall" fn(usize)) {
    Whistle.initialize(original, whistle).expect("synthetic whistle trampoline");
    WhistleSend.initialize(sender, whistle_send).expect("synthetic send trampoline");
    READY.store(true, Ordering::Release);
}
pub(crate) fn run(camera: usize) { whistle(camera); }
pub(crate) unsafe fn destroy(camera: usize) { forget(camera); }
pub(crate) fn ready(enabled: bool) { READY.store(enabled, Ordering::Release); }
pub(crate) unsafe fn detonate(camera: usize) -> bool {
    device(camera).is_some_and(|owner| execute(owner, Command::Detonate))
}

// Called only from the fixed shared instrumentation in the runner's private
// test checkout. The shipped production files do not call these adapters.
#[derive(Clone,Copy)]
struct AddressBindings { info_slot:usize, camera_vtable:usize, leaves:[usize;4] }
thread_local! {static ADDRESSES:Cell<Option<AddressBindings>>=const{Cell::new(None)};}
pub(crate) fn bind_addresses(info_slot:usize,camera_vtable:usize,leaves:[usize;4]) {
    assert!(info_slot!=0&&camera_vtable!=0&&leaves.iter().all(|p|*p!=0));
    assert!(info_slot!=camera_vtable&&leaves.iter().all(|p|*p!=info_slot&&*p!=camera_vtable));
    ADDRESSES.with(|v|{assert!(v.get().is_none());v.set(Some(AddressBindings{info_slot,camera_vtable,leaves}));});
}
pub(crate) fn clear_addresses() {ADDRESSES.with(|v|v.set(None));}
pub(crate) fn info_slot()->usize {ADDRESSES.with(|v|v.get().expect("owned address binding").info_slot)}
pub(crate) unsafe fn read_word(address:usize,offset:usize)->Option<u32> {
    let translated=ADDRESSES.with(|v|match v.get(){
        Some(b) if address==0x339e5a0&&offset==0=>b.info_slot,
        Some(b) if address==0x02ab095c&&matches!(offset,0x7b0|0x7b4)=>b.camera_vtable,
        _=>address,
    });
    // Preserve the actual Windows VirtualQuery/range check and memory read.
    crate::equipment_native::word(translated,offset)
}
pub(crate) fn translated_read_is_active()->bool {
    super::word as *const () as usize==read_word as *const () as usize
}
pub(crate) fn native_address(address:usize)->usize {
    ADDRESSES.with(|v|match v.get(){
        Some(b)=>match address {0x544000=>b.leaves[0],0x6843d0=>b.leaves[1],
            0xad3ea0=>b.leaves[2],0xaa5280=>b.leaves[3],_=>address},
        None=>address,
    })
}
