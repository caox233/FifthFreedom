//! Bounded first-chance observations, NOT a crash verdict. No dump, strings,
//! register values, stack contents, debugger, or exception interception.
//! Fixed buffers and a preopened file keep allocator failures out of tracing.
use std::{ffi::c_void, fmt::{self, Write}, os::windows::io::IntoRawHandle,
    path::Path, sync::atomic::{AtomicBool, AtomicPtr, AtomicU32, Ordering}};

#[repr(C)]
struct Record { code:u32, flags:u32, nested:*mut Record, address:*mut c_void,
    count:u32, information:[usize;15] }
#[repr(C)]
struct Pointers { record:*mut Record, context:*mut c_void }
#[repr(C)]
#[derive(Default)]
struct Region { base:usize, allocation:usize, allocation_protect:u32,
    size:usize, state:u32, protect:u32, kind:u32 }
#[link(name="kernel32")]
extern "system" {
    fn AddVectoredExceptionHandler(first:u32,handler:unsafe extern "system" fn(*mut Pointers)->i32)->*mut c_void;
    fn RemoveVectoredExceptionHandler(handle:*mut c_void)->u32;
    fn GetCurrentProcessId()->u32;
    fn GetCurrentThreadId()->u32;
    fn GetSystemTimeAsFileTime(time:*mut u64);
    fn RtlCaptureStackBackTrace(skip:u32,count:u32,frames:*mut *mut c_void,hash:*mut u32)->u16;
    fn VirtualQuery(address:*const c_void,region:*mut Region,size:usize)->usize;
    fn ReadProcessMemory(process:*mut c_void,address:*const c_void,buffer:*mut c_void,size:usize,read:*mut usize)->i32;
    fn WriteFile(handle:*mut c_void,data:*const u8,size:u32,written:*mut u32,overlapped:*mut c_void)->i32;
    fn FlushFileBuffers(handle:*mut c_void)->i32;
    fn SetFilePointerEx(handle:*mut c_void,distance:i64,new_position:*mut i64,method:u32)->i32;
    fn CloseHandle(handle:*mut c_void)->i32;
}
static FILE:AtomicPtr<c_void>=AtomicPtr::new(std::ptr::null_mut());
static HANDLER:AtomicPtr<c_void>=AtomicPtr::new(std::ptr::null_mut());
static BUSY:AtomicBool=AtomicBool::new(false);
static COUNT:AtomicU32=AtomicU32::new(0);
struct Buffer { bytes:[u8;4096], len:usize }
impl Write for Buffer {
    fn write_str(&mut self,s:&str)->fmt::Result {
        if self.len+s.len()>self.bytes.len(){return Err(fmt::Error);}
        self.bytes[self.len..self.len+s.len()].copy_from_slice(s.as_bytes());self.len+=s.len();Ok(())
    }
}
fn important(code:u32)->bool {
    matches!(code,0xc0000005|0xc000001d|0xc0000094|0xc0000096|0xc0000409|0xc0000374)
}
// Only image identity and relative code location. Unknown/private/JIT addresses
// become zeros: no heap address, path or arbitrary memory is serialized.
unsafe fn location(address:*mut c_void)->(u32,u32,u32) {
    let mut r=Region::default();
    if VirtualQuery(address,&mut r,std::mem::size_of::<Region>())==0 || r.kind!=0x1000000 || r.allocation==0 {return (0,0,0);}
    let base=r.allocation;
    let mut header=Region::default();
    if VirtualQuery(base as _,&mut header,std::mem::size_of::<Region>())==0 || header.state!=0x1000 || header.protect&0x101!=0 || header.size<0x100 {return (0,0,0);}
    let mut dos=[0u32;16];let mut read=0usize;
    if ReadProcessMemory(-1isize as _,base as _,dos.as_mut_ptr() as _,64,&mut read)==0 || read!=64 || dos[0]&0xffff!=0x5a4d{return (0,0,0);}
    let pe=dos[15] as usize;
    if pe>header.size.saturating_sub(0x60) || pe>0x100000{return (0,0,0);}
    let mut nt=[0u32;24];
    if ReadProcessMemory(-1isize as _,(base+pe) as _,nt.as_mut_ptr() as _,96,&mut read)==0 || read!=96 || nt[0]!=0x4550{return (0,0,0);}
    let stamp=nt[2];let size=nt[20];
    let rva=(address as usize).wrapping_sub(base);
    if rva>=size as usize{return (0,0,0);}
    (stamp,size,rva as u32)
}
// CONTEXT_CONTROL for x86, checked against the local Windows SDK winnt.h.
// Read only the fixed prefix through SegSs (204 bytes). None of these raw
// register values or stack words are written to the diagnostic file.
unsafe fn fault_context(out:&mut Buffer,context:*mut c_void) {
    #[cfg(target_arch="x86")]
    {
        let mut c=[0u32;51];let mut read=0usize;
        if context.is_null() || ReadProcessMemory(-1isize as _,context,c.as_mut_ptr() as _,std::mem::size_of_val(&c),&mut read)==0
            || read!=std::mem::size_of_val(&c) || c[0]&0x10001!=0x10001 {
            let _=out.write_str("\"FaultContext\":{\"Captured\":false}");return;
        }
        let (t,s,r)=location(c[46] as usize as _);
        let _=write!(out,"\"FaultContext\":{{\"Captured\":true,\"Instruction\":[{},{},{}],\"ReturnCandidates\":[",t,s,r);
        let low:usize;let high:usize;
        std::arch::asm!("mov {0:e}, fs:[8]",out(reg)low,options(nostack,readonly,preserves_flags));
        std::arch::asm!("mov {0:e}, fs:[4]",out(reg)high,options(nostack,readonly,preserves_flags));
        let sp=c[49] as usize;let mut bp=c[45] as usize;let mut count=0;
        // No stack scan: at most the fault ESP return slot and seven monotonic
        // EBP links, all on this exception thread's TIB-bounded committed stack.
        if stack_contains(sp,4,low,high) {
            if let Some(words)=stack_words(sp,1,low,high) {
                append_return_candidate(out,words[0],"stack-top-candidate",&mut count);
            }
            for _ in 0..7 {
                if bp<sp {break;}
                let Some(words)=stack_words(bp,2,low,high) else {break;};
                append_return_candidate(out,words[1],"frame-chain-candidate",&mut count);
                let next=words[0] as usize;
                if next<=bp || next-bp>65536 {break;}
                bp=next;
            }
        }
        let _=out.write_str("]}");
    }
    #[cfg(not(target_arch="x86"))]
    {let _=context;let _=out.write_str("\"FaultContext\":{\"Captured\":false}");}
}
fn stack_contains(address:usize,bytes:usize,low:usize,high:usize)->bool {
    low<high && address>=low && address%4==0 && address.checked_add(bytes).is_some_and(|end|end<=high)
}
unsafe fn stack_words(address:usize,count:usize,low:usize,high:usize)->Option<[u32;2]> {
    if !(1..=2).contains(&count) || !stack_contains(address,count*4,low,high){return None;}
    let mut region=Region::default();
    if VirtualQuery(address as _,&mut region,std::mem::size_of::<Region>())==0 || region.state!=0x1000
        || region.protect&0x101!=0 || !region.base.checked_add(region.size).is_some_and(|end|address+count*4<=end){return None;}
    let mut words=[0u32;2];let mut read=0usize;
    if ReadProcessMemory(-1isize as _,address as _,words.as_mut_ptr() as _,count*4,&mut read)==0 || read!=count*4{return None;}
    Some(words)
}
unsafe fn append_return_candidate(out:&mut Buffer,address:u32,source:&str,count:&mut usize) {
    if *count>=8{return;}
    let mut region=Region::default();
    if VirtualQuery(address as usize as _,&mut region,std::mem::size_of::<Region>())==0 || region.kind!=0x1000000
        || region.state!=0x1000 || region.protect&0x101!=0 || region.protect&0xf0==0{return;}
    let (t,s,r)=location(address as usize as _);if s==0{return;}
    // This is NOT an unwound/confirmed caller: x86 FPO and corrupted stacks can
    // leave plausible code values. Keep the provenance explicit for triage.
    let _=write!(out,"{}{{\"Source\":\"{}\",\"Location\":[{},{},{}]}}",if *count==0{""}else{","},source,t,s,r);
    *count+=1;
}

