//! Allocate before releasing old bytes. Both buffers belong to the native heap.
pub type Parse = unsafe extern "thiscall" fn(usize,u32)->u32;
pub type Allocate = unsafe extern "thiscall" fn(usize,usize,u32,*const u32)->usize;
pub type Free = unsafe extern "thiscall" fn(usize,usize);
pub struct Api { pub heap:usize,pub allocate:Allocate,pub free:Free,pub parse:Parse }
#[derive(Debug,PartialEq)] pub enum Outcome { Applied(u32), AllocationFailed, ParseRejected }
/// Caller proves a fresh compact font and readable/writable native array layout.
pub unsafe fn replace(reader:usize,data:&[u8],api:&Api)->Outcome{
    let array=(reader-12) as *mut u32;
    let old=[array.read_unaligned(),array.add(1).read_unaligned(),array.add(2).read_unaligned()];
    let stat=0x105;
    let new=(api.allocate)(api.heap,array as usize,data.len() as u32,&stat);
    if new==0 || new==old[0] as usize{return Outcome::AllocationFailed;}
    std::ptr::copy_nonoverlapping(data.as_ptr(),new as *mut u8,data.len());
    array.write_unaligned(new as u32);array.add(1).write_unaligned(data.len() as u32);array.add(2).write_unaligned(data.len() as u32);
    let used=(api.parse)(reader,0);
    if used==data.len() as u32 {
        (api.free)(api.heap,old[0] as usize);
        Outcome::Applied(used)
    }else{
        for(i,v)in old.into_iter().enumerate(){array.add(i).write_unaligned(v);}
        (api.parse)(reader,0);
        (api.free)(api.heap,new);
        Outcome::ParseRejected
    }
}
