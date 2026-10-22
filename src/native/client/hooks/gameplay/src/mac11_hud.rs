//! The weapon-swap HUD reads EWeaponArchetype+34 (m_InventoryIcon), not the
//! inventory slot. Override only that consumer's FName; never reclassify a gun
//! or rewrite a shared archetype to obtain a different picture.
use crate::equipment_native::{patch, readable, signature, word};
use std::sync::atomic::{AtomicBool, Ordering};
static READY: AtomicBool = AtomicBool::new(false);
// After the native selected-weapon cache check: unchanged HUD frames must not
// pay for identity/FName resolution on every frame.
const SITE: usize = 0x1a4f110;
const ORIGINAL: [u8;8] = [0x8b,0x45,0x08,0x68,0x80,0,0,0];
const PISTOL: &[u8] = b"ADV_WF_Pistol";
const SMG: &[u8] = b"ADV_WF_SMG";
const MAC: &[u8] = b"ADV_Spy_SMG_MAC11";

fn eligible(enabled: bool, weapon: usize, icon: u32,
    read: impl Fn(usize,usize)->Option<u32>, named: impl Fn(u32,&[u8])->bool) -> bool {
    let check = || -> Option<bool> {
        if !enabled || !named(icon,SMG) { return Some(false); }
        let info = read(0x339e5a0,0)? as usize;
        let world = read(info,0x144)?;
        let props = read(weapon,0x744)? as usize;
        let logic = read(weapon,0x748)? as usize;
        Some(world>=0x10000 && read(info,0x330)?&0x10!=0
            && read(weapon,0)?==0x02a85b7c && read(weapon,0x144)?==world
            && named(read(weapon,0x740)?,MAC)
            && read(props,0)?==0x02a176c4 && named(read(props,0x28)?,MAC)
            && read(props,0x34)?==icon && read(props,0x38)?>>24==2
            && read(logic,0x28)? as usize==weapon
            && read(logic,0x2c)? as usize==props)
    };
    check()==Some(true)
}
unsafe extern "C" fn display_icon(weapon:usize,original:u32)->u32 {
    if !eligible(READY.load(Ordering::Acquire),weapon,original,
        |p,o|word(p,o),|n,s|super::native_name_is(n,s)) { return original; }
    let mut name=0u32;
    let construct:unsafe extern "thiscall" fn(usize,usize,u32,u32)->usize=
        std::mem::transmute(0x4c7d80usize);
    construct(&mut name as *mut u32 as usize,b"ADV_WF_Pistol\0".as_ptr()as usize,1,0);
    if name!=0 && super::native_name_is(name,PISTOL) { name } else { original }
}
static CONTINUE: usize=0x1a4f118;
#[unsafe(naked)]
unsafe extern "C" fn icon_branch(){
    core::arch::naked_asm!(
        // EBP+8 contains the original icon; EDI is this HUD update's weapon.
        // Original MOV/PUSH preserve flags. Keep every other live register and
        // FPU/SIMD state across the Rust/native-name call, including original EBP.
        "mov eax,[ebp+8]", "pushfd", "pushad", "mov ebp,esp",
        "sub esp,528", "and esp,-16", "fxsave [esp]",
        "push eax", "push edi", "call {select}", "add esp,8",
        "mov [ebp+28],eax", "fxrstor [esp]", "mov esp,ebp", "popad", "popfd",
        "push 0x80", "jmp dword ptr [{next}]",
        select=sym display_icon,next=sym CONTINUE,
    );
}
fn installed()->[u8;8]{
    let mut b=[0xe9,0,0,0,0,0x90,0x90,0x90];
    b[1..5].copy_from_slice(&(icon_branch as usize as u32).wrapping_sub(SITE as u32+5).to_le_bytes());b
}
pub(super) unsafe fn init(){
    if READY.load(Ordering::Acquire){return;}
    for &(a,n,h) in SIGNATURES {
        if !signature(a,n,h){tracing::error!("SCBL_MOD_MAC11_HUD enabled=false reason=signature site={a:x}");return;}
    }
    if !patch(SITE,&ORIGINAL,&installed()){
        tracing::error!("SCBL_MOD_MAC11_HUD enabled=false reason=callsite");return;
    }
    READY.store(true,Ordering::Release);
    tracing::info!("SCBL_MOD_MAC11_HUD enabled=true image=ADV_WF_Pistol scope=MP-exact-MAC11 archetype=unchanged");
}
pub(super) unsafe fn deinit(){
    READY.store(false,Ordering::Release);
    if readable(SITE,8)&&std::slice::from_raw_parts(SITE as *const u8,8)==installed()
        && !patch(SITE,&installed(),&ORIGINAL){
        tracing::error!("SCBL_MOD_MAC11_HUD restore_failed site=1a4f110");
    }
}
const SIGNATURES:&[(usize,usize,&str)]=&[
    (0x1a4f0b0, 170, "cdb12cdef2577bd6b951dfd7ad4c6efb47aee791316d3b552b21e5e04f56db57"),
    (0x4c7d80, 20, "eb41ac49bf05d388c2f1cf0f73dd2c99781dc3345a06ac745895c924b64b3ac7"),
];
#[cfg(test)] mod tests {
    use super::*;
    use std::collections::HashMap;
    fn fields()->HashMap<(usize,usize),u32>{HashMap::from([
        ((0x339e5a0,0),0x10000),((0x10000,0x144),0x60000),((0x10000,0x330),0x10),
        ((0x20000,0),0x2a85b7c),((0x20000,0x144),0x60000),((0x20000,0x740),101),
        ((0x20000,0x744),0x30000),((0x20000,0x748),0x40000),
        ((0x30000,0),0x2a176c4),((0x30000,0x28),101),((0x30000,0x34),103),
        ((0x30000,0x38),0x02090306),((0x40000,0x28),0x20000),((0x40000,0x2c),0x30000),
    ])}
    fn named(n:u32,s:&[u8])->bool{(n==101&&s==MAC)||(n==103&&s==SMG)||(n==102&&s==PISTOL)}
    #[test] fn exact_mp_mac11_hud_only_without_mutation(){
        let f=fields();assert!(eligible(true,0x20000,103,|p,o|f.get(&(p,o)).copied(),named));
        assert_eq!(f,fields());
        for icon in [0,101,102,104,u32::MAX]{assert!(!eligible(true,0x20000,icon,|p,o|f.get(&(p,o)).copied(),named));}
        assert!(!eligible(false,0x20000,103,|_,_|panic!("disabled read"),|_,_|panic!("disabled name")));
    }
    #[test] fn missing_or_mismatched_world_identity_and_link_retain_native_icon(){
        let f=fields();for key in f.keys(){
            let mut x=f.clone();x.remove(key);assert!(!eligible(true,0x20000,103,|p,o|x.get(&(p,o)).copied(),named),"missing {key:?}");
            x.insert(*key,0);assert!(!eligible(true,0x20000,103,|p,o|x.get(&(p,o)).copied(),named),"zero {key:?}");
        }
    }
}
