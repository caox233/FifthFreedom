//! Read-only current-stage state, not the previous pass's renderer bits.
//! Retail175B4E0 chooses a strategy from the current camera, resets shader flags
//! and enables option1F9 before175A8A0. Known strategy callbacks modify only the
//! separate pass-template properties, never this node's owned emissive material.
use crate::scis_material_binding::Read;

// Constructor17559C0 populates exactly these slots. Keep the three consumed
// virtual methods pinned as well as their class; a foreign vtable fails closed.
const CLASSES: [[u32; 4]; 5] = [
    [0x2c0916c, 0x174bea0, 0x174cf20, 0x174cf70],
    [0x2c091bc, 0x174c100, 0x174d240, 0x174d290],
    [0x2c09454, 0x174c260, 0x174d6d0, 0x174ba20],
    [0x2c094a4, 0x174c4b0, 0x174dbc0, 0x174dc10],
    [0x2c0965c, 0x174c5d0, 0x174de80, 0x174df10],
];

struct Alpha<'a, R: Read> {
    read: &'a R,
    renderer: u32,
    option_word: u32,
    collection: Option<u32>,
    pub(crate) template: u32,
}
impl<R: Read> Read for Alpha<'_, R> {
    fn word(&self, address: u32) -> Option<u32> {
        if address == self.renderer.checked_add(0x1c)? {
            if let Some(collection) = self.collection {
                return Some(collection);
            }
        }
        if address == self.renderer.checked_add(0x2300)?
            || address == self.renderer.checked_add(0x2304)?
        {
            return Some(0);
        }
        let value = self.read.word(address)?;
        if address == self.option_word {
            Some((value & !0xff00) | 0x100)
        } else {
            Some(value)
        }
    }
}

/// `pass` must be this renderer's normal geometry pass, constructed at outer+66C4.
/// Only the ordinary, non-override Alpha path is supported. The caller still
/// must bind the node to the selected provider/frame and the consumed transform.
fn alpha<'a, R: Read>(read: &'a R, renderer: u32, pass: u32) -> Option<Alpha<'a, R>> {
    if read.pointer(renderer, 0x236c)? != pass
        || read.pointer(pass, 0x14)? != renderer
        || read.at(renderer, 0x24bc)? != 0
    {
        return None;
    }
    let settings = read.pointer(renderer, 0x20d0)?;
    if read.at(settings, 0x274)? & 0x1db != 0 || read.at(pass, 0xc)? != 10 {
        return None;
    }
    let index = read.at(settings, 0x340)?;
    let class = match index {
        2 | 3 => 0,
        4 | 5 => 1,
        7 => 2,
        8 => 3,
        9 => 4,
        _ => return None,
    };
    let strategy = read.pointer(read.pointer(pass, 8)?, index * 4)?;
    let expected = CLASSES[class];
    if read.at(strategy, 0)? != expected[0]
        || read.at(expected[0], 0x14)? != expected[1]
        || read.at(expected[0], 0x20)? != expected[2]
        || read.at(expected[0], 0x2c)? != expected[3]
    {
        return None;
    }
    let template = read.pointer(pass, 0)?;
    if read.pointer(strategy, 4)? != template || read.pointer(strategy, 8)? != template {
        return None;
    }
    let options = read.pointer(renderer, 0x14)?;
    // word() may only replace this one byte, preserving neighbouring options.
    let option_word = options.checked_add(0x1f8)?;
    if option_word & 3 != 0 {
        return None;
    }
    Some(Alpha {
        read,
        renderer,
        option_word,
        template,
        collection: None,
    })
}

/// One entry point for current normal-material resolution. Requires the actual
/// shader-mask allocation registered by the same atomic install group.
pub(crate) fn select_alpha(
    read: &impl Read,
    renderer: u32,
    pass: u32,
    node: u32,
    sub: u32,
    class: crate::scis_material_binding::EmissiveClass,
) -> Option<crate::scis_material_binding::Selection> {
    let stage = alpha(read, renderer, pass)?;
    zero_base_lod(read, node)?;

    let masks = read.word(0x344a658)?;
    let count = crate::scis_shader_masks::count(masks)?;
    let table = read.word(0x344a664)?;
    let selected = crate::scis_material_binding::select_normal_emissive(
        &stage,
        renderer,
        node,
        sub,
        crate::scis_material_binding::NormalPass::Alpha,
        class,
        masks,
        count,
        table,
    )?;
    // The strategy is free to alter its own template between draws. It must
    // never alias the owned material whose final fields were just examined.
    if selected.props == stage.template
        || read.word(0x344a658)? != masks
        || read.word(0x344a664)? != table
        || crate::scis_shader_masks::count(masks) != Some(count)
    {
        return None;
    }
    Some(selected)
}

