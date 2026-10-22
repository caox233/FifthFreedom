//! Intercept the measured-distance player bullet calculation, before integer
//! damage and the native victim body/armor pass. Campaign and other guns pass.
use crate::equipment_native::{patch, readable, signature, word};
use std::sync::atomic::{AtomicBool, Ordering};
#[path = "scis_damage_policy.rs"] mod policy;
#[path = "vss_balance.rs"] mod vss;
static READY: AtomicBool = AtomicBool::new(false);
const SITE: usize = 0xe09be4;
const NATIVE: usize = 0xe098b0;
type Damage = unsafe extern "thiscall" fn(usize, usize, f32, u32, f32) -> f32;
fn call(target: usize) -> [u8;5] {
    let mut b=[0xe8,0,0,0,0];
    b[1..].copy_from_slice(&(target as u32).wrapping_sub(SITE as u32+5).to_le_bytes());b
}
unsafe extern "thiscall" fn damage(logic:usize,victim:usize,distance:f32,bone:u32,attenuation:f32)->f32 {
    let native:Damage=std::mem::transmute(NATIVE);
    let original=native(logic,victim,distance,bone,attenuation);
    if !READY.load(Ordering::Acquire) || !original.is_finite() || original<=0.0 { return original; }
    if let Some(value)=vss::calculate(logic,victim,distance,bone,attenuation) { return value; }
    let Some(factor)=policy::eligible(logic,victim,distance,bone,attenuation,
        |p,o|word(p,o),|n|super::scis_name(n)) else { return original; };
    let Some(piercing)=policy::active_ap(logic, |p,o|word(p,o),
        |n|super::native_name_is(n,b"Rifle_Ammo_AP")) else { return original; };
    let base:unsafe extern "thiscall" fn(usize)->i32=std::mem::transmute(0xdfeef0usize);
    // Beyond 50m retain the original range/attachment falloff shape, normalized
    // at the boundary so there is no abrupt reversion to the old weak damage.
    let far=if distance>5000.0 {
        let at50=native(logic,victim,5000.0,bone,attenuation);
        if !at50.is_finite() || at50<=0.0 {return original;}
        (original/at50).clamp(0.0,1.0)
    } else {1.0};
    policy::pre_armor(distance,bone,factor,base(logic),attenuation,far,piercing).unwrap_or(original)
}
pub(super) unsafe fn init() {
    if READY.load(Ordering::Acquire) {return;}
    for &(p,n,h) in SIGNATURES {
        if !signature(p,n,h) {tracing::error!("SCBL_MOD_SCIS_DAMAGE enabled=false reason=signature address={p:x}");return;}
    }
    if !patch(SITE,&call(NATIVE),&call(damage as usize)) {
        tracing::error!("SCBL_MOD_SCIS_DAMAGE enabled=false reason=callsite");return;
    }
    READY.store(true,Ordering::Release);
    tracing::info!("SCBL_MOD_SCIS_DAMAGE enabled=true revision=distance-ap torso_lethal_m=25/30 close_limb_lethal_m=5/10 ap=exact-active-attachment armor=native-health-and-reduction campaign=unchanged");
}
pub(super) unsafe fn deinit() {
    READY.store(false,Ordering::Release);
    let installed=call(damage as usize);
    if readable(SITE,5) && std::slice::from_raw_parts(SITE as *const u8,5)==installed
        && !patch(SITE,&installed,&call(NATIVE)) {tracing::error!("SCBL_MOD_SCIS_DAMAGE restore_failed");}
}
const SIGNATURES:&[(usize,usize,&str)]=&[
    (0xe09af0,255,"5782247f8368fc81836b9c452849a438ae4ff01a6edb76e33e447cd27aeb1cc1"),
    (0xe098b0,572,"cd19c5c8eee547a9aad7f9d8eeecadb5b2aab292c40a735d6a2e831860d2b7b4"),
    (0xdfeef0,88,"775ea5166d4b63d71414291e0d1f3111f2b1fe8df49e2994762999619fe001ff"),
    (0xdd81b0,141,"cbf37db3984eda97c2c3d5f7e3a33c02d75b2ae802b04f24bac37bd9d86afbf9"),
    (0xc84280,225,"2a4a11fb612ae20337f3ab6e151d0c9a4b47254d77ab8e7a76fcd252ad7f831c"),
    (0xd30d80,229,"d6d30c64cdce6a1b6bce200a0139b63f0da0107cc18386dcae75c764c8a1b19d"),
    (0xc70b10,29,"f21151896f425815cd021327c83c684f0443c26ec5fdf33f142f40678e193562"),
    (0xdd7270,137,"68ce34b12b3ae12e8c9c798e3b0c2d7285d20b9cc95edef29dd94a75d00bb680"),
    (0xe00170,155,"71d1a1f781bd4a3a18cb4285587fa3007e209f50cd8a800205256681b48145fb"),
    (0xdfef50,122,"ecd2386622d1bd76c1195f6065bbe6edf6298709ebe2533c4c9fd356348055b7"),
    (0xdfefd0,541,"4d6d3a0b3bee4846a5a877f52758bdc63a5ba8a42659a69e3f1f9d92034b76aa"),
    (0x4fc5f0,149,"e1ef3626e0a13d8858df0c2d2c6464d4c57db732153874c6ec65842cc838ba2e"),
    (0x8b5e60,42,"316b0873cfe3c1d6a1732fe5e375dffe56eaefa2da6420be43afc9312bfab600"),
];
