//! Read-only decoding of an already selected native emissive draw.
//! Selection/lifetime ownership is supplied by the caller; this never treats
//! a previous D3D context binding as proof of the current packet.
pub(crate) trait Read {
    fn word(&self, address: u32) -> Option<u32>;
    // A bounded current-scene membership check; native readers can validate the
    // whole array once instead of querying every word's virtual-memory region.
    fn unique_member(&self, first: u32, count: u32, value: u32) -> Option<bool> {
        if first < 0x10000 || count == 0 || count > 4096 {
            return None;
        }
        first.checked_add(count.checked_mul(4)?)?;
        let mut matches = 0;
        for i in 0..count {
            matches += u32::from(self.at(first, i * 4)? == value);
        }
        Some(matches == 1)
    }
    fn at(&self, base: u32, offset: u32) -> Option<u32> {
        if base < 0x10000 {
            return None;
        }
        self.word(base.checked_add(offset)?)
    }
    fn byte(&self, base: u32, offset: u32) -> Option<u8> {
        let a = base.checked_add(offset)?;
        Some((self.word(a & !3)? >> ((a & 3) * 8)) as u8)
    }
    fn pointer(&self, base: u32, offset: u32) -> Option<u32> {
        let p = self.at(base, offset)?;
        (p >= 0x10000).then_some(p)
    }
}

/// 1695460 uses signed entries; all negative values are ignored and the last
/// writer wins. Reject positive out-of-range entries rather than corrupt memory.
pub(crate) fn remap(map: &[i8; 16], input: &[u32; 16]) -> Option<[u32; 16]> {
    let mut out = [0; 16];
    for (source, &dest) in map.iter().enumerate() {
        if dest >= 16 {
            return None;
        }
        if dest >= 0 {
            out[dest as usize] = input[source];
        }
    }
    Some(out)
}