// selected_draw decodes absolute hidden bits; native draws use selected-LOD
// relative bits. They are equivalent only for a zero-base LOD.
fn zero_base_lod(read: &impl Read, node: u32) -> Option<()> {
    // The shared Draw decoder still checks absolute hidden bits. Accept only
    // a zero-base selected LOD, where it agrees with the native relative mask.
    // This restriction is deliberate; do not drop visibility checks to widen it.
    let mesh = read.pointer(node, 0xf8)?;
    let lod_count = read.at(mesh, 0x6c)?;
    if lod_count != 0 {
        if lod_count > 32 {
            return None;
        }
        let flags = read.at(node, 0x38)?;
        let wanted = if flags & 0x400000 != 0 {
            read.byte(node, 0x149)? as u32
        } else {
            (flags >> 3) & 3
        };
        let lod = read
            .pointer(mesh, 0x68)?
            .checked_add(wanted.min(lod_count - 1) * 0x34)?;
        if read.at(lod, 0x2c)? != 0 {
            return None;
        }
    }

    Some(())
}

/// Ordinary deferred stage6 is distinct from the special normal Alpha pass.
/// 16DC2A6 calls outer+4DA8/177AE10 even when camera+340 is zero. Its current
/// source list is classified by17797E0; stage6 calls17727F0 using own materials
/// without the normal pass's bit800. Do not reuse the already-built queue here:
/// before_main runs before1779EF0 clears/rebuilds it for this scene.
fn deferred<'a, R: Read>(read: &'a R, renderer: u32, node: u32) -> Option<Alpha<'a, R>> {
    let pass = renderer.checked_sub(0x2c)?.checked_add(0x4da8)?;
    let settings = read.pointer(renderer, 0x20d0)?;
    let options = read.pointer(renderer, 0x14)?;
    if read.pointer(pass, 0x8ac)? != renderer
        || read.at(settings, 0x340)? != 0
        || read.at(settings, 0x274)? != 0
        || read.at(renderer, 0x2340)? != 0
        || read.at(renderer, 0x24bc)? != 0
        || read.at(node, 0x34)? & 0x800 != 0 //177B670 substitutes these nodes.
        || (read.byte(options, 0x1dd)? != 0 && read.at(node, 0x2c)? & 2 != 0)
    {
        return None;
    }
    let flags = read.at(node, 0x38)?;
    if flags & 0xc0000 == 0 || flags & 0x20 == 0 || flags & 0x400 != 0 {
        return None;
    }
    // The bound runtime Source/Transport proves frame ownership; this extra
    // check proves membership in the CURRENT source that17797E0 will consume.
    // Only the unsliced primary collection is supported, not guessed partitions.
    if read.at(renderer, 0x2208)? != u32::MAX {
        return None;
    }
    // At16DF0D8 the cached record (+1C) may belong to the previous view or be
    // null. The subsequent16DC4CC/1760B00 selects settings+26C, exactly as the
    // camera preparation/commit path does. Never authorize from stale membership.
    let slot = read.at(settings, 0x26c)?;
    if slot >= 10 {
        return None;
    }
    let collection = renderer.checked_add(0x24)?.checked_add(slot * 0x344)?;
    let count = read.at(collection, 0x74)?;
    if count == 0 || count > 4096 {
        return None;
    }
    let first = read.pointer(collection, 0x70)?;
    if !read.unique_member(first, count, node)? {
        return None;
    }
    zero_base_lod(read, node)?;
    let option_word = options.checked_add(0x1f8)?;
    if option_word & 3 != 0 {
        return None;
    }
    //177AE5D resets flags via175CA30;177AE92 sets1F9 before stage6.
    Some(Alpha {
        read,
        renderer,
        option_word,
        template: 0,
        collection: Some(collection),
    })
}

/// Prepared once for one identify call, never cached across frames or nodes.
/// Material reads remain fresh per submesh; membership is not rescanned.
pub(crate) struct CurrentStage {
    renderer: u32,
    node: u32,
    option_word: u32,
    template: u32,
    collection: Option<u32>,
    pass: crate::scis_material_binding::NormalPass,
}

pub(crate) fn prepare_current(
    read: &impl Read,
    renderer: u32,
    normal_pass: u32,
    node: u32,
) -> Option<CurrentStage> {
    let settings = read.pointer(renderer, 0x20d0)?;
    let (stage, pass) = if read.at(settings, 0x340)? != 0 {
        zero_base_lod(read, node)?;
        (
            alpha(read, renderer, normal_pass)?,
            crate::scis_material_binding::NormalPass::Alpha,
        )
    } else {
        (
            deferred(read, renderer, node)?,
            crate::scis_material_binding::NormalPass::DeferredAlpha,
        )
    };
    Some(CurrentStage {
        renderer,
        node,
        option_word: stage.option_word,
        template: stage.template,
        collection: stage.collection,
        pass,
    })
}

