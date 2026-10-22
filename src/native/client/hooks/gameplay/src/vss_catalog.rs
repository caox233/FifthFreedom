//! Campaign VSS copied into Spy TwoHander; all native balance/availability fields survive.
//! Do not reclassify the original or invent an optic attachment: its scope is default.
use super::scis::{CopySpec, native_net_id};
pub(super) const RETAIL_NAME: &[u8] = b"Sniper_VSS";
pub(super) const COPY_NAME: &[u8] = b"SCBL_Spy_VSS";
const IDENTITIES: &[(&[u8], &[u8], u32)] = &[
    (b"Sniper_VSS", b"SCBL_Spy_VSS", native_net_id(b"SCBL_Spy_VSS")),
    (b"Sniper_VSS.Sniper_VSS_ExtMag", b"SCBL_Spy_VSS.Sniper_VSS_ExtMag", native_net_id(b"SCBL_Spy_VSS.Sniper_VSS_ExtMag")),
    (b"Sniper_VSS.Internal_Blueprinting", b"SCBL_Spy_VSS.Internal_Blueprinting", native_net_id(b"SCBL_Spy_VSS.Internal_Blueprinting")),
    (b"Sniper_VSS.Internal_MatchGradeTrigger", b"SCBL_Spy_VSS.Internal_MatchGradeTrigger", native_net_id(b"SCBL_Spy_VSS.Internal_MatchGradeTrigger")),
    (b"Sniper_VSS.Internal_RecoilBufferAdj", b"SCBL_Spy_VSS.Internal_RecoilBufferAdj", native_net_id(b"SCBL_Spy_VSS.Internal_RecoilBufferAdj")),
    (b"Sniper_VSS.Internal_BarrelAcc", b"SCBL_Spy_VSS.Internal_BarrelAcc", native_net_id(b"SCBL_Spy_VSS.Internal_BarrelAcc")),
    (b"Sniper_VSS.Internal_WeightBalance", b"SCBL_Spy_VSS.Internal_WeightBalance", native_net_id(b"SCBL_Spy_VSS.Internal_WeightBalance")),
    (b"Sniper_VSS.Rifle_Ammo_MatchGrade", b"SCBL_Spy_VSS.Rifle_Ammo_MatchGrade", native_net_id(b"SCBL_Spy_VSS.Rifle_Ammo_MatchGrade")),
    (b"Sniper_VSS.Rifle_Ammo_AP", b"SCBL_Spy_VSS.Rifle_Ammo_AP", native_net_id(b"SCBL_Spy_VSS.Rifle_Ammo_AP")),
];
const SPEC: CopySpec = CopySpec {
    source: RETAIL_NAME, source_id: 1707760461, identities: IDENTITIES,
    faction: b"Spy", kind: 1, required: 0x1ff, minimum: 9,
};
pub(super) unsafe fn register_with_copy(catalog:usize,xml:usize,factory:impl FnMut(usize)->u8)->u8 {
    super::scis::register_with_spec(catalog,xml,&SPEC,factory)
}
