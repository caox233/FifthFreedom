//! Simplified glyphs in the retail Traditional Chinese font, before publication.
//! Scoped to the verified compact-font load callsite; no user-text rewriting.
mod codec;
mod gdi;
mod transaction;
use crate::input_mouse::memory::{readable,word,signature,patch};
use std::sync::{OnceLock,atomic::{AtomicBool,Ordering}};
static ACTIVE:AtomicBool=AtomicBool::new(false);
static PATCH:OnceLock<[u8;5]>=OnceLock::new();
static CACHE:OnceLock<Option<(Vec<u8>,Vec<u8>)>>=OnceLock::new();
const SITE:usize=0x23eee1a;
const ORIGINAL:[u8;5]=[0xe8,0xe6,0xf7,0xff,0xff];
const PARSE:usize=0x23ee605;
unsafe fn native(reader:usize,offset:u32)->u32{
    let f:transaction::Parse=std::mem::transmute(PARSE);f(reader,offset)
}
unsafe fn fresh<'a>(reader:usize,offset:u32)->Option<&'a [u8]>{
    let font=reader.checked_sub(0x30)?;
    if offset!=0||!readable(font,0x6c)||word(font,0)?!=0x2d39ec0
        ||word(reader,0)?!=0x2d39eb8||word(reader,8)? as usize!=reader-12
        ||word(reader,0x20)?!=0 {return None;}
    let address=word(font,0x24)? as usize;let len=word(font,0x28)? as usize;let cap=word(font,0x2c)? as usize;
    if len>cap||len!=664431||!readable(address,len)
        || !signature(address,len,"b27d71133fc72d845ec8d929582a8a92cb6f05766f70c87aa0f3a61714cb2c2a"){return None;}
    let bytes=std::slice::from_raw_parts(address as *const u8,len);
    codec::parse(bytes)?;Some(bytes)
}
unsafe extern "thiscall" fn loaded(reader:usize,offset:u32)->u32{
    if !ACTIVE.load(Ordering::Acquire){return native(reader,offset);}
    let Some(source)=fresh(reader,offset)else{return native(reader,offset);};
    let cached=CACHE.get_or_init(||{
        let font=codec::parse(source)?;
        let glyphs=gdi::missing_calibrated(&font)?;
        let supplemented=font.supplement(&glyphs)?;
        let count=codec::parse(&supplemented)?.codes.len()-font.codes.len();
        tracing::info!("SCBL_FONT_GLYPHS generated={} runtime_system_font=true text_conversion=false",count);
        Some((source.to_vec(),supplemented))
    });
    let Some((original,supplemented))=cached else{return native(reader,offset);};
    if source!=original{return native(reader,offset);}
    let Some(heap)=word(0x34cc66c,0).map(|v|v as usize)else{return native(reader,offset);};
    let Some(vtable)=word(heap,0).map(|v|v as usize)else{return native(reader,offset);};
    let Some(free)=word(vtable,0x30).map(|v|v as usize).filter(|&v|readable(v,1))else{return native(reader,offset);};
    let Some(allocate)=word(vtable,0x38).map(|v|v as usize).filter(|&v|readable(v,1))else{return native(reader,offset);};
    let api=transaction::Api{heap,allocate:std::mem::transmute(allocate),free:std::mem::transmute(free),parse:std::mem::transmute(PARSE)};
    match transaction::replace(reader,supplemented,&api) {
        transaction::Outcome::Applied(n)=>{tracing::info!("SCBL_FONT_GLYPHS native_font_loaded=true");n},
        transaction::Outcome::AllocationFailed=>{tracing::warn!("SCBL_FONT_GLYPHS allocation_failed_native_preserved=true");native(reader,offset)},
        transaction::Outcome::ParseRejected=>{tracing::warn!("SCBL_FONT_GLYPHS parse_rejected_native_restored=true");source.len() as u32},
    }
}
pub(crate) unsafe fn init(){
    if ACTIVE.load(Ordering::Acquire){return;}
    // Exact build and original call ownership. Different native font APIs stay untouched.
    if !signature(PARSE,0x167,"49f9db6f81a0e2f198f03787fc8f40919ff268940fe3c11f08c367dcd8a7a692")
        || !signature(0x23ecc77,0x4e,"81911c4c06f42da4bee0ddaed031e7df05e609e919898c868ef04196eb77b89c")
        || !signature(0x23eed4a,0x16c,"7b1c04760ce7b53d910073f303011d800cf4c4d68e5cc25c75bc2b85291996a6") {
        tracing::warn!("SCBL_FONT_GLYPHS unsupported_native_font_build=true");return;
    }
    let mut replacement=ORIGINAL;
    replacement[1..].copy_from_slice(&((loaded as *const () as usize).wrapping_sub(SITE+5) as u32).to_le_bytes());
    if PATCH.set(replacement).is_err(){return;}
    ACTIVE.store(true,Ordering::Release);
    if !patch(SITE,&ORIGINAL,&replacement){ACTIVE.store(false,Ordering::Release);tracing::warn!("SCBL_FONT_GLYPHS callsite_not_owned=true");}
}
pub(crate) unsafe fn deinit(){
    ACTIVE.store(false,Ordering::Release);
    if let Some(replacement)=PATCH.get(){let _=patch(SITE,replacement,&ORIGINAL);}
    // Published glyph buffers remain owned by the native font and its destructor.
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    #[ignore="requires the locally extracted, hash-pinned retail fonts_cht body"]
    fn native_stock_font_gate_and_lifetime() {
        let path=std::env::var("SCBL_FONT_FALLBACK_FIXTURE").expect("set the retained compact-font fixture path");
        let mut body=std::fs::read(path).unwrap();
        let mut font=[0u32;27];let base=font.as_mut_ptr() as usize;let reader=base+0x30;
        font[0]=0x2d39ec0;font[0x24/4]=body.as_mut_ptr() as u32;
        font[0x28/4]=body.len() as u32;font[0x2c/4]=body.len() as u32;
        font[0x30/4]=0x2d39eb8;font[0x38/4]=(base+0x24) as u32;
        unsafe{
            assert_eq!(fresh(reader,0).unwrap().len(),664431);
            assert!(fresh(reader,1).is_none());
            font[0x50/4]=1;assert!(fresh(reader,0).is_none());font[0x50/4]=0;
            font[0x38/4]+=4;assert!(fresh(reader,0).is_none());font[0x38/4]-=4;
            font[0]=0;assert!(fresh(reader,0).is_none());font[0]=0x2d39ec0;
            font[0x2c/4]-=1;assert!(fresh(reader,0).is_none());font[0x2c/4]+=1;
            body[20]^=1;assert!(fresh(reader,0).is_none());body[20]^=1;
            assert!(fresh(reader,0).is_some());
        }
    }
}