/// Exact native173A410 hash/linked-bucket lookup with bounded reads and traversal.
/// This returns the native wrapper, not the D3D shader object at wrapper+8.
pub(crate) fn shader(r: &impl Read, table: u32, key: [u32; 3]) -> Option<u32> {
    let count = r.at(table, 0xc)?;
    if count == 0 || count > 65536 {
        return None;
    }
    let buckets = r.pointer(table, 8)?;
    let hash = key[0]
        .wrapping_mul(0x8da6b343)
        .wrapping_sub(key[1].wrapping_mul(0x27e9c7bf));
    let bucket = buckets.checked_add((hash % count).checked_mul(8)?)?;
    let mut entry = r.pointer(bucket, 0)?;
    let last = r.pointer(bucket, 4)?;
    let end = r.at(last, 0x18)?;
    // The render table is small. A malformed/cyclic chain must not hang render.
    for _ in 0..256 {
        if entry == end {
            return None;
        }
        if [r.at(entry, 0)?, r.at(entry, 4)?, r.at(entry, 8)?] == key {
            return r.pointer(entry, 0xc);
        }
        entry = r.at(entry, 0x18)?;
    }
    None
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct Draw {
    pub vb: u32,
    pub ib: u32,
    pub srv: u32,
    pub stride: u32,
    pub offset: u32,
    pub index_offset: u32,
    pub indices: u32,
    pub start: u32,
    pub base: i32,
    pub vs: u32,
    pub ps: u32,
    pub layout: u32,
    pub uv: [u32; 4],
}
/// Input layout table dimensions are native globals0341FA4C/0341FA40.
/// Called only after native shader selection and material-property substitution.
/// `state` is the native logical device state, NOT ID3D11DeviceContext.
pub(crate) fn current(
    r: &impl Read,
    packet: u32,
    expected_node: u32,
    state: u32,
    layouts: u32,
    vs_count: u32,
    declaration_count: u32,
) -> Option<Draw> {
    let node = r.pointer(packet, 0)?;
    if node != expected_node {
        return None;
    }
    let sub = r.pointer(packet, 4)?;
    let props = r.pointer(packet, 0x20)?;
    let decl = r.pointer(node, 0x154)?;
    if r.at(state, 0x74)? != decl {
        return None;
    }
    resolved(
        r,
        node,
        sub,
        props,
        r.pointer(props, 0x10c)?,
        r.pointer(state, 0x64)?,
        r.pointer(state, 0x6c)?,
        layouts,
        vs_count,
        declaration_count,
    )
}

/// Common read-only draw decoder. All arguments describe the *current* native
/// selection; no logical-device state from an earlier pass is consulted here.
pub(crate) fn resolved(
    r: &impl Read,
    node: u32,
    sub: u32,
    props: u32,
    texture: u32,
    vs: u32,
    ps: u32,
    layouts: u32,
    vs_count: u32,
    declaration_count: u32,
) -> Option<Draw> {
    let mesh = r.pointer(node, 0xf8)?;
    let start = r.pointer(mesh, 0x60)?;
    let count = r.at(mesh, 0x64)?;
    let delta = sub.checked_sub(start)?;
    if count == 0
        || count > 32
        || delta % 0x90 != 0
        || delta / 0x90 >= count
        || r.at(node, 0xf4)? & (1u32 << (delta / 0x90)) != 0
    {
        return None;
    }
    let material_index = r.at(sub, 0x3c)?;
    if material_index >= r.at(node, 0x164)? {
        return None;
    }
    // packet+20 is the actual substituted properties; +1C can be a different
    // source material, and must not be mistaken for the consumed properties.
    if r.byte(props, 0x46)? != 0 {
        return None;
    } // Additional UV matrix not yet supported.
    if r.byte(texture, 0x6d)? == 0 {
        return None;
    }
    let srv = r.pointer(r.pointer(texture, 0x30)?, 0x14)?;
    let v = r.pointer(sub, 0x50)?;
    let i = r.pointer(sub, 0x58)?;
    // Native172A998/99E takes submesh+58 whenever hull/domain is absent.
    // An allocated but unused +5C must not reject an ordinary triangle draw.
    if r.at(vs, 0xc)? != 0 || r.at(vs, 0x10)? != 0 {
        return None;
    } // no hull/domain
    let decl = r.pointer(node, 0x154)?;
    let vi = r.at(vs, 0x1c)?;
    let di = r.at(decl, 0x20)?;
    if vs_count == 0
        || vs_count > 65536
        || declaration_count == 0
        || declaration_count > 65536
        || vi >= vs_count
        || di >= declaration_count
    {
        return None;
    }
    let row = r.pointer(layouts, vi.checked_mul(4)?)?;
    let layout = r.pointer(row, di.checked_mul(4)?)?;
    if r.at(ps, 0x14)? != 0 {
        let mapping = r.pointer(ps, 0x10)?;
        let mut map = [-1; 16];
        for (j, d) in map.iter_mut().enumerate() {
            *d = r.byte(mapping, j as u32)? as i8;
        }
        // Other native SRVs may be modified by this material pass. We can
        // authorize the reticle only if slot0's final writer is its own SRV.
        let mut origins = [0; 16];
        for (j, x) in origins.iter_mut().enumerate() {
            *x = j as u32 + 1;
        }
        if remap(&map, &origins)?[0] != 1 {
            return None;
        }
    }
    let stride = r.at(v, 0x44)?;
    let offset = r.at(sub, 0x60)?;
    let triangles = r.at(sub, 0x64)?;
    let native_i = r.pointer(i, 0x2c)?;
    if stride != 32
        || offset != 0
        || triangles != 14
        || r.at(v, 0x40)? != 16
        || r.at(native_i, 0x2c)? != 57
    {
        return None;
    } // DXGI_FORMAT_R16_UINT
    Some(Draw {
        vb: r.pointer(r.pointer(v, 0x2c)?, 8)?,
        ib: r.pointer(native_i, 8)?,
        srv,
        stride,
        offset,
        index_offset: 0,
        indices: triangles.checked_mul(3)?,
        start: 0,
        base: 0,
        vs: r.pointer(vs, 8)?,
        ps: r.pointer(ps, 8)?,
        layout,
        uv: [
            r.at(props, 0xf0)?,
            r.at(props, 0xf4)?,
            r.at(props, 0xf8)?,
            r.at(props, 0xfc)?,
        ],
    })
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(crate) struct Selection {
    pub node: u32,
    pub sub: u32,
    pub props: u32,
    pub texture: u32,
    pub vs: u32,
    pub ps: u32,
    pub flags: [u32; 2],
    pub vertex_key: [u32; 3],
    pub pixel_key: [u32; 3],
}

/// Exact native 1644BF0/528BE0 class-name ancestry comparison, with a finite
/// bound rather than recursively following a corrupt superclass chain.
fn typed_material(r: &impl Read, source: u32, type_record: u32) -> Option<bool> {
    let wanted = r.at(type_record, 4)?;
    let mut cls = r.pointer(source, 0x1c)?;
    for _ in 0..64 {
        if r.at(cls, 4)? == wanted {
            return Some(true);
        }
        cls = r.at(cls, 0x18)?;
        if cls == 0 {
            return Some(false);
        }
        if cls < 0x10000 {
            return None;
        }
    }
    None
}

/// Native 173A9F0/173AA50 mask then shader-table selection. `mask_count`
/// must come from the actual native allocation/serialized header, NOT an enum
/// guess or readable VirtualQuery region size. A missing allocation proof fails.
fn select_shader(
    r: &impl Read,
    masks: u32,
    mask_count: u32,
    table: u32,
    index: u32,
    flags: [u32; 2],
) -> Option<(u32, [u32; 3])> {
    if mask_count == 0 || mask_count > 65536 || index >= mask_count {
        return None;
    }
    let at = index.checked_mul(8)?;
    let key = [
        index,
        flags[0] & r.at(masks, at)?,
        flags[1] & r.at(masks, at.checked_add(4)?)?,
    ];
    Some((shader(r, table, key)?, key))
}

/// Read-only counterpart of native17B6720 dispatch, both17B6200/17B6570 paths
/// and the172B2E0 PS override.
/// Called only for a retained emissive pass/node owned by the selected scene.
/// Native default properties are shared and still contain the previous draw's
/// texture before this pass. Return the current source texture separately;
/// never mutate that shared template or renderer just to predict selection.
/// `virtual_override` is valid only when provided by the exact pass's native
/// query at vtable+1C; None rejects that branch rather than guessing index15E.
pub(crate) fn select_emissive(
    r: &impl Read,
    renderer: u32,
    pass: u32,
    node: u32,
    sub: u32,
    type_record: u32,
    masks: u32,
    mask_count: u32,
    table: u32,
    virtual_override: Option<u32>,
) -> Option<Selection> {
    let mesh = r.pointer(node, 0xf8)?;
    let first = r.pointer(mesh, 0x60)?;
    let count = r.at(mesh, 0x64)?;
    let delta = sub.checked_sub(first)?;
    if count == 0
        || count > 32
        || delta % 0x90 != 0
        || delta / 0x90 >= count
        || r.at(node, 0xf4)? & (1u32 << (delta / 0x90)) != 0
    {
        return None;
    }
    let mi = r.at(sub, 0x3c)?;
    let mc = r.at(node, 0x164)?;
    if mc == 0 || mc > 4096 || mi >= mc {
        return None;
    }
    let source = r.pointer(r.pointer(node, 0x160)?, mi.checked_mul(8)?)?;
    // Actual17B6720 dispatch: selected camera settings+274 bit200 uses
    // diffuse substitution17B6200. Ordinary emissive17B6570 uses source+110
    // and does NOT add the typed diffuse-variant flag. Do not conflate them.
    let pass_flags = r.at(r.pointer(renderer, 0x20d0)?, 0x274)?;
    // Outer native17B6820 skips this pass entirely without bits20 or200.
    // A valid emissive texture alone does not mean this scene will draw it.
    if pass_flags & 0x220 == 0 {
        return None;
    }
    let force_diffuse = pass_flags & 0x200 != 0;
    let mut flags = [r.at(node, 0x168)?, r.at(node, 0x16c)?];
    if force_diffuse && typed_material(r, source, type_record)? && r.at(source, 0x1b4)? != 0 {
        flags[1] |= 1;
    }
    let implementation = r.pointer(pass, 4)?;
    let props = r.pointer(pass, 8)?;
    let texture = r.pointer(source, if force_diffuse { 0x10c } else { 0x110 })?;
    let vi = r.at(implementation, 0x30)?;
    let mut pi = r.at(implementation, 0x34)?;
    let (vs, vertex_key) = select_shader(r, masks, mask_count, table, vi, flags)?;
    // Native selects the default PS first even when the override then replaces
    // it. Only the consumed PS is relevant; an absent default may be replaced.
    let mut ps_flags = flags;
    if r.byte(props, 0x16a)? != 0 && r.at(r.pointer(renderer, 0x14)?, 0x3b8)? != 1 {
        ps_flags[1] |= 0x80000000;
        pi = if r.at(renderer, 0x24bc)? != 0 {
            virtual_override?
        } else {
            0x15e
        };
    }
    let (ps, pixel_key) = select_shader(r, masks, mask_count, table, pi, ps_flags)?;
    Some(Selection {
        node,
        sub,
        props,
        texture,
        vs,
        ps,
        flags,
        vertex_key,
        pixel_key,
    })
}

pub(crate) fn selected_draw(
    r: &impl Read,
    s: Selection,
    layouts: u32,
    rows: u32,
    cols: u32,
) -> Option<Draw> {
    resolved(
        r, s.node, s.sub, s.props, s.texture, s.vs, s.ps, layouts, rows, cols,
    )
}

/// Normal geometry passes use each submesh's own finalized material, unlike
/// NormalsPass.PreviewMaterial. The caller must identify the actual pass and pin
/// this class's four native addresses before enabling a camera correction.
#[derive(Clone, Copy)]
pub(crate) struct EmissiveClass {
    pub properties: u32,
    pub implementation: u32,
    pub vertex_query: u32,
    pub pixel_query: u32,
}
#[derive(Clone, Copy)]
pub(crate) enum NormalPass {
    Opaque,
    Alpha,
    Solid,
    /// Deferred renderer1779FE0 ->1779D90 ->17727F0 (stages5/6).
    /// Unlike175A8A0 this dispatcher always uses the node's own material;
    /// node+34 bit800 and camera+340 are NOT its dispatch conditions.
    DeferredAlpha,
}

/// Counterpart of non-instanced1750190/1753C80/1755000 selection, followed by
/// EmissiveMaterial172B2E0. It predicts desired state, not the previous draw's
/// logical-device bindings. Unsupported sorting/instancing fails closed.
pub(crate) fn select_normal_emissive(
    r: &impl Read,
    renderer: u32,
    node: u32,
    sub: u32,
    pass: NormalPass,
    class: EmissiveClass,
    masks: u32,
    mask_count: u32,
    table: u32,
) -> Option<Selection> {
    // Native pass dispatch175A3D2/175A9BD/175AB62 uses this material only
    // for bit800 and no override bit400000. Otherwise it substitutes pass props.
    let deferred = matches!(pass, NormalPass::DeferredAlpha);
    if !deferred && (r.at(node, 0x34)? & 0x800 == 0 || r.at(node, 0x2c)? & 0x400000 != 0) {
        return None;
    }
    let mesh = r.pointer(node, 0xf8)?;
    let first = r.pointer(mesh, 0x60)?;
    let count = r.at(mesh, 0x64)?;
    let delta = sub.checked_sub(first)?;
    if count == 0
        || count > 32
        || delta % 0x90 != 0
        || delta / 0x90 >= count
        || r.byte(mesh, 0x87)? != 0
    {
        return None;
    }
    let index = delta / 0x90;
    let lod_count = r.at(mesh, 0x6c)?;
    let (begin, drawn) = if lod_count == 0 {
        (0, count)
    } else {
        if lod_count > 32 {
            return None;
        }
        let flags = r.at(node, 0x38)?;
        let wanted = if flags & 0x400000 != 0 {
            r.byte(node, 0x149)? as u32
        } else {
            (flags >> 3) & 3
        };
        let lod = r
            .pointer(mesh, 0x68)?
            .checked_add(wanted.min(lod_count - 1) * 0x34)?;
        (r.at(lod, 0x2c)?, r.at(lod, 0x30)?)
    };
    if drawn == 0
        || begin.checked_add(drawn)? > count
        || index < begin
        || index >= begin + drawn
        || r.at(node, 0xf4)? & (1 << (index - begin)) != 0
    {
        return None;
    }
    if r.at(sub, 0x58)? != 0 && r.at(sub, 0x64)? == 0 {
        return None;
    }
    if r.at(node, 0x34)? & 0x10 != 0
        && r.at(sub, 0x78)? != 0
        && r.at(r.pointer(sub, 0x74)?, 0x90)? != 0
    {
        return None;
    }
    let mi = r.at(sub, 0x3c)?;
    let mc = r.at(node, 0x164)?;
    if mc == 0 || mc > 4096 || mi >= mc {
        return None;
    }
    let props = r.pointer(r.pointer(node, 0x160)?, mi.checked_mul(8)?)?;
    if r.at(props, 0)? != class.properties {
        return None;
    }
    let implementation = r.pointer(props, 0x2c)?;
    if r.at(implementation, 0)? != class.implementation
        || r.at(class.implementation, 0x18)? != class.vertex_query
        || r.at(class.implementation, 0x1c)? != class.pixel_query
    {
        return None;
    }
    // Native17297D0 derives46 from100,39 from168/16A and high flag4000
    // from16A. Never authorize identity UV or opaque drawing from stale fields.
    let special = r.byte(props, 0x16a)? != 0;
    let alpha = r.byte(props, 0x168)? != 0 || special;
    if r.at(props, 0x100)? != 0
        || r.byte(props, 0x46)? != 0
        || (r.byte(props, 0x39)? != 0) != alpha
        || (special && r.at(props, 0x34)? & 0x4000 == 0)
    {
        return None;
    }
    let mask = r.byte(renderer, 0x2344)?;
    if r.byte(node, 0x2d)? & mask == 0 || r.byte(props, 0x3d)? & mask == 0 {
        return None;
    }
    let rejected = match pass {
        NormalPass::Opaque => alpha || r.byte(props, 0x4c)? != 0,
        NormalPass::Alpha => !alpha,
        NormalPass::Solid => r.byte(props, 0x4c)? != 0,
        NormalPass::DeferredAlpha => {
            !alpha || r.byte(props, 0x3a)? != 0 || r.byte(props, 0x3b)? != 0
        }
    };
    if rejected {
        return None;
    }
    // Deferred1773062 may call17B81D0 when options+204 is enabled. For the
    // supported Alpha contract above, props+39 !=0 and props+3A ==0: that
    // native helper always returns without changing either shader-flag word
    // (17B8220..17B822A). Do not reject the entire optional-lighting mode.
    // Opaque/refractive variants can add bit40000000 and remain unsupported;
    // keep the derived-alpha and3A/3B checks before resolving any shaders.
    let options = r.pointer(renderer, 0x14)?;
    let source_flags = r.at(props, 0x34)?;
    let mut flags = [
        r.at(node, 0x168)? | r.at(props, 0x30)?,
        r.at(node, 0x16c)? | source_flags,
    ];
    if r.byte(options, 0x1e9)? == 0 {
        flags[1] &= !0x1e000000;
    }
    if r.byte(options, 0x1f9)? != 0 {
        flags[1] |= 0x800000;
    }
    if r.byte(options, 0x1e8)? == 0 {
        flags[1] &= !0x400000;
    }
    flags[0] |= r.at(renderer, 0x2300)?;
    flags[1] |= r.at(renderer, 0x2304)?;
    if r.byte(props, 0x4a)? == 0 {
        flags[0] |= 0x4000000;
    }
    if r.byte(r.pointer(renderer, 0x1c)?, 4)? != 0 {
        flags[1] |= 0x200000;
    }
    let virtual_mode = r.at(renderer, 0x24bc)?;
    let (vi, mut pi) = if virtual_mode == 1 {
        (0xf, 0x156)
    } else {
        (r.at(implementation, 0x30)?, r.at(implementation, 0x34)?)
    };
    let (vs, vertex_key) = select_shader(r, masks, mask_count, table, vi, flags)?;
    let mut ps_flags = flags;
    if special && r.at(options, 0x3b8)? != 1 {
        ps_flags[1] |= 0x80000000;
        pi = if virtual_mode == 1 {
            0x156
        } else if virtual_mode != 0 {
            r.at(implementation, 0x34)?
        } else {
            0x15e
        };
    }
    let (ps, pixel_key) = select_shader(r, masks, mask_count, table, pi, ps_flags)?;
    Some(Selection {
        node,
        sub,
        props,
        texture: r.pointer(props, 0x10c)?,
        vs,
        ps,
        flags,
        vertex_key,
        pixel_key,
    })
}
