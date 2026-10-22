//! Local IPC request metadata only. Never reads another process or starts a game.
use core::ffi::c_void;
const MAGIC: u32 = 0x5653454d;
#[derive(Clone, Copy, PartialEq, Eq)]
pub(super) struct Request { pub tick: u64, pub nonce: u32 }
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Error { Wait, Unreadable, Torn, Protocol, Expired }
fn parse(bytes: [u8; 32], pid: u32, now: u64) -> Result<Request, Error> {
    let word=|o|u32::from_le_bytes(bytes[o..o+4].try_into().unwrap());
    if word(0)!=MAGIC || word(4)!=1 || word(16)!=pid || word(20)==0 || word(24)!=0 || word(28)!=0 {
        return Err(Error::Protocol);
    }
    let tick=u64::from_le_bytes(bytes[8..16].try_into().unwrap());
    if now<tick || now-tick>=5000 {return Err(Error::Expired);}
    Ok(Request{tick,nonce:word(20)})
}
#[link(name="kernel32")]
extern "system" {
    fn CreateEventW(sa:*const c_void, manual:i32, initial:i32, name:*const u16)->*mut c_void;
    fn CreateFileMappingW(file:*mut c_void,sa:*const c_void,protection:u32,high:u32,low:u32,name:*const u16)->*mut c_void;
    fn MapViewOfFile(mapping:*mut c_void,access:u32,high:u32,low:u32,size:usize)->*mut c_void;
    fn UnmapViewOfFile(view:*const c_void)->i32;
    fn CloseHandle(handle:*mut c_void)->i32;
    fn WaitForSingleObject(handle:*mut c_void,timeout:u32)->u32;
    fn GetLastError()->u32;
    fn GetCurrentProcessId()->u32;
    fn GetCurrentProcess()->*mut c_void;
    fn ReadProcessMemory(process:*mut c_void,base:*const c_void,buffer:*mut c_void,size:usize,read:*mut usize)->i32;
}
pub(super) struct Ipc { event:usize, mapping:usize, view:usize, pid:u32 }
impl Drop for Ipc {
    fn drop(&mut self) { unsafe {
        if self.view!=0 {UnmapViewOfFile(self.view as *const _);}
        if self.mapping!=0 {CloseHandle(self.mapping as *mut _);}
        if self.event!=0 {CloseHandle(self.event as *mut _);}
    }}
}
impl Ipc {
    pub(super) fn create()->Option<Self> {
        let pid=unsafe{GetCurrentProcessId()};
        Self::named(pid,&format!("Local\\SCBL_VSS_EMF_WITNESS_{pid}"),&format!("Local\\SCBL_VSS_EMF_WITNESS_REQUEST_{pid}"))
    }
    fn named(pid:u32,event_name:&str,mapping_name:&str)->Option<Self> {
        let wide=|s:&str|s.encode_utf16().chain(Some(0)).collect::<Vec<_>>();
        let (event_name,mapping_name)=(wide(event_name),wide(mapping_name));
        let mut result=Self{event:0,mapping:0,view:0,pid};
        unsafe {
            result.event=CreateEventW(core::ptr::null(),0,0,event_name.as_ptr()) as usize;
            if result.event==0 || GetLastError()==183 {return None;}
            result.mapping=CreateFileMappingW(usize::MAX as *mut _,core::ptr::null(),4,0,32,mapping_name.as_ptr()) as usize;
            if result.mapping==0 || GetLastError()==183 {return None;}
            result.view=MapViewOfFile(result.mapping as *mut _,4,0,0,32) as usize;
            if result.view==0 {return None;}
        }
        Some(result)
    }
    fn copy(&self)->Result<[u8;32],Error> {
        let(mut bytes,mut count)=([0u8;32],0usize);
        let ok=unsafe{ReadProcessMemory(GetCurrentProcess(),self.view as *const _,bytes.as_mut_ptr().cast(),32,&mut count)};
        if ok==0 || count!=32 {return Err(Error::Unreadable);}
        Ok(bytes)
    }
    pub(super) fn take(&self,now:u64)->Result<Option<Request>,Error> {
        match unsafe{WaitForSingleObject(self.event as *mut _,0)} {
            258=>return Ok(None), 0=>{}, _=>return Err(Error::Wait),
        }
        let a=self.copy()?;let b=self.copy()?;
        if a!=b {return Err(Error::Torn);}
        parse(a,self.pid,now).map(Some)
    }
}
#[cfg(test)] mod tests {
    use super::*;
    fn request(tick:u64)->[u8;32] {
        let mut b=[0;32];b[0..4].copy_from_slice(&MAGIC.to_le_bytes());b[4..8].copy_from_slice(&1u32.to_le_bytes());
        b[8..16].copy_from_slice(&tick.to_le_bytes());b[16..20].copy_from_slice(&123u32.to_le_bytes());b[20..24].copy_from_slice(&7u32.to_le_bytes());b
    }
    #[test] fn timestamp_limits_apply_at_consumption() {
        assert!(parse(request(100),123,100).is_ok());assert!(parse(request(100),123,5099).is_ok());
        assert!(matches!(parse(request(100),123,5100),Err(Error::Expired)));
        assert!(matches!(parse(request(100),123,99),Err(Error::Expired)));
    }
    #[test] fn protocol_pid_nonce_and_reserved_are_fail_closed() {
        for (offset,value) in [(0,0),(4,2),(16,124),(20,0),(24,1),(28,1)] {
            let mut b=request(100);b[offset..offset+4].copy_from_slice(&u32::to_le_bytes(value));
            assert!(matches!(parse(b,123,101),Err(Error::Protocol)));
        }
    }
    #[test] fn fixture_names_never_open_production_event_and_start_unsignaled() {
        let id=unsafe{GetCurrentProcessId()};let suffix=format!("{id}_request_unit");
        let a=format!("Local\\SCBL_VSS_EMF_WITNESS_SELFTEST_{suffix}");
        let b=format!("Local\\SCBL_VSS_EMF_WITNESS_SELFTEST_MAP_{suffix}");
        let ipc=Ipc::named(id,&a,&b).unwrap();assert!(matches!(ipc.take(0),Ok(None)));
        assert!(Ipc::named(id,&a,&b).is_none());drop(ipc);
        assert!(Ipc::named(id,&a,&b).is_some());
    }
}
