//! Copy only the two normalized comparison bars from this live catalog variant.
//! Native catalogs ship different values: do not hard-code a bar count or meters.
use crate::equipment_native::{word, writable};
pub(super) const SOURCE_NAME: &[u8] = b"ADV_Spy_SMG_Scorpion";
const COPY_NAME: &[u8] = b"SCBL_Spy_VSS";
const SUPPRESSOR_NAME: &[u8] = b"ADV_Spy_SMG_Scorpion.SMG_Suppressor";
unsafe fn unique(items: &[usize], name: &[u8], kind: u32) -> Option<usize> {
    let id = super::scis::native_net_id(name);
    let mut found = None;
    for &p in items {
        let named = super::name(p) == Some(name);
        let identified = word(p, 0x10) == Some(id);
        if !named && !identified { continue; }
        if !named || !identified || found.is_some() || word(p, 0x3c)? != kind { return None; }
        found = Some(p);
    }
    found
}
unsafe fn values(catalog: usize) -> Option<(usize, f32, f32)> {
    if word(catalog, 0)? != super::STANDARD { return None; }
    let roots = super::vector(catalog, 0x34)?;
    let pfs = unique(&roots, SOURCE_NAME, 1)?;
    let vss = unique(&roots, COPY_NAME, 1)?;
    if pfs == vss || word(pfs, 0x40)? != 1 || word(vss, 0x40)? != 1 { return None; }
    let children = super::vector(pfs, 0x88)?;
    let suppressor = unique(&children, SUPPRESSOR_NAME, 1)?;
    let d = f32::from_bits(word(pfs, 0x94)?);
    let r = f32::from_bits(word(pfs, 0x98)?);
    let delta = f32::from_bits(word(suppressor, 0x8c)?);
    if !d.is_finite() || !r.is_finite() || !delta.is_finite()
        || !(0.0..=1.0).contains(&d) || !(0.0..=1.0).contains(&r)
        || !(-1.0..=0.0).contains(&delta) { return None; }
    let damage = (d + delta).clamp(0.0, 1.0);
    let range = (r * 1.5).clamp(0.0, 1.0);
    if !writable(vss + 0x94, 8) { return None; }
    Some((vss, damage, range))
}
pub(super) unsafe fn sync(catalog: usize) -> bool {
    let Some((vss, damage, range)) = values(catalog) else { return false; };
    // Validate every link and both outputs before either write. Never write the
    // PFS source, its suppressor, the campaign VSS or any ammo/capacity fields.
    ((vss + 0x94) as *mut f32).write_unaligned(damage);
    ((vss + 0x98) as *mut f32).write_unaligned(range);
    true
}
