//! EE5E50 builds the base UI ammo/silence data from an INI section named after
//! NetName, unlike the catalog factory which reads Archetype. The independent
//! Merc copy has no INI section. Redirect only the section-name concatenation;
//! keep its later CB64A0 catalog lookup and native upgrade-count calculation.
use crate::equipment_native::{patch,readable,signature,word};
#[path="scis_stats_policy.rs"]
mod policy;
const SITE:usize=0xee5ea2;
const CONCAT:usize=0x42eeb0;
type Concat=unsafe extern "thiscall" fn(usize,usize,usize)->usize;

fn bytes(target:usize)->[u8;5] {
    let mut b=[0xe8,0,0,0,0];
    b[1..].copy_from_slice(&(target as u32).wrapping_sub(SITE as u32+5).to_le_bytes()); b
}
pub(super) unsafe fn text<'a>(p:usize)->Option<&'a [u8]> {
    let data=word(p,0)? as usize;
    let count=word(p,4)? as usize;
    let capacity=word(p,8)? as usize;
    if !(1..=128).contains(&count)||capacity<count||capacity>1024||!readable(data,count) {return None;}
    let bytes=std::slice::from_raw_parts(data as *const u8,count);
    (bytes.last()==Some(&0)&&!bytes[..count-1].contains(&0)).then_some(&bytes[..count-1])
}
unsafe fn concat_with(left:usize,out:usize,right:usize,native:impl FnOnce(usize,usize,usize)->usize)->usize {
    let mapped=text(left).zip(text(right)).and_then(|(prefix,name)|policy::config_name(prefix,name));
    if let Some(name)=mapped {
        let mut storage=name.to_vec();storage.push(0);
        let borrowed=[storage.as_ptr() as usize,storage.len(),storage.len()];
        // 42EEB0 reads RHS and copies into OUT. It never owns/frees RHS.
        native(left,out,borrowed.as_ptr() as usize)
    } else { native(left,out,right) }
}
unsafe extern "thiscall" fn concat(left:usize,out:usize,right:usize)->usize {
    let native:Concat=std::mem::transmute(CONCAT);
    concat_with(left,out,right,|l,o,r|native(l,o,r))
}
pub(super) unsafe fn init() {
    if !signature(0xee5e50,413,"df48080f4b1c959bf6b589dc0c71c43db5d0cc6b831d202c086d255072c024c4")
        || !signature(CONCAT,128,"9fb97954293edc488838fff12a873160b4ea767aa4d2516a1647ea0d4c79c002")
        || !patch(SITE,&bytes(CONCAT),&bytes(concat as usize)) {
        tracing::error!("SCBL_MOD_SCIS_STATS enabled=false reason=native-signature-or-patch");return;
    }
    tracing::info!("SCBL_MOD_SCIS_STATS enabled=true config=retail catalog_identity=preserved");
}
pub(super) unsafe fn deinit() {
    if readable(SITE,5)&&std::slice::from_raw_parts(SITE as *const u8,5)==bytes(concat as usize)
        && !patch(SITE,&bytes(concat as usize),&bytes(CONCAT)) {
        tracing::error!("SCBL_MOD_SCIS_STATS restore_failed site=ee5ea2");
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn scis_stats_borrowed_rhs_preserves_original_catalog_and_return_value() {
        let a=b"Echelon.EWeaponArchetype.\0";let b=b"SCBL_Merc_SCIS\0";
        let left=[a.as_ptr() as usize,a.len(),a.len()];let right=[b.as_ptr() as usize,b.len(),b.len()];
        let l=left.as_ptr() as usize;let r=right.as_ptr() as usize;let before=right;
        unsafe {
            assert_eq!(concat_with(l,123,r,|ll,o,rr|{assert_eq!((ll,o),(l,123));assert_eq!(text(rr),Some(b"Sniper_Proto".as_slice()));456}),456);
            assert_eq!(right,before);assert_eq!(text(r),Some(b"SCBL_Merc_SCIS".as_slice()));
            assert_eq!(concat_with(0,123,r,|ll,o,rr|{assert_eq!((ll,o,rr),(0,123,r));789}),789);
        }
    }
}
