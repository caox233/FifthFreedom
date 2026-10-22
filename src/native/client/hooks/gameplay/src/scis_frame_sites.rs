//! Entire cooperating installation. No partial subset authorizes a write.
use crate::scis_frame_patch_group as group;
use group::Site;
#[derive(Clone, Copy)]
pub(crate) struct Targets {
    pub source: u32,
    pub publish: u32,
    pub admit: u32,
    pub drain: u32,
    pub finish: u32,
    pub main: u32,
    pub classified: u32,
    pub layout: u32,
    pub vs: u32,
    pub ps: u32,
    pub masks: u32,
    #[cfg(feature = "scis-runtime-diagnostics")]
    pub emissive_observer: u32,
}
pub(crate) const ADMIT: u32 = 0x16dfdc0;
pub(crate) const ADMIT_BYTES: [u8; 5] = [0x55, 0x8b, 0xec, 0x8b, 0xc1];
pub(crate) fn plan(t: Targets) -> Vec<Site> {
    let jump = |a: u32, b: &[u8], d: u32| Site {
        address: a as usize,
        original: b.to_vec(),
        replacement: group::jump(a as usize, d as usize, b.len()),
    };
    let call = |a: u32, b: &[u8], d: u32| Site {
        address: a as usize,
        original: b.to_vec(),
        replacement: group::call(a as usize, d as usize),
    };
    let mov = |a: u32, b: &[u8], op: u8, d: u32| {
        let mut code = vec![op];
        code.extend(d.to_le_bytes());
        Site {
            address: a as usize,
            original: b.to_vec(),
            replacement: code,
        }
    };
    let sites=vec![
        call(0x668bdf, &[0xe8, 0xac, 0xed, 0xff, 0xff], t.source),
        jump(0x667a2c, &[0xe8, 0xaf, 0x28, 0xfd, 0], t.publish),
        jump(ADMIT, &ADMIT_BYTES, t.admit),
        jump(0x161d275, &[0xe8, 0x46, 0xfe, 0xff, 0xff], t.drain),
        jump(
            0x16def90,
            &[0xc7, 0x81, 0x30, 0x43, 0, 0, 0, 0, 0, 0],
            t.finish,
        ),
        jump(0x16df0d8, &[0x8b, 0x40, 0x70, 0x52, 0xff, 0xd0], t.main),
        jump(crate::scis_frame_bridges::CLASSIFIED_SITE, &crate::scis_frame_bridges::CLASSIFIED_BYTES, t.classified),
        mov(0x1675a07, &[0x8b, 0x10, 0x8b, 0x52, 0x2c], 0xba, t.layout),
        mov(0x1676222, &[0x8b, 0x10, 0x8b, 0x52, 0x2c], 0xba, t.layout),
        mov(0x1677702, &[0x8b, 0x17, 0x8b, 0x52, 0x30], 0xba, t.vs),
        mov(0x1677b61, &[0x8b, 0x0b, 0x8b, 0x49, 0x3c], 0xb9, t.ps),
        call(0x173ad2f, &[0xe8, 0x3c, 0x86, 0xf0, 0xff], t.masks),
    ];
    #[cfg(feature = "scis-runtime-diagnostics")]
    let sites={let mut sites=sites;
        sites.push(call(0x172b34c,&[0xe8,0x7f,0xef,0xff,0xff],t.emissive_observer));sites};
    sites
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn scis_whole_group_has_twelve_unique_sites_and_preserves_special_pass_entry() {
        let t = Targets {
            source: 0x10000000,
            publish: 0x10000100,
            admit: 0x10000200,
            drain: 0x10000300,
            finish: 0x10000400,
            main: 0x10000500,
            classified: 0x10000b00,
            layout: 0x10000600,
            vs: 0x10000700,
            ps: 0x10000800,
            masks: 0x10000900,
            #[cfg(feature = "scis-runtime-diagnostics")]
            emissive_observer: 0x10000a00,
        };
        let sites = plan(t);
        assert_eq!(sites.len(), if cfg!(feature="scis-runtime-diagnostics"){13}else{12});
        assert!(group::valid(&sites));
        let main = sites.iter().find(|s| s.address == 0x16df0d8).unwrap();
        assert_eq!(main.address + main.original.len(), 0x16df0de);
        for fail in 0..sites.len() {
            for partial in [false, true] {
                let mut bytes: Vec<_> = sites.iter().map(|s| s.original.clone()).collect();
                let mut attempt = 0;
                assert!(!group::publish(&sites, |a, expected, next| {
                    let i = sites.iter().position(|s| s.address == a).unwrap();
                    if bytes[i] != expected {
                        return false;
                    }
                    let bad = attempt == fail;
                    attempt += 1;
                    if !bad || partial {
                        bytes[i] = next.to_vec();
                    }
                    !bad
                }));
                assert_eq!(
                    bytes,
                    sites.iter().map(|s| s.original.clone()).collect::<Vec<_>>()
                );
            }
        }
    }
}
