//! Immutable GPU content proof; camera ownership and shader proof are separate.
use sha2::{Digest, Sha256};
use std::{
    marker::PhantomData,
    rc::Rc,
    thread::{self, ThreadId},
};
use windows::{
    core::Interface,
    Win32::Graphics::{
        Direct3D::{D3D11_SRV_DIMENSION_TEXTURE2D, D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST},
        Direct3D11::*,
        Dxgi::Common::{DXGI_FORMAT, DXGI_FORMAT_BC3_UNORM, DXGI_FORMAT_R16_UINT},
    },
};
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Proof {
    Unknown,
    Mismatch,
    ContentOnly,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DrawRange {
    pub indices: u32,
    pub start: u32,
    pub base: i32,
}
/// Explicit inputs of the selected current native draw. Not context-bound state.
pub struct CurrentDraw<'a> {
    pub vb: &'a ID3D11Buffer,
    pub ib: &'a ID3D11Buffer,
    pub srv: &'a ID3D11ShaderResourceView,
    pub stride: u32,
    pub offset: u32,
    pub index_offset: u32,
    pub format: DXGI_FORMAT,
    pub topology: windows::Win32::Graphics::Direct3D::D3D_PRIMITIVE_TOPOLOGY,
    pub range: DrawRange,
}
impl DrawRange {
    fn captured(self) -> bool {
        self == Self {
            indices: 42,
            start: 0,
            base: 0,
        }
    }
}
struct Entry {
    vb: ID3D11Buffer,
    ib: ID3D11Buffer,
    tex: ID3D11Texture2D,
    proof: Proof,
}
pub struct IdentityCache {
    entries: Vec<Entry>,
    device: Option<ID3D11Device>,
    thread: Option<ThreadId>,
    pub readbacks: u32,
    pub hits: u32,
    _not_send: PhantomData<Rc<()>>,
}
impl Default for IdentityCache {
    fn default() -> Self {
        Self {
            entries: Vec::with_capacity(4),
            device: None,
            thread: None,
            readbacks: 0,
            hits: 0,
            _not_send: PhantomData,
        }
    }
}
impl IdentityCache {
    pub fn reset(&mut self) {
        *self = Self::default();
    }
    /// Caller owns the immediate render context. No concurrent context access.
    pub unsafe fn bound(
        &mut self,
        ctx: &ID3D11DeviceContext,
        draw: DrawRange,
        budget: &mut u32,
    ) -> Proof {
        self.inspect(ctx, draw, budget).unwrap_or(Proof::Unknown)
    }
    unsafe fn inspect(
        &mut self,
        ctx: &ID3D11DeviceContext,
        draw: DrawRange,
        budget: &mut u32,
    ) -> Option<Proof> {
        if self.thread.is_some_and(|id| id != thread::current().id())
            || !draw.captured()
            || ctx.GetType() != D3D11_DEVICE_CONTEXT_IMMEDIATE
        {
            return None;
        }
        let device = ctx.GetDevice().ok()?;
        if self.device.as_ref().is_some_and(|d| !crate::scis_gpu_device::same(d, &device)) {
            return None;
        }
        let (mut vb, mut ib, mut stride, mut offset, mut index_offset, mut format) =
            (None, None, 0, 0, 0, DXGI_FORMAT::default());
        ctx.IAGetVertexBuffers(0, 1, Some(&mut vb), Some(&mut stride), Some(&mut offset));
        ctx.IAGetIndexBuffer(Some(&mut ib), Some(&mut format), Some(&mut index_offset));
        let (vb, ib) = (vb?, ib?);
        if stride != 32
            || offset != 0
            || index_offset != 0
            || format != DXGI_FORMAT_R16_UINT
            || ctx.IAGetPrimitiveTopology() != D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST
            || !valid_buffer(&vb, 512, D3D11_BIND_VERTEX_BUFFER)
            || !valid_buffer(&ib, 84, D3D11_BIND_INDEX_BUFFER)
        {
            return None;
        }
        let mut srv = [None];
        ctx.PSGetShaderResources(0, Some(&mut srv));
        let srv = srv[0].take()?;
        let mut desc = D3D11_SHADER_RESOURCE_VIEW_DESC::default();
        srv.GetDesc(&mut desc);
        if desc.Format != DXGI_FORMAT_BC3_UNORM
            || desc.ViewDimension != D3D11_SRV_DIMENSION_TEXTURE2D
            || desc.Anonymous.Texture2D.MostDetailedMip != 0
            || desc.Anonymous.Texture2D.MipLevels != 1
        {
            return None;
        }
        let tex: ID3D11Texture2D = srv.GetResource().ok()?.cast().ok()?;
        self.current(ctx, &vb, &ib, &tex, budget)
    }
    /// Validate explicitly supplied current consumed resources, not whichever
    /// buffers happen to remain bound from an unrelated or previous draw.
    pub unsafe fn current_draw(
        &mut self,
        ctx: &ID3D11DeviceContext,
        input: CurrentDraw<'_>,
        budget: &mut u32,
    ) -> Proof {
        // No extra reads outside the opt-in bounded recorder. This is purely
        // observational: it cannot spend proof budget, populate cache or authorize
        // projection. Six rows are admitted/dropped as one unit, not independently.
        crate::scis_diagnostics::trace_bundle(crate::scis_diagnostics::RETICLE_GPU_DETAIL,
            || self.describe(ctx, &input, *budget));
        if self.thread.is_some_and(|id| id != thread::current().id())
            || ctx.GetType() != D3D11_DEVICE_CONTEXT_IMMEDIATE
            || !input.range.captured()
            || input.stride != 32
            || input.offset != 0
            || input.index_offset != 0
            || input.format != DXGI_FORMAT_R16_UINT
            || input.topology != D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST
        {
            return Proof::Unknown;
        }
        let mut desc = D3D11_SHADER_RESOURCE_VIEW_DESC::default();
        input.srv.GetDesc(&mut desc);
        if desc.Format != DXGI_FORMAT_BC3_UNORM
            || desc.ViewDimension != D3D11_SRV_DIMENSION_TEXTURE2D
            || desc.Anonymous.Texture2D.MostDetailedMip != 0
            || desc.Anonymous.Texture2D.MipLevels != 1
        {
            return Proof::Unknown;
        }
        let Some(tex) = input
            .srv
            .GetResource()
            .ok()
            .and_then(|r| r.cast::<ID3D11Texture2D>().ok())
        else {
            return Proof::Unknown;
        };
        self.current(ctx, input.vb, input.ib, &tex, budget)
            .unwrap_or(Proof::Unknown)
    }
    unsafe fn describe(&self, ctx:&ID3D11DeviceContext, input:&CurrentDraw<'_>, budget:u32)
        -> [[u32;16];6] {
        use std::{hash::{Hash,Hasher},collections::hash_map::DefaultHasher,
            sync::atomic::{AtomicU32,Ordering}};
        static SEQUENCE:AtomicU32=AtomicU32::new(0);
        const UNREAD:u32=0x7fff_ffff;
        let sequence=SEQUENCE.fetch_add(1,Ordering::Relaxed).wrapping_add(1);
        let mut rows=[[UNREAD;16];6];
        for row in &mut rows { row[0]=1;row[1]=sequence; }
        let mut mask=0u32;
        macro_rules! reject {($bit:expr,$bad:expr)=>{if $bad {mask|=1u32<<$bit;}};}
        let same_thread=self.thread.is_none_or(|id| id==thread::current().id());
        rows[0][2]=ctx.as_raw() as usize as u32; rows[0][3]=same_thread as u32;
        rows[0][7]=self.device.as_ref().map_or(0,|d|d.as_raw() as usize as u32);
        rows[1][2]=input.vb.as_raw() as usize as u32;
        rows[1][3]=input.ib.as_raw() as usize as u32;
        rows[1][4]=input.srv.as_raw() as usize as u32;
        rows[1][6..14].copy_from_slice(&[input.stride,input.offset,input.index_offset,
            input.format.0 as u32,input.topology.0 as u32,input.range.indices,input.range.start,input.range.base as u32]);
        rows[2][8..12].copy_from_slice(&[budget,self.readbacks,self.hits,self.entries.len() as u32]);
        let mut current_hash=DefaultHasher::new();thread::current().id().hash(&mut current_hash);
        let current_hash=current_hash.finish();rows[2][13]=current_hash as u32;rows[2][14]=(current_hash>>32)as u32;
        if let Some(id)=self.thread {let mut h=DefaultHasher::new();id.hash(&mut h);let h=h.finish();rows[3][8]=h as u32;rows[3][9]=(h>>32)as u32;}
        reject!(0,!same_thread);reject!(2,!input.range.captured());reject!(3,input.stride!=32);
        reject!(4,input.offset!=0);reject!(5,input.index_offset!=0);
        reject!(6,input.format!=DXGI_FORMAT_R16_UINT);reject!(7,input.topology!=D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        rows[1][14]=mask;rows[1][15]=0;
        // Never touch an immediate context or any COM object on a rejected thread.
        if !same_thread {return rows;}
        let kind=ctx.GetType();rows[0][4]=kind.0 as u32;
        reject!(1,kind!=D3D11_DEVICE_CONTEXT_IMMEDIATE);
        rows[1][14]=mask;
        if kind!=D3D11_DEVICE_CONTEXT_IMMEDIATE {return rows;}
        let device=ctx.GetDevice();
        match &device { Ok(d)=>{rows[0][5]=0;rows[0][6]=d.as_raw() as usize as u32;
            reject!(9,self.device.as_ref().is_some_and(|old|!crate::scis_gpu_device::same(old,d)));},
            Err(e)=>{rows[0][5]=e.code().0 as u32;reject!(8,true);} }
        let mut vd=D3D11_BUFFER_DESC::default();let mut id=D3D11_BUFFER_DESC::default();
        input.vb.GetDesc(&mut vd);input.ib.GetDesc(&mut id);
        rows[2][2..8].copy_from_slice(&buffer_fields(&vd));rows[3][2..8].copy_from_slice(&buffer_fields(&id));
        reject!(16,!buffer_matches(&vd,512,D3D11_BIND_VERTEX_BUFFER));
        reject!(17,!buffer_matches(&id,84,D3D11_BIND_INDEX_BUFFER));
        for (result,slot,error_bit,mismatch_bit) in [(input.vb.GetDevice(),8,19,22),(input.ib.GetDevice(),10,20,23)] {
            match result {Ok(d)=>{rows[0][slot]=0;rows[0][slot+1]=d.as_raw() as usize as u32;
                reject!(mismatch_bit,device.as_ref().is_ok_and(|expected|!crate::scis_gpu_device::same(expected,&d)));},
                Err(e)=>{rows[0][slot]=e.code().0 as u32;reject!(error_bit,true);} }
        }
        let mut sd=D3D11_SHADER_RESOURCE_VIEW_DESC::default();input.srv.GetDesc(&mut sd);
        rows[4][2]=sd.Format.0 as u32;rows[4][3]=sd.ViewDimension.0 as u32;
        rows[4][4..8].copy_from_slice(std::slice::from_raw_parts((&sd.Anonymous as *const _)as*const u32,4));
        reject!(10,sd.Format!=DXGI_FORMAT_BC3_UNORM);
        reject!(11,sd.ViewDimension!=D3D11_SRV_DIMENSION_TEXTURE2D);
        if sd.ViewDimension==D3D11_SRV_DIMENSION_TEXTURE2D {
            reject!(12,sd.Anonymous.Texture2D.MostDetailedMip!=0);
            reject!(13,sd.Anonymous.Texture2D.MipLevels!=1);
        }
        match input.srv.GetResource() {
            Err(e)=>{rows[0][14]=e.code().0 as u32;reject!(14,true);},
            Ok(resource)=>{ rows[0][14]=0;
                match resource.cast::<ID3D11Texture2D>() {
                    Err(e)=>{rows[0][15]=e.code().0 as u32;reject!(15,true);},
                    Ok(tex)=>{rows[0][15]=0;rows[1][5]=tex.as_raw() as usize as u32;
                        let mut td=D3D11_TEXTURE2D_DESC::default();tex.GetDesc(&mut td);
                        rows[5][2..13].copy_from_slice(&texture_fields(&td));
                        reject!(18,!texture_matches(&td));
                        match tex.GetDevice(){Ok(d)=>{rows[0][12]=0;rows[0][13]=d.as_raw() as usize as u32;
                            reject!(24,device.as_ref().is_ok_and(|expected|!crate::scis_gpu_device::same(expected,&d)));},
                            Err(e)=>{rows[0][12]=e.code().0 as u32;reject!(21,true);} }
                        let cached=self.entries.iter().any(|e|&e.vb==input.vb&&&e.ib==input.ib&&e.tex==tex);
                        rows[2][12]=cached as u32;
                        if !cached {reject!(25,self.entries.len()==4);reject!(26,budget<3);}
                    }
                }
            }
        }
        rows[1][14]=mask; rows
    }
    unsafe fn current(
        &mut self,
        ctx: &ID3D11DeviceContext,
        vb: &ID3D11Buffer,
        ib: &ID3D11Buffer,
        tex: &ID3D11Texture2D,
        budget: &mut u32,
    ) -> Option<Proof> {
        if self.thread.is_some_and(|id| id != thread::current().id())
            || ctx.GetType() != D3D11_DEVICE_CONTEXT_IMMEDIATE
            || !valid_buffer(vb, 512, D3D11_BIND_VERTEX_BUFFER)
            || !valid_buffer(ib, 84, D3D11_BIND_INDEX_BUFFER)
        {
            return None;
        }
        let device = ctx.GetDevice().ok()?;
        if self.device.as_ref().is_some_and(|d| !crate::scis_gpu_device::same(d, &device)) {
            return None;
        }
        if !valid_texture(&tex)
            || !crate::scis_gpu_device::same(&vb.GetDevice().ok()?, &device)
            || !crate::scis_gpu_device::same(&ib.GetDevice().ok()?, &device)
            || !crate::scis_gpu_device::same(&tex.GetDevice().ok()?, &device)
        {
            return None;
        }
        if let Some(e) = self
            .entries
            .iter()
            .find(|e| &e.vb == vb && &e.ib == ib && &e.tex == tex)
        {
            self.hits = self.hits.saturating_add(1);
            return Some(e.proof);
        }
        // COM references prevent address reuse; immutable data cannot mutate. Bounded
        // saturation fails closed rather than evicting/re-reading on every draw.
        if self.entries.len() == 4 || *budget < 3 {
            return None;
        }
        *budget -= 3;
        self.device = Some(device.clone());
        self.thread = Some(thread::current().id());
        self.readbacks = self.readbacks.saturating_add(3);
        let v = read_buffer(ctx, &device, &vb, 512)?;
        let i = read_buffer(ctx, &device, &ib, 84)?;
        let t = read_texture(ctx, &device, &tex)?;
        let matched = matches_hash(
            &v,
            "428f6de1688943fac2d3c7ff08196fc08e9342313511003ebbce0d50cb16f9b2",
        ) && matches_hash(
            &i,
            "05270899decdcdffb4699a541c82af57762ef4406dc4798c75d5975a22d7c19f",
        ) && matches_hash(
            &t,
            "1bdcd0b08acfa061cd18bc7d692caf096fdf70b2123e285fba05cd204742caff",
        );
        let proof = if matched {
            Proof::ContentOnly
        } else {
            Proof::Mismatch
        };
        self.entries.push(Entry {
            vb: vb.clone(),
            ib: ib.clone(),
            tex: tex.clone(),
            proof,
        });
        Some(proof)
    }
}
unsafe fn valid_buffer(b: &ID3D11Buffer, bytes: u32, bind: D3D11_BIND_FLAG) -> bool {
    let mut d = D3D11_BUFFER_DESC::default();
    b.GetDesc(&mut d);
    buffer_matches(&d,bytes,bind)
}
fn buffer_fields(d:&D3D11_BUFFER_DESC)->[u32;6] {
    [d.ByteWidth,d.Usage.0 as u32,d.BindFlags,d.CPUAccessFlags,d.MiscFlags,d.StructureByteStride]
}
fn buffer_matches(d:&D3D11_BUFFER_DESC,bytes:u32,bind:D3D11_BIND_FLAG)->bool {
    d.ByteWidth == bytes
        && d.Usage == D3D11_USAGE_IMMUTABLE
        && d.BindFlags == bind.0 as u32
        && d.CPUAccessFlags == 0
        && d.MiscFlags == 0
        && d.StructureByteStride == 0
}
unsafe fn valid_texture(t: &ID3D11Texture2D) -> bool {
    let mut d = D3D11_TEXTURE2D_DESC::default();
    t.GetDesc(&mut d);
    texture_matches(&d)
}
fn texture_fields(d:&D3D11_TEXTURE2D_DESC)->[u32;11] {
    [d.Width,d.Height,d.MipLevels,d.ArraySize,d.Format.0 as u32,d.SampleDesc.Count,
        d.SampleDesc.Quality,d.Usage.0 as u32,d.BindFlags,d.CPUAccessFlags,d.MiscFlags]
}
fn texture_matches(d:&D3D11_TEXTURE2D_DESC)->bool {
    d.Width == 512
        && d.Height == 512
        && d.MipLevels == 1
        && d.ArraySize == 1
        && d.Format == DXGI_FORMAT_BC3_UNORM
        && d.SampleDesc.Count == 1
        && d.SampleDesc.Quality == 0
        && d.Usage == D3D11_USAGE_IMMUTABLE
        && d.BindFlags == D3D11_BIND_SHADER_RESOURCE.0 as u32
        && d.CPUAccessFlags == 0
        && d.MiscFlags == 0
}
pub(crate) fn matches_hash(bytes: &[u8], expected: &str) -> bool {
    format!("{:x}", Sha256::digest(bytes)) == expected
}
struct MapLease<'a> {
    ctx: &'a ID3D11DeviceContext,
    resource: ID3D11Resource,
}
impl Drop for MapLease<'_> {
    fn drop(&mut self) {
        unsafe {
            self.ctx.Unmap(&self.resource, 0);
        }
    }
}
unsafe fn read_buffer(
    ctx: &ID3D11DeviceContext,
    d: &ID3D11Device,
    b: &ID3D11Buffer,
    len: usize,
) -> Option<Vec<u8>> {
    let desc = D3D11_BUFFER_DESC {
        ByteWidth: len as u32,
        Usage: D3D11_USAGE_STAGING,
        CPUAccessFlags: D3D11_CPU_ACCESS_READ.0 as u32,
        ..Default::default()
    };
    let mut copy = None;
    d.CreateBuffer(&desc, None, Some(&mut copy)).ok()?;
    let copy = copy?;
    let resource = copy.cast().ok()?;
    ctx.CopyResource(&copy, b);
    let mut m = D3D11_MAPPED_SUBRESOURCE::default();
    ctx.Map(&copy, 0, D3D11_MAP_READ, 0, Some(&mut m)).ok()?;
    let _lease = MapLease { ctx, resource };
    if m.pData.is_null() {
        return None;
    }
    Some(std::slice::from_raw_parts(m.pData.cast::<u8>(), len).to_vec())
}
unsafe fn read_texture(
    ctx: &ID3D11DeviceContext,
    d: &ID3D11Device,
    t: &ID3D11Texture2D,
) -> Option<Vec<u8>> {
    let mut desc = D3D11_TEXTURE2D_DESC::default();
    t.GetDesc(&mut desc);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
    let mut copy = None;
    d.CreateTexture2D(&desc, None, Some(&mut copy)).ok()?;
    let copy = copy?;
    let resource = copy.cast().ok()?;
    ctx.CopyResource(&copy, t);
    let mut m = D3D11_MAPPED_SUBRESOURCE::default();
    ctx.Map(&copy, 0, D3D11_MAP_READ, 0, Some(&mut m)).ok()?;
    let _lease = MapLease { ctx, resource };
    if m.pData.is_null() || m.RowPitch < 2048 {
        return None;
    }
    let mut bytes = Vec::with_capacity(262144);
    for y in 0..128 {
        bytes.extend_from_slice(std::slice::from_raw_parts(
            m.pData.cast::<u8>().add(y * m.RowPitch as usize),
            2048,
        ));
    }
    Some(bytes)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn every_draw_field_checked() {
        let good = DrawRange {
            indices: 42,
            start: 0,
            base: 0,
        };
        assert!(good.captured());
        for bad in [
            DrawRange {
                indices: 39,
                ..good
            },
            DrawRange { start: 1, ..good },
            DrawRange { base: 1, ..good },
        ] {
            assert!(!bad.captured());
        }
    }
    #[test]
    fn different_content_cannot_reuse_hash() {
        assert!(matches_hash(
            b"abc",
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        ));
        assert!(!matches_hash(
            b"abd",
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        ));
    }
}
