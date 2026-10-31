//! Windows ownership/pipe endpoint for the Instance-owned read-only protocol.
//! Tokio's overlapped pipe owns its I/O buffers/handle; cancelling a future drops
//! no caller-owned OVERLAPPED storage. No detached read task outlives Instance.
use super::owned_read::{self as wire, Descriptor};
use sha2::{Digest, Sha256};
use std::{ffi::c_void, fs::{File, OpenOptions}, future::Future, io::{self, Read}, net::Ipv4Addr,
    os::windows::{fs::OpenOptionsExt, io::{AsRawHandle, FromRawHandle, OwnedHandle}}, sync::atomic::{AtomicBool,Ordering},time::Duration};
use tokio::io::AsyncReadExt;
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};
static CLAIMED: AtomicBool = AtomicBool::new(false);

pub struct Observation { pub name: String, pub guid: u128, pub luid: u64, pub index: u32 }
pub struct Owner { descriptor: Descriptor, parent: Parent, pipe: NamedPipeClient, challenge: wire::Challenge }
impl Owner {
    pub async fn prepare(instance: &str) -> io::Result<Option<Self>> {
        let Some(raw) = std::env::var_os(wire::ENVIRONMENT) else { return Ok(None) };
        if CLAIMED.swap(true,Ordering::AcqRel) { return Err(wire::rejected()); }
        let raw = raw.into_string().map_err(|_| wire::rejected())?;
        let descriptor = Descriptor::parse(&raw,instance)?;
        let parent = Parent::capture(&descriptor)?;
        // Identification SQOS prevents the pipe server impersonating this core.
        let mut pipe = ClientOptions::new().security_qos_flags(0x00010000).open(format!(r"\\.\pipe\{}",descriptor.pipe))?;
        parent.check_pipe(&pipe)?;
        let challenge = tokio::time::timeout(Duration::from_secs(10),async {
            let challenge: wire::Challenge = wire::read(&mut pipe).await?;
            challenge.validate(&descriptor)?;
            parent.check_pipe(&pipe)?;
            Ok::<_,io::Error>(challenge)
        }).await.map_err(|_| wire::rejected())??;
        Ok(Some(Self { descriptor,parent,pipe,challenge }))
    }
    pub async fn initialization_lost(&self) {
        // Before HELLO the owner must send no request. Readability therefore
        // means EOF, unsolicited bytes, or a spurious notification to retry.
        loop {
            tokio::select! {
                _ = wait_parent(&self.parent) => return,
                ready = self.pipe.readable() => {
                    if ready.is_err() { return; }
                    match self.pipe.try_read(&mut [0u8;1]) {
                        Err(error) if error.kind()==io::ErrorKind::WouldBlock => (),
                        _ => return,
                    }
                }
            }
        }
    }
    pub async fn serve<F,Fut>(&mut self, mut observe: F) -> io::Result<()>
    where F: FnMut(Ipv4Addr)->Fut, Fut: Future<Output=io::Result<Observation>> {
        self.parent.check_pipe(&self.pipe)?;
        tokio::time::timeout(Duration::from_secs(3),wire::write(&mut self.pipe,&wire::Hello::new(&self.descriptor,&self.challenge)))
            .await.map_err(|_|wire::rejected())??;
        let mut sequence = 0; let mut nonce = String::new();
        loop {
            // Idle has no observation authority. EOF is readable and immediately
            // ends this attempt. Parent loss is checked without a detached task.
            let first=tokio::select! {
                byte = self.pipe.read_u8() => byte?,
                _ = wait_parent(&self.parent) => return Err(wire::rejected()),
            };
            let deadline = tokio::time::Instant::now()+Duration::from_secs(3);
            let operation = async {
                self.parent.check_pipe(&self.pipe)?;
                let request: wire::Read = wire::read_after_first(&mut self.pipe,first).await?;
                let source = request.validate(&self.descriptor,sequence,&nonce)?;
                // The caller is the real Instance's current typed NIC accessor.
                let observation = observe(source).await;
                self.parent.check_pipe(&self.pipe)?;
                match observation {
                    Ok(s) => wire::write(&mut self.pipe,&wire::Observed::new(&self.descriptor,&request,&s.name,s.guid,s.luid,s.index)?).await?,
                    Err(_) => wire::write(&mut self.pipe,&wire::Unavailable::new(&self.descriptor,&request)).await?,
                }
                sequence=request.request; nonce=request.nonce;
                Ok::<(),io::Error>(())
            };
            tokio::select! {
                result=tokio::time::timeout_at(deadline,operation)=>result.map_err(|_|wire::rejected())??,
                _=wait_parent(&self.parent)=>return Err(wire::rejected()),
            }
        }
    }
}
async fn wait_parent(parent:&Parent) { while parent.alive() {tokio::time::sleep(Duration::from_millis(50)).await;} }
struct Parent { handle: OwnedHandle, pid:u32, _image:File }
impl Parent {
    fn capture(d:&Descriptor)->io::Result<Self> {
        unsafe {
            let snapshot=CreateToolhelp32Snapshot(2,0);
            if snapshot as isize == -1 {return Err(wire::rejected());}
            let snapshot=OwnedHandle::from_raw_handle(snapshot);
            let mut row: ProcessEntry=std::mem::zeroed(); row.size=std::mem::size_of::<ProcessEntry>() as u32;
            let current=GetCurrentProcessId(); let mut found=None;
            if Process32FirstW(snapshot.as_raw_handle(),&mut row)!=0 {loop {
                if row.pid==current {found=Some(row.parent);break;}
                if Process32NextW(snapshot.as_raw_handle(),&mut row)==0 {break;}
            }}
            if found != Some(d.parent_pid) {return Err(wire::rejected());}
            let handle=OpenProcess(0x00100000|0x1000,0,d.parent_pid);
            if handle.is_null(){return Err(wire::rejected());}
            let handle=OwnedHandle::from_raw_handle(handle);
            let (mut birth,mut exit,mut kernel,mut user)=(0u64,0u64,0u64,0u64);
            if GetProcessTimes(handle.as_raw_handle(),&mut birth,&mut exit,&mut kernel,&mut user)==0 || birth!=d.parent_birth
                || GetProcessId(handle.as_raw_handle())!=d.parent_pid || WaitForSingleObject(handle.as_raw_handle(),0)!=0x102 {return Err(wire::rejected());}
            let mut path=vec![0u16;32768];let mut length=path.len() as u32;
            if QueryFullProcessImageNameW(handle.as_raw_handle(),0,path.as_mut_ptr(),&mut length)==0 {return Err(wire::rejected());}
            let path=String::from_utf16(&path[..length as usize]).map_err(|_|wire::rejected())?;
            if !path.eq_ignore_ascii_case(&d.parent_path) {return Err(wire::rejected());}
            // The independently observed process image must match the explicit
            // launch anchor. This is not a publisher or server-signature proof.
            let mut image=OpenOptions::new().read(true).share_mode(1).open(&path)?;
            let length=image.metadata()?.len();
            if !(1..=256*1024*1024).contains(&length) {return Err(wire::rejected());}
            let mut digest=Sha256::new();let mut buffer=[0;65536];let mut total=0u64;
            loop {let n=image.read(&mut buffer)?;if n==0 {break;}total+=n as u64;if total>length{return Err(wire::rejected());}digest.update(&buffer[..n]);}
            if total!=length || format!("{:x}",digest.finalize())!=d.parent_sha256 {return Err(wire::rejected());}
            Ok(Self {handle,pid:d.parent_pid,_image:image})
        }
    }
    fn alive(&self)->bool {unsafe {WaitForSingleObject(self.handle.as_raw_handle(),0)==0x102}}
    fn check_pipe(&self,pipe:&NamedPipeClient)->io::Result<()> {
        let mut pid=0; if !self.alive() || unsafe {GetNamedPipeServerProcessId(pipe.as_raw_handle(),&mut pid)}==0 || pid!=self.pid {return Err(wire::rejected());} Ok(())
    }
}
#[repr(C)] struct ProcessEntry {size:u32,usage:u32,pid:u32,heap:usize,module:u32,threads:u32,parent:u32,priority:i32,flags:u32,name:[u16;260]}
#[link(name="kernel32")]
unsafe extern "system" {
    fn CreateToolhelp32Snapshot(flags:u32,pid:u32)->*mut c_void;
    fn Process32FirstW(snapshot:*mut c_void,row:*mut ProcessEntry)->i32;
    fn Process32NextW(snapshot:*mut c_void,row:*mut ProcessEntry)->i32;
    fn GetCurrentProcessId()->u32;
    fn OpenProcess(access:u32,inherit:i32,pid:u32)->*mut c_void;
    fn GetProcessId(process:*mut c_void)->u32;
    fn GetProcessTimes(process:*mut c_void,birth:*mut u64,exit:*mut u64,kernel:*mut u64,user:*mut u64)->i32;
    fn QueryFullProcessImageNameW(process:*mut c_void,flags:u32,path:*mut u16,length:*mut u32)->i32;
    fn WaitForSingleObject(handle:*mut c_void,timeout:u32)->u32;
    fn GetNamedPipeServerProcessId(pipe:*mut c_void,pid:*mut u32)->i32;
}
