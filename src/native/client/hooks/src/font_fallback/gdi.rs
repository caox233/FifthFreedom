//! Generate missing BMP CJK outlines through the local Windows font service.
//! No font files or user strings are read, persisted, logged, or redistributed.
use super::codec::{Contour,Edge,Font,Glyph};
use std::collections::BTreeSet;
#[repr(C)] #[derive(Clone,Copy)] struct Fixed { fraction:u16, whole:i16 }
#[repr(C)] struct Matrix { m11:Fixed,m12:Fixed,m21:Fixed,m22:Fixed }
#[repr(C)] #[derive(Default)] struct Metrics { width:u32,height:u32,x:i32,y:i32,advance_x:i16,advance_y:i16 }
#[link(name="gdi32")]
extern "system" {
    fn CreateCompatibleDC(dc:usize)->usize;
    fn CreateFontW(height:i32,width:i32,angle:i32,orient:i32,weight:i32,italic:u32,underline:u32,strike:u32,
        charset:u32,out_precision:u32,clip:u32,quality:u32,pitch:u32,face:*const u16)->usize;
    fn SelectObject(dc:usize,object:usize)->usize;
    fn DeleteObject(object:usize)->i32;
    fn DeleteDC(dc:usize)->i32;
    fn GetGlyphIndicesW(dc:usize,text:*const u16,count:i32,indices:*mut u16,flags:u32)->u32;
    fn GetGlyphOutlineW(dc:usize,code:u32,format:u32,metrics:*mut Metrics,bytes:u32,data:*mut u8,matrix:*const Matrix)->u32;
}
struct Context { dc:usize, font:usize, old:usize }
impl Drop for Context {fn drop(&mut self){unsafe{if self.old!=0{SelectObject(self.dc,self.old);}DeleteObject(self.font);DeleteDC(self.dc);}}}
impl Context {
    fn new() -> Option<Self> {unsafe{
        for face in ["Microsoft YaHei","SimSun","Microsoft JhengHei"] {
            let dc=CreateCompatibleDC(0);if dc==0{return None;}
            let text:Vec<u16>=face.encode_utf16().chain(Some(0)).collect();
            let font=CreateFontW(-256,0,0,0,400,0,0,0,1,0,0,0,0,text.as_ptr());
            if font==0{DeleteDC(dc);continue;}
            let old=SelectObject(dc,font);
            if old==0||old==usize::MAX{DeleteObject(font);DeleteDC(dc);continue;}
            let ctx=Self{dc,font,old};let cp=[0x65e0,0x8bed];let mut ids=[0xffff;2];
            if GetGlyphIndicesW(dc,cp.as_ptr(),2,ids.as_mut_ptr(),1)!=u32::MAX && ids.iter().all(|&i|i!=0xffff) {return Some(ctx);}
        }
        None
    }}
    fn glyph(&self, cp:u16)->Option<Glyph>{unsafe{
        let zero=Fixed{fraction:0,whole:0};let one=Fixed{fraction:0,whole:1};
        let matrix=Matrix{m11:one,m12:zero,m21:zero,m22:one};let mut metrics=Metrics::default();
        let bytes=GetGlyphOutlineW(self.dc,u32::from(cp),0x102,&mut metrics,0,std::ptr::null_mut(),&matrix);
        if bytes==0||bytes>128*1024{return None;}
        let mut raw=vec![0;bytes as usize];
        if GetGlyphOutlineW(self.dc,u32::from(cp),0x102,&mut metrics,bytes,raw.as_mut_ptr(),&matrix)!=bytes{return None;}
        decode(cp,metrics.advance_x,&raw)
    }}
}
fn word(raw:&[u8],p:usize)->Option<u16>{Some(u16::from_le_bytes(raw.get(p..p+2)?.try_into().ok()?))}
fn dword(raw:&[u8],p:usize)->Option<u32>{Some(u32::from_le_bytes(raw.get(p..p+4)?.try_into().ok()?))}
fn point(raw:&[u8],p:usize)->Option<[i32;2]>{
    let fixed=|p|->Option<i32>{let fraction=word(raw,p)?;let whole=word(raw,p+2)? as i16;
        Some((f64::from(whole)+f64::from(fraction)/65536.).round_ties_even() as i32)};
    Some([fixed(p)?,-fixed(p+4)?])
}
pub fn decode(cp:u16,advance:i16,raw:&[u8])->Option<Glyph>{
    if raw.len()>128*1024||advance<=0{return None;}
    let mut contours=Vec::new();let mut pos=0;
    let mut bounds=[i32::MAX,i32::MAX,i32::MIN,i32::MIN];
    let mut include=|p:[i32;2]|{bounds[0]=bounds[0].min(p[0]);bounds[1]=bounds[1].min(p[1]);bounds[2]=bounds[2].max(p[0]);bounds[3]=bounds[3].max(p[1]);};
    while pos<raw.len() {
        let len=dword(raw,pos)? as usize;
        if len<16||pos.checked_add(len)?>raw.len()||dword(raw,pos+4)?!=24||contours.len()>=256{return None;}
        let end=pos+len;let start=point(raw,pos+8)?;include(start);
        let mut cursor=pos+16;let mut edges=Vec::new();let mut endpoint=start;
        while cursor<end {
            let kind=word(raw,cursor)?;let count=word(raw,cursor+2)? as usize;cursor+=4;
            if count==0||cursor.checked_add(count*8)?>end{return None;}
            let points:Vec<[i32;2]>=(0..count).map(|i|point(raw,cursor+i*8)).collect::<Option<_>>()?;
            cursor+=count*8;
            match kind {
                1=>for p in points {include(p);edges.push(Edge::Line(p));endpoint=p;},
                2 if count>=2=>for i in 0..count-1 {
                    let control=points[i];
                    let to=if i==count-2 {points[i+1]}else{
                        [((control[0]+points[i+1][0]) as f64/2.).round_ties_even() as i32,
                         ((control[1]+points[i+1][1]) as f64/2.).round_ties_even() as i32]};
                    include(control);include(to);edges.push(Edge::Quad(control,to));endpoint=to;
                },
                _=>return None,
            }
            if edges.len()>8192{return None;}
        }
        if edges.is_empty(){return None;}
        if endpoint!=start {edges.push(Edge::Line(start));}
        contours.push(Contour{start,edges});pos=end;
    }
    if contours.is_empty(){return None;}
    Some(Glyph{code:cp,advance,bounds,contours})
}
pub fn missing(existing:&BTreeSet<u16>)->Option<Vec<Glyph>> {
    let context=Context::new()?;
    collect(&context,existing)
}
fn collect(context:&Context,existing:&BTreeSet<u16>)->Option<Vec<Glyph>> {
    let codes:Vec<u16>=(0x3400..=0x9fff).filter(|c|!existing.contains(c)).collect();
    let mut indices=vec![0xffff;codes.len()];
    if codes.is_empty(){return Some(Vec::new());}
    if unsafe{GetGlyphIndicesW(context.dc,codes.as_ptr(),codes.len() as i32,indices.as_mut_ptr(),1)}==u32::MAX {return None;}
    let mut glyphs=Vec::new();
    for (cp,id) in codes.into_iter().zip(indices) {
        if id==0xffff{continue;}
        // Missing or unsupported outlines are left absent, never aliased to another character.
        if let Some(glyph)=context.glyph(cp) {glyphs.push(glyph);}
    }
    Some(glyphs)
}
#[derive(Clone,Copy,Debug)]
struct Calibration { source:i16,target:i16,baseline:i32 }
impl Calibration {
    fn from_samples(samples:&[(i16,[i32;4],i16,[i32;4])])->Option<Self> {
        if samples.len()<8{return None;}
        let (target,_,source,_)=samples[0];
        if target<=0||source<=0{return None;}
        let scale=f64::from(target)/f64::from(source);
        if !(0.25..=4.).contains(&scale){return None;}
        let mut shifts=Vec::with_capacity(samples.len());
        for &(native_advance,native,system_advance,system) in samples {
            if native_advance!=target||system_advance!=source
                ||native[0]>=native[2]||native[1]>=native[3]
                ||system[0]>=system[2]||system[1]>=system[3] {return None;}
            let shift=f64::from(native[3])-(f64::from(system[3])*scale).round_ties_even();
            if shift.abs()>256. {return None;}
            shifts.push(shift as i32);
        }
        shifts.sort_unstable();
        // A shared baseline, never an individual stretch/translation per character.
        let n=shifts.len();
        let baseline=((f64::from(shifts[(n-1)/2])+f64::from(shifts[n/2]))/2.).round_ties_even() as i32;
        Some(Self{source,target,baseline})
    }
    fn point(self,p:[i32;2])->Option<[i32;2]> {
        let scale=f64::from(self.target)/f64::from(self.source);
        let q=[(f64::from(p[0])*scale).round_ties_even(),
            (f64::from(p[1])*scale).round_ties_even()+f64::from(self.baseline)];
        if !q.iter().all(|v|(-16384.0..16384.0).contains(v)){return None;}
        Some([q[0] as i32,q[1] as i32])
    }
    fn apply(self,mut glyph:Glyph)->Option<Glyph> {
        if glyph.advance!=self.source||glyph.contours.is_empty(){return None;}
        let mut bounds=[i32::MAX,i32::MAX,i32::MIN,i32::MIN];
        let mut point=|p:[i32;2]|->Option<[i32;2]>{
            let q=self.point(p)?;
            bounds[0]=bounds[0].min(q[0]);bounds[1]=bounds[1].min(q[1]);
            bounds[2]=bounds[2].max(q[0]);bounds[3]=bounds[3].max(q[1]);Some(q)
        };
        for contour in &mut glyph.contours {
            contour.start=point(contour.start)?;
            for edge in &mut contour.edges {
                *edge=match *edge {
                    Edge::Line(p)=>Edge::Line(point(p)?),
                    Edge::Quad(control,end)=>Edge::Quad(point(control)?,point(end)?),
                };
            }
        }
        glyph.bounds=bounds;glyph.advance=self.target;Some(glyph)
    }
}
pub fn missing_calibrated(font:&Font<'_>)->Option<Vec<Glyph>> {
    let context=Context::new()?;
    // These shared CJK anchors establish the retail cell and baseline. They are
    // not a supplement whitelist: every supported missing CJK glyph is generated.
    let mut samples=Vec::new();
    for cp in "田中口日目木山大".encode_utf16() {
        let (advance,bounds)=font.metrics(cp)?;
        let reference=context.glyph(cp)?;
        samples.push((advance,bounds,reference.advance,reference.bounds));
    }
    let calibration=Calibration::from_samples(&samples)?;
    collect(&context,&font.codes)?.into_iter().map(|g|calibration.apply(g)).collect()
}
#[cfg(test)]
mod tests{
    use super::*;
    #[test] fn malformed_gdi_outline_is_rejected(){for len in 0..48{assert!(decode(0x65e0,256,&vec![0;len]).is_none());}}
    #[test] fn cjk_font_has_real_distinct_simplified_outlines(){
        let Some(ctx)=Context::new()else{return;};
        let a=ctx.glyph(0x65e0).unwrap();let b=ctx.glyph(0x8bed).unwrap();
        assert!(!a.contours.is_empty());assert_ne!(a.contours,b.contours);
        assert!(super::super::codec::encode(&a).is_some());
    }
    #[test] fn calibration_scales_curves_and_cell_with_one_baseline() {
        let samples=vec![(192,[0,-180,190,0],256,[0,-200,255,32]);8];
        let c=Calibration::from_samples(&samples).unwrap();
        assert_eq!(c.baseline,-24);
        let glyph=Glyph{code:0x65e0,advance:256,bounds:[0,-200,240,32],
            contours:vec![Contour{start:[0,32],edges:vec![Edge::Quad([240,-200],[0,-200]),Edge::Line([0,32])]}]};
        let g=c.apply(glyph).unwrap();
        assert_eq!(g.advance,192);assert_eq!(g.bounds,[0,-174,180,0]);
        assert_eq!(g.contours[0].edges[0],Edge::Quad([180,-174],[0,-174]));
        assert!(super::super::codec::encode(&g).is_some());
    }
    #[test] fn calibration_rejects_inconsistent_or_unbounded_metrics() {
        let mut samples=vec![(192,[0,-180,190,0],256,[0,-200,255,32]);8];
        assert!(Calibration::from_samples(&samples[..7]).is_none());
        samples[0].0=0;assert!(Calibration::from_samples(&samples).is_none());
        samples[0].0=191;assert!(Calibration::from_samples(&samples).is_none());
        samples[0].0=192;samples[0].1[3]=2000;
        assert!(Calibration::from_samples(&samples).is_none());
        assert!(Calibration{source:256,target:192,baseline:0}.point([i32::MAX,0]).is_none());
    }
}