impl CurrentStage {
    pub(crate) fn select(
        &self,
        read: &impl Read,
        sub: u32,
        class: crate::scis_material_binding::EmissiveClass,
    ) -> Option<crate::scis_material_binding::Selection> {
        let stage = Alpha {
            read,
            renderer: self.renderer,
            option_word: self.option_word,
            template: self.template,
            collection: self.collection,
        };
        let masks = read.word(0x344a658)?;
        let count = crate::scis_shader_masks::count(masks)?;
        let table = read.word(0x344a664)?;
        let selected = crate::scis_material_binding::select_normal_emissive(
            &stage,
            self.renderer,
            self.node,
            sub,
            self.pass,
            class,
            masks,
            count,
            table,
        )?;
        if selected.props == self.template
            || read.word(0x344a658)? != masks
            || read.word(0x344a664)? != table
            || crate::scis_shader_masks::count(masks) != Some(count)
        {
            return None;
        }
        Some(selected)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::BTreeMap;
    struct Memory(BTreeMap<u32, u32>);
    impl Read for Memory {
        fn word(&self, a: u32) -> Option<u32> {
            self.0.get(&a).copied()
        }
    }
    const R: u32 = 0x100000;
    const N: u32 = 0x200000;
    const S: u32 = 0x300000;
    const O: u32 = 0x400000;
    const C: u32 = R + 0x24 + 3 * 0x344;
    const A: u32 = 0x600000;
    const M: u32 = 0x700000;
    fn fixture() -> Memory {
        Memory(BTreeMap::from([
            (R - 0x2c + 0x4da8 + 0x8ac, R),
            (R + 0x20d0, S),
            (R + 0x14, O),
            (R + 0x2340, 0),
            (R + 0x24bc, 0),
            (S + 0x340, 0),
            (S + 0x274, 0),
            (N + 0x34, 0x408600),
            (N + 0x38, 0x080c10e0),
            (O + 0x1dc, 0),
            (R + 0x2208, u32::MAX),
            (R + 0x1c, 0),
            (S + 0x26c, 3),
            (C + 0x70, A),
            (C + 0x74, 3),
            (A, N + 4),
            (A + 4, N),
            (A + 8, N + 8),
            (N + 0xf8, M),
            (M + 0x6c, 0),
        ]))
    }
    #[test]
    fn current_deferred_lens_is_not_a_normal_alpha_strategy() {
        let r = fixture();
        assert!(prepare_current(&r, R, R - 0x2c + 0x66c4, N).is_some());
        assert!(alpha(&r, R, R - 0x2c + 0x66c4).is_none());
        for (a, v) in [
            (R - 0x2c + 0x4da8 + 0x8ac, R + 4),
            (S + 0x340, 1),
            (S + 0x274, 0x200),
            (S + 0x26c, 10),
            (S + 0x26c, u32::MAX),
            (R + 0x2340, 1),
            (R + 0x24bc, 1),
            (N + 0x34, 0x408e00),
            (N + 0x38, 0x080c14e0),
            (N + 0x38, 0x080010e0),
            (N + 0x38, 0x080c10c0),
            (R + 0x2208, 0),
            (C + 0x74, 0),
            (C + 0x74, 4097),
            (A + 4, N + 4),
            (A, N),
        ] {
            let mut changed = fixture();
            changed.0.insert(a, v);
            assert!(
                prepare_current(&changed, R, R - 0x2c + 0x66c4, N).is_none(),
                "{a:x}={v:x}"
            );
        }
    }
    #[test]
    fn pre_main_uses_bound_camera_for_membership_and_material_flags() {
        for old in [0, R + 0x24, C] {
            let mut r = fixture();
            r.0.insert(R + 0x1c, old);
            let s = deferred(&r, R, N).expect("bound camera is available before native selects it");
            assert_eq!(s.word(R + 0x1c), Some(C));
            r.0.insert(C + 0x74, 0);
            r.0.insert(R + 0x24 + 0x70, A);
            r.0.insert(R + 0x24 + 0x74, 3);
            assert!(deferred(&r, R, N).is_none(), "old view cannot authorize current view");
        }
    }
    #[test]
    fn current_source_membership_is_unique_bounded_and_read_only() {
        let r = fixture();
        let before = r.0.clone();
        assert_eq!(r.unique_member(A, 3, N), Some(true));
        assert_eq!(r.unique_member(A, 3, N + 12), Some(false));
        assert_eq!(r.unique_member(A, 4, N), None);
        assert_eq!(r.unique_member(A, 4097, N), None);
        assert_eq!(r.unique_member(0xfffffffc, 2, N), None);
        assert_eq!(r.0, before);
    }
}
