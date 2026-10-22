//! Retail compact-font supplement. Existing glyph bytes/metrics remain native.
//! Only called before the font is published; inserting sorted glyphs changes indices.
use std::collections::BTreeSet;
pub const MAX_BYTES: usize = 16 * 1024 * 1024;
const FACE: &[u8] = b"DFPHeiMediumB5\0";

#[derive(Clone, Debug, PartialEq)]
pub enum Edge { Line([i32; 2]), Quad([i32; 2], [i32; 2]) }
#[derive(Clone, Debug, PartialEq)]
pub struct Contour { pub start: [i32; 2], pub edges: Vec<Edge> }
#[derive(Clone, Debug)]
pub struct Glyph { pub code: u16, pub advance: i16, pub bounds: [i32; 4], pub contours: Vec<Contour> }
pub struct Font<'a> {
    bytes: &'a [u8], header: usize, table: usize, count: usize,
    pub codes: BTreeSet<u16>,
}
fn u16_at(b: &[u8], n: usize) -> Option<u16> { Some(u16::from_le_bytes(b.get(n..n+2)?.try_into().ok()?)) }
fn u32_at(b: &[u8], n: usize) -> Option<u32> { Some(u32::from_le_bytes(b.get(n..n+4)?.try_into().ok()?)) }
pub fn parse(bytes: &[u8]) -> Option<Font<'_>> {
    if bytes.len() > MAX_BYTES || !bytes.starts_with(FACE) { return None; }
    let header = FACE.len();
    if u16_at(bytes, header+2)? != 256 { return None; }
    let count = u32_at(bytes, header+10)? as usize;
    if count == 0 || count > 65535 { return None; }
    let table = (u32_at(bytes, header+14)? as usize).checked_add(header+18)?;
    let end = table.checked_add(count.checked_mul(8)?)?;
    if end >= bytes.len() { return None; }
    let mut codes = BTreeSet::new();
    let mut previous = None;
    for i in 0..count {
        let record = table+i*8;
        let code = u16_at(bytes, record)?;
        let offset = u32_at(bytes, record+4)? as usize;
        if previous.is_some_and(|p| p >= code) || !(header+18..table).contains(&offset) { return None; }
        codes.insert(code); previous=Some(code);
    }
    // Preserve the kerning block byte-for-byte, but validate its encoded length.
    let first = bytes[end];
    let width = (first & 3) as usize + 1;
    let mut kern = (first >> 2) as usize;
    for i in 1..width { kern |= (*bytes.get(end+i)? as usize) << (6+8*(i-1)); }
    if end.checked_add(width)?.checked_add(kern.checked_mul(6)?)? != bytes.len() { return None; }
    Some(Font {bytes, header, table, count, codes})
}
fn signed15(out: &mut Vec<u8>, n: i32) -> Option<()> {
    if !(-16384..16384).contains(&n) { return None; }
    if (-64..64).contains(&n) { out.push((n << 1) as u8); }
    else { out.extend_from_slice(&[((n << 1) as u8)|1, (n >> 7) as u8]); }
    Some(())
}
fn unsigned15(out: &mut Vec<u8>, n: usize) -> Option<()> {
    if n >= 32768 { return None; }
    if n < 128 { out.push((n << 1) as u8); }
    else { out.extend_from_slice(&[((n << 1) as u8)|1, (n >> 7) as u8]); }
    Some(())
}
fn unsigned30(out: &mut Vec<u8>, n: usize) -> Option<()> {
    if n >= 1 << 30 { return None; }
    let width = (1..=4).find(|w| n < 1usize << (w*8-2))?;
    out.extend_from_slice(&(((n as u32) << 2) | (width-1) as u32).to_le_bytes()[..width]);
    Some(())
}
fn packed(out: &mut Vec<u8>, kind: u8, deltas: &[i32], bits: usize) -> Option<()> {
    // Largest quadratic uses 80 bits. Avoid platform-width dependent packing.
    let mut raw = [0u8; 10]; raw[0]=kind; let mut offset=4;
    for &n in deltas {
        if n < -(1 << (bits-1)) || n >= 1 << (bits-1) { return None; }
        for bit in 0..bits {
            if ((n as u32) >> bit)&1 != 0 { raw[(offset+bit)/8] |= 1 << ((offset+bit)%8); }
        }
        offset+=bits;
    }
    out.extend_from_slice(&raw[..offset.div_ceil(8)]); Some(())
}
pub fn encode(g: &Glyph) -> Option<Vec<u8>> {
    if g.contours.is_empty() || g.contours.len() > 256 { return None; }
    let mut out=Vec::new();
    for n in g.bounds { signed15(&mut out,n)?; }
    unsigned15(&mut out,g.contours.len())?;
    for c in &g.contours {
        if c.edges.is_empty() || c.edges.len()>8192 { return None; }
        let [mut x,mut y]=c.start;
        signed15(&mut out,x)?; signed15(&mut out,y)?;
        unsigned30(&mut out,c.edges.len()*2)?;
        for edge in &c.edges {
            match *edge {
                Edge::Line([a,b]) => { packed(&mut out,7,&[a.checked_sub(x)?,b.checked_sub(y)?],18)?; x=a;y=b; }
                Edge::Quad([a,b],[e,f]) => {
                    packed(&mut out,15,&[a.checked_sub(x)?,b.checked_sub(y)?,e.checked_sub(a)?,f.checked_sub(b)?],19)?;
                    x=e;y=f;
                }
            }
        }
        if out.len()>128*1024 { return None; }
    }
    Some(out)
}
impl Font<'_> {
    /// Metrics of an existing glyph, without changing its encoded outline.
    pub fn metrics(&self, code: u16) -> Option<(i16, [i32; 4])> {
        let (mut low, mut high) = (0, self.count);
        while low < high {
            let mid = low + (high - low) / 2;
            let record = self.table + mid * 8;
            match u16_at(self.bytes, record)?.cmp(&code) {
                std::cmp::Ordering::Less => low = mid + 1,
                std::cmp::Ordering::Greater => high = mid,
                std::cmp::Ordering::Equal => {
                    let advance = u16_at(self.bytes, record + 2)? as i16;
                    let mut cursor = u32_at(self.bytes, record + 4)? as usize;
                    let shapes = self.bytes.get(..self.table)?;
                    let mut bounds = [0; 4];
                    for value in &mut bounds {
                        let first = *shapes.get(cursor)?;
                        cursor += 1;
                        *value = if first & 1 == 0 {
                            i32::from((first as i8) >> 1)
                        } else {
                            let second = *shapes.get(cursor)?;
                            cursor += 1;
                            let raw = i32::from(first >> 1) | (i32::from(second) << 7);
                            if raw >= 16384 { raw - 32768 } else { raw }
                        };
                    }
                    if advance <= 0 || bounds[0] > bounds[2] || bounds[1] > bounds[3] {
                        return None;
                    }
                    return Some((advance, bounds));
                }
            }
        }
        None
    }
    pub fn supplement(&self, glyphs: &[Glyph]) -> Option<Vec<u8>> {
        let mut added=Vec::new(); let mut codes=self.codes.clone();
        let mut extension=Vec::new();
        for g in glyphs {
            if codes.contains(&g.code) { continue; }
            if !(0x3400..=0x9fff).contains(&g.code) { return None; }
            let encoded=encode(g)?;
            let offset=self.table.checked_add(extension.len())?;
            if offset.checked_add(encoded.len())? > MAX_BYTES { return None; }
            extension.extend_from_slice(&encoded);
            added.push((g.code,g.advance,offset as u32)); codes.insert(g.code);
        }
        if added.is_empty() || self.count+added.len()>65535 { return None; }
        let end=self.table+self.count*8;
        let mut records: Vec<(u16,i16,u32)>=(0..self.count).map(|i| {
            let p=self.table+i*8;
            (u16_at(self.bytes,p).unwrap(),u16_at(self.bytes,p+2).unwrap() as i16,u32_at(self.bytes,p+4).unwrap())
        }).collect();
        records.extend(added);records.sort_unstable_by_key(|g|g.0);
        let size=self.table.checked_add(extension.len())?.checked_add(records.len()*8)?.checked_add(self.bytes.len()-end)?;
        if size>MAX_BYTES { return None; }
        let mut out=Vec::with_capacity(size);out.extend_from_slice(&self.bytes[..self.table]);
        out[self.header+10..self.header+14].copy_from_slice(&(records.len() as u32).to_le_bytes());
        let blob_size=(self.table-(self.header+18)+extension.len()) as u32;
        out[self.header+14..self.header+18].copy_from_slice(&blob_size.to_le_bytes());
        out.extend(extension);
        for (cp,advance,offset) in records {out.extend(cp.to_le_bytes());out.extend(advance.to_le_bytes());out.extend(offset.to_le_bytes());}
        out.extend_from_slice(&self.bytes[end..]);
        parse(&out)?; Some(out)
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    fn original() -> Vec<u8> {
        let mut b=FACE.to_vec();b.extend([0,0x60,0,1]);b.extend([0;6]);b.extend(1u32.to_le_bytes());
        b.extend(9u32.to_le_bytes());b.extend([0,0,2,2,2,0,0,8,0]);
        b.extend(0x7121u16.to_le_bytes());b.extend(256i16.to_le_bytes());b.extend(((FACE.len()+18) as u32).to_le_bytes());b.push(0);b
    }
    fn glyph() -> Glyph {Glyph{code:0x65e0,advance:256,bounds:[0,-200,200,0],contours:vec![Contour{start:[0,0],edges:vec![Edge::Line([200,0]),Edge::Quad([200,-200],[0,-200]),Edge::Line([0,0])]}]}}
    #[test] fn inserts_sorted_and_preserves_native_bytes_and_metrics() {
        let b=original();let f=parse(&b).unwrap();let g=glyph();let out=f.supplement(&[g]).unwrap();let next=parse(&out).unwrap();
        assert_eq!(&out[..f.header+10],&b[..f.header+10]);assert_eq!(&out[f.header+18..f.table],&b[f.header+18..f.table]);
        assert_eq!(&out[next.table+8..next.table+16],&b[f.table..f.table+8]);assert_eq!(next.codes.len(),2);
    }
    #[test] fn malformed_or_foreign_font_is_unchanged() {
        let b=original();for n in 0..b.len(){assert!(parse(&b[..n]).is_none());}
        let mut other=b.clone();other[0]=b'X';assert!(parse(&other).is_none());
        let mut wrong_em=b;wrong_em[FACE.len()+2]=1;assert!(parse(&wrong_em).is_none());
    }
    #[test] fn no_replacement_of_existing_unicode() {
        let b=original();let mut g=glyph();g.code=0x7121;assert!(parse(&b).unwrap().supplement(&[g]).is_none());
    }
    #[test] fn invalid_geometry_rejects_whole_supplement() {
        let b=original();let mut g=glyph();g.bounds[0]=i32::MIN;assert!(parse(&b).unwrap().supplement(&[g]).is_none());
    }
    #[test] fn metrics_reads_native_and_extended_signed_coordinates() {
        let b=original();let f=parse(&b).unwrap();
        assert_eq!(f.metrics(0x7121),Some((256,[0,0,1,1])));
        assert_eq!(f.metrics(0x65e0),None);
        let out=f.supplement(&[glyph()]).unwrap();
        assert_eq!(parse(&out).unwrap().metrics(0x65e0),Some((256,[0,-200,200,0])));
    }
}