unsafe extern "system" fn observe(p:*mut Pointers)->i32 {
    if p.is_null() || (*p).record.is_null() {return 0;}
    let record=&*(*p).record;
    if !important(record.code) || BUSY.swap(true,Ordering::Acquire){return 0;}
    let file=FILE.load(Ordering::Acquire);
    let index=COUNT.fetch_add(1,Ordering::Relaxed);
    if !file.is_null() {
        let mut time=0u64;GetSystemTimeAsFileTime(&mut time);
        let mut frames=[std::ptr::null_mut();24];
        let count=RtlCaptureStackBackTrace(0,frames.len() as u32,frames.as_mut_ptr(),std::ptr::null_mut()) as usize;
        let mut b=Buffer{bytes:[0;4096],len:0};
        let (stamp,size,rva)=location(record.address);
        let access=if record.code==0xc0000005 && record.count>=2 {match record.information[0]{0=>0,1=>1,8=>8,_=>9}} else {9};
        let null_access=record.code==0xc0000005 && record.count>=2 && record.information[1]<65536;
        let _=write!(b,"{{\"Schema\":2,\"Pid\":{},\"ThreadId\":{},\"UtcFileTime\":{},\"Sequence\":{},\"Code\":{},\"Access\":{},\"NearNull\":{},\"Location\":[{},{},{}],\"Frames\":[",GetCurrentProcessId(),GetCurrentThreadId(),time,index,record.code,access,null_access,stamp,size,rva);
        for (i,&frame) in frames.iter().take(count.min(frames.len())).enumerate(){
            let (t,s,a)=location(frame);let _=write!(b,"{}[{},{},{}]",if i==0{""}else{","},t,s,a);
        }
        let _=b.write_str("],\"FramesKind\":\"observer-stack\",");
        fault_context(&mut b,(*p).context);
        let _=b.write_str("}");
        b.bytes[b.len..].fill(b' ');b.bytes[4095]=b'\n';
        // Eight fixed slots retain the latest observations even if the game
        // handles earlier exceptions. At most 32 KiB per process.
        if SetFilePointerEx(file,(index%8) as i64*4096,std::ptr::null_mut(),0)!=0 {
            let mut written=0;let _=WriteFile(file,b.bytes.as_ptr(),4096,&mut written,std::ptr::null_mut());
            let _=FlushFileBuffers(file);
        }
    }
    BUSY.store(false,Ordering::Release);
    0 // EXCEPTION_CONTINUE_SEARCH: never changes the game's exception handling.
}
pub fn init(directory:&Path)->bool {
    if !HANDLER.load(Ordering::Acquire).is_null(){return true;}
    // Keep separate identities when the launcher observes more than one game
    // process. CreateNew refuses overwrite and symlink-following an old record.
    let path=directory.join(format!("exception-observations-{}.jsonl",unsafe{GetCurrentProcessId()}));
    let Ok(file)=std::fs::OpenOptions::new().write(true).create_new(true).open(path) else{return false;};
    let handle=file.into_raw_handle();FILE.store(handle,Ordering::Release);
    let handler=unsafe{AddVectoredExceptionHandler(0,observe)};
    if handler.is_null(){FILE.store(std::ptr::null_mut(),Ordering::Release);unsafe{CloseHandle(handle);};return false;}
    HANDLER.store(handler,Ordering::Release);true
}
pub unsafe fn deinit(){
    let handler=HANDLER.swap(std::ptr::null_mut(),Ordering::AcqRel);
    if !handler.is_null(){RemoveVectoredExceptionHandler(handler);}
    // Do not race a worker already inside the callback. OS closes a retained
    // handle on process exit; unloading while a handler runs is unsupported.
    if !BUSY.swap(true,Ordering::Acquire){let file=FILE.swap(std::ptr::null_mut(),Ordering::AcqRel);if !file.is_null(){CloseHandle(file);}BUSY.store(false,Ordering::Release);}
}
