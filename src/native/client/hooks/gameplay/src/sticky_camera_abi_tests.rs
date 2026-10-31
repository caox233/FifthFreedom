//! Owned x86 machine-code caller/receiver for the production Disable detour.
//! The dispatcher has an independent ECX + one DWORD / ret 4 contract and calls
//! a fabricated object's +4D0 slot. It records, then repairs, its own stack.
//! An earlier fault inside the old Rust trampoline caller is observed only
//! when the armed native leaf recorded the exact known wrong-call conditions;
//! the child exits without resuming and the parent reports a failed assertion.
//! No retail image/instruction body, game process, input, sound or capture.
use super::*;
use std::ffi::c_void;
use std::sync::atomic::AtomicUsize;
use windows::Win32::System::Diagnostics::Debug::EXCEPTION_POINTERS;

const TEST: &str = "sticky_camera_runtime::abi_replay::camera_disable_native_stack_contract";
const CHILD: &str = "FF_CAMERA_ABI_CHILD";
const VALUES: [u32;4] = [0x13579bdf, 0, u32::MAX, 0x2468ace0];
const SAVED: [u32;3] = [0x11335577,0x22446688,0x336699cc];
const GUARDS: [u32;2] = [0x5aa55aa5,0xa55aa55a];
static ARMED:AtomicBool=AtomicBool::new(false);
static CASE_TRACE:AtomicUsize=AtomicUsize::new(0);
static CASE_OBJECT:AtomicUsize=AtomicUsize::new(0);
static CASE_COUNT:AtomicUsize=AtomicUsize::new(0);
static COMPLETED:AtomicUsize=AtomicUsize::new(0);
static CASE_STAGE:AtomicUsize=AtomicUsize::new(0);
static CASE_REPEAT:AtomicUsize=AtomicUsize::new(0);
static CASE_RECEIVER:AtomicUsize=AtomicUsize::new(0);
static CASE_ARGUMENT:AtomicUsize=AtomicUsize::new(0);
static CLEANUP_CORRECT:AtomicBool=AtomicBool::new(false);
static PRIOR_FAILURES:AtomicUsize=AtomicUsize::new(0);
static CASE_THREAD:AtomicUsize=AtomicUsize::new(0);

#[link(name="kernel32")]
extern "system" {
    fn VirtualAlloc(p:*mut c_void,n:usize,kind:u32,protect:u32)->*mut c_void;
    fn VirtualProtect(p:*mut c_void,n:usize,protect:u32,old:*mut u32)->i32;
    fn FlushInstructionCache(process:*mut c_void,p:*const c_void,n:usize)->i32;
    fn GetCurrentProcess()->*mut c_void;
    fn AddVectoredExceptionHandler(first:u32,handler:unsafe extern "system" fn(*mut EXCEPTION_POINTERS)->i32)->*mut c_void;
    fn SetErrorMode(mode:u32)->u32;
    fn GetStdHandle(kind:u32)->*mut c_void;
    fn WriteFile(handle:*mut c_void,data:*const u8,size:u32,written:*mut u32,overlapped:*mut c_void)->i32;
    fn ExitProcess(code:u32)->!;
    fn GetCurrentThreadId()->u32;
}

// No heap allocation, lock, log framework, unwind or context modification in
// the exception callback. This is an isolated fixture-only observation gate.
struct Output{bytes:[u8;1024],used:usize}
impl std::fmt::Write for Output{
    fn write_str(&mut self,s:&str)->std::fmt::Result{
        let end=self.used.checked_add(s.len()).ok_or(std::fmt::Error)?;
        if end>self.bytes.len(){return Err(std::fmt::Error);}
        self.bytes[self.used..end].copy_from_slice(s.as_bytes());self.used=end;Ok(())
    }
}
unsafe extern "system" fn observe_fault(p:*mut EXCEPTION_POINTERS)->i32{
    if CASE_THREAD.load(Ordering::SeqCst)!=GetCurrentThreadId()as usize
        ||!ARMED.swap(false,Ordering::SeqCst)||p.is_null(){return 0;}
    let record=(*p).ExceptionRecord;
    if record.is_null(){return 0;}
    let r=&*record;
    if r.ExceptionCode.0 as u32!=0xc0000005||r.NumberParameters!=2||r.ExceptionInformation[0]!=8{return 0;}
    let context=(*p).ContextRecord;
    if context.is_null(){return 0;}
    let eip=(*context).Eip;
    if eip as usize!=r.ExceptionAddress as usize||eip as usize!=r.ExceptionInformation[1]{return 0;}
    let trace=CASE_TRACE.load(Ordering::SeqCst)as*const u32;
    if trace.is_null(){return 0;}
    let object=CASE_OBJECT.load(Ordering::SeqCst)as u32;
    let argument=CASE_ARGUMENT.load(Ordering::SeqCst)as u32;
    let received=*trace.add(2);
    let count=(*trace).wrapping_sub(CASE_COUNT.load(Ordering::SeqCst)as u32);
    let cleanup=CLEANUP_CORRECT.load(Ordering::SeqCst);
    let completed=COMPLETED.load(Ordering::SeqCst);
    let stage=CASE_STAGE.load(Ordering::SeqCst);
    let repeat=CASE_REPEAT.load(Ordering::SeqCst);
    let receiver=CASE_RECEIVER.load(Ordering::SeqCst);
    // Never classify an arbitrary crash, a wrong receiver, a repeated native
    // call or a failure before the known first hooked boundary as this defect.
    if completed!=16||stage!=1||repeat!=0||receiver!=0||argument!=VALUES[0]
        ||count!=1||*trace.add(1)!=object||received==argument||!cleanup
        ||PRIOR_FAILURES.load(Ordering::SeqCst)!=0{return 0;}
    use std::fmt::Write;
    let mut out=Output{bytes:[0;1024],used:0};
    if writeln!(out,"CAMERA_ABI_ABORT {{\"schema\":1,\"completed_cases\":{completed},\"stage\":{stage},\"repeat\":{repeat},\"receiver\":{receiver},\"argument\":{argument},\"received_argument\":{received},\"native_count_delta\":{count},\"receiver_correct\":true,\"cleanup_correct\":true,\"previous_cases_validated\":true,\"prior_failures\":0,\"cleanup_calls\":17,\"exception_code\":3221225477,\"access\":8,\"eip\":{eip},\"exception_address\":{},\"access_target\":{},\"child_exit_code\":86,\"context_resumed\":false,\"game_executed\":false}}",r.ExceptionAddress as usize,r.ExceptionInformation[1]).is_err(){return 0;}
    let mut written=0;
    if WriteFile(GetStdHandle((-11i32)as u32),out.bytes.as_ptr(),out.used as u32,&mut written,std::ptr::null_mut())==0||written!=out.used as u32{return 0;}
    ExitProcess(86)
}

fn imm(code:&mut Vec<u8>,value:u32){code.extend(value.to_le_bytes());}
fn write_const(code:&mut Vec<u8>,offset:u8,value:u32){code.extend([0xc7,0x42,offset]);imm(code,value);}

// The independent cdecl caller preserves its original callee-saved registers.
// args: target, fabricated receiver, opaque DWORD, output[10]. Callee contract
// is thiscall(ret 4) for dispatch and thiscall(ret 0) for Cleanup.
fn caller(one_argument:bool)->Vec<u8>{
    let mut c=vec![0x55,0x89,0xe5,0x53,0x56,0x57,0x83,0xec,0x08,
        0x8b,0x55,0x14,0x89,0x22,0x89,0x6a,0x14,0xc7,0x04,0x24];
    imm(&mut c,GUARDS[0]);c.extend([0xc7,0x44,0x24,0x04]);imm(&mut c,GUARDS[1]);
    for (opcode,value) in [(0xbb,SAVED[0]),(0xbe,SAVED[1]),(0xbf,SAVED[2])]{c.push(opcode);imm(&mut c,value);}
    c.extend([0x8b,0x4d,0x0c]); // ECX = receiver, never an invented first stack arg.
    if one_argument{c.extend([0xff,0x75,0x10]);}
    c.extend([0xff,0x55,0x08,0x8b,0x55,0x14,0x89,0x62,0x04,
        0x89,0x5a,0x08,0x89,0x72,0x0c,0x89,0x7a,0x10,0x89,0x6a,0x18,
        0x89,0x42,0x24,0x8b,0x04,0x24,0x89,0x42,0x1c,
        0x8b,0x44,0x24,0x04,0x89,0x42,0x20,
        0x8d,0x65,0xf4,0x5f,0x5e,0x5b,0x5d,0xc3]);c
}

// Synthetic virtual dispatcher, not EDA030's instruction body. Its known ABI
// is sufficient to expose the production hook's missing callee-pop. Record
// the REAL returned ESP before restoring this fixture's saved EBP frame.
fn dispatcher()->Vec<u8>{
    let mut c=vec![0x55,0x89,0xe5,0x53,0x56,0x57,0x83,0xec,0x04,
        0x89,0x4d,0xf0,0x89,0xce,0xbb];imm(&mut c,SAVED[0]);
    c.push(0xbf);imm(&mut c,SAVED[2]);
    c.extend([0x8b,0x51,0x04,0x89,0x62,0x14,0x8b,0x45,0x08,
        0x8b,0x11,0x50,0xff,0x92,0xd0,0x04,0x00,0x00,
        0x8b,0x55,0xf0,0x8b,0x52,0x04,0x89,0x62,0x18,0x89,0x42,0x1c,
        0x89,0x5a,0x20,0x89,0x72,0x24,0x89,0x7a,0x28,
        0x8d,0x65,0xf4,0x5f,0x5e,0x5b,0x5d,0xc2,0x04,0x00]);c
}

// Three fabricated native early-return paths all have the same ret 4. The
// original caller argument is read independently from [EBP+8]. These branches
// are test cases, not a reconstruction of the unprovided retail function.
fn native_disable(cleanup_bytes:u8)->Vec<u8>{
    let mut c=vec![0x55,0x89,0xe5,0x8b,0x51,0x04,0xff,0x02,
        0x89,0x4a,0x04,0x8b,0x45,0x08,0x89,0x42,0x08,
        0x85,0xc0,0x75,0x0b];
    write_const(&mut c,12,0);c.extend([0x5d,0xc2,cleanup_bytes,0]);
    c.extend([0x83,0xf8,0xff,0x75,0x0b]);
    write_const(&mut c,12,2);c.extend([0x5d,0xc2,cleanup_bytes,0]);
    write_const(&mut c,12,1);c.extend([0x5d,0xc2,cleanup_bytes,0]);c
}
fn native_cleanup()->Vec<u8>{
    // Keep Cleanup an independently recorded ECX-only / ret 0 method.
    vec![0x55,0x89,0xe5,0x8b,0x51,0x04,0xff,0x42,0x10,
        0x89,0x4a,0x2c,0x5d,0xc3]
}

struct Code{base:usize,bytes:[Vec<u8>;6]}
impl Code{
    unsafe fn new()->Self{
        let bytes=[caller(true),caller(false),dispatcher(),native_disable(4),native_cleanup(),native_disable(0)];
        let base=VirtualAlloc(std::ptr::null_mut(),4096,0x3000,4)as usize;
        assert_ne!(base,0,"CAMERA_ABI_FIXTURE_ALLOCATION_REQUIRED");
        for(i,b)in bytes.iter().enumerate(){assert!(b.len()<0x100);std::ptr::copy_nonoverlapping(b.as_ptr(),(base+i*0x100)as*mut u8,b.len());}
        let mut old=0;assert_ne!(VirtualProtect(base as*mut c_void,4096,0x20,&mut old),0);
        assert_ne!(FlushInstructionCache(GetCurrentProcess(),base as*const c_void,4096),0);
        Self{base,bytes}
    }
    fn at(&self,i:usize)->usize{self.base+i*0x100}
    unsafe fn unchanged(&self,i:usize)->bool{std::slice::from_raw_parts(self.at(i)as*const u8,self.bytes[i].len())==self.bytes[i]}
}
// Keep fixture targets mapped until isolated process exit, as the actual
// StaticDetour retains its trampoline after disable. No target is freed while
// that published trampoline still exists, and no cross-thread hook is run.

#[derive(Default)]
struct Failures{stack_cleanup:u32,argument_forwarding:u32,receiver:u32,nonvolatile:u32,native_count:u32,cleanup:u32,caller_stack:u32,branch:u32,restoration:u32}
impl Failures{fn total(&self)->u32{self.stack_cleanup+self.argument_forwarding+self.receiver+self.nonvolatile+self.native_count+self.cleanup+self.caller_stack+self.branch+self.restoration}}
type Invoke=unsafe extern "C" fn(usize,usize,u32,*mut u32);
fn caller_ok(out:&[u32;10])->bool{out[0]==out[1]&&out[5]==out[6]&&out[7..9]==GUARDS}
fn registers_ok(out:&[u32;10])->bool{out[2..5]==SAVED}

unsafe fn exercise(){
    // Keep every JSON marker at column zero even under libtest --nocapture.
    println!();
    assert!(!READY.load(Ordering::Acquire));
    SetErrorMode(0x0002); // No WER dialog in this child; no process-wide game hook.
    assert!(!AddVectoredExceptionHandler(1,observe_fault).is_null(),"CAMERA_ABI_FAULT_OBSERVER_REQUIRED");
    let code=Code::new();
    let call:Invoke=std::mem::transmute(code.at(0));let clean:Invoke=std::mem::transmute(code.at(1));
    let mut table=vec![0usize;0x4d4/4];table[0x4d0/4]=code.at(3);
    let mut traces=[[0u32;12];2];
    let mut objects=[[table.as_ptr()as usize,0usize];2];
    for i in 0..2{objects[i][1]=traces[i].as_mut_ptr()as usize;}
    // A deliberately wrong ret 0 fixture must be detected at the dispatch
    // boundary while its EBP-based repair keeps our independent caller safe.
    table[0x4d0/4]=code.at(5);let mut negative=[0u32;10];
    call(code.at(2),objects[0].as_mut_ptr()as usize,0x13579bdf,negative.as_mut_ptr());
    assert_eq!(traces[0][6].wrapping_sub(traces[0][5]),u32::MAX-3,"CAMERA_ABI_NEGATIVE_STACK_NOT_DETECTED");
    assert!(caller_ok(&negative)&&registers_ok(&negative),"CAMERA_ABI_NEGATIVE_CALLER_NOT_RESTORED");
    table[0x4d0/4]=code.at(3);traces=[[0;12];2];
    // Type inference deliberately uses each role's REAL production declaration.
    // Baseline is not cast to the repaired ABI and still builds successfully.
    Disable.initialize(std::mem::transmute(code.at(3)),disable).expect("owned production Disable detour");
    Cleanup.initialize(std::mem::transmute(code.at(4)),cleanup).expect("owned production Cleanup detour");
    let mut failures=Failures::default();let mut cases=0u32;
    for stage in 0..4{
        let hooked=stage==1||stage==3;
        if hooked{Disable.enable().unwrap();Cleanup.enable().unwrap();}
        else if stage==2{Disable.disable().unwrap();Cleanup.disable().unwrap();}
        if !hooked&&(!code.unchanged(3)||!code.unchanged(4)){failures.restoration+=1;}
        for repeat in 0..2{for receiver in 0..2{for argument in VALUES{
            let object=objects[receiver].as_mut_ptr()as usize;let before=traces[receiver];
            let mut cleanup_out=[0u32;10];clean(code.at(4),object,0,cleanup_out.as_mut_ptr());
            let cleanup_ok=traces[receiver][4]==before[4]+1&&traces[receiver][11]==object as u32&&caller_ok(&cleanup_out)&&registers_ok(&cleanup_out);
            failures.cleanup+=u32::from(!cleanup_ok);
            CASE_TRACE.store(traces[receiver].as_ptr()as usize,Ordering::SeqCst);
            CASE_OBJECT.store(object,Ordering::SeqCst);CASE_COUNT.store(before[0]as usize,Ordering::SeqCst);
            COMPLETED.store(cases as usize,Ordering::SeqCst);CASE_STAGE.store(stage,Ordering::SeqCst);
            CASE_REPEAT.store(repeat,Ordering::SeqCst);CASE_RECEIVER.store(receiver,Ordering::SeqCst);
            CASE_ARGUMENT.store(argument as usize,Ordering::SeqCst);CLEANUP_CORRECT.store(cleanup_ok,Ordering::SeqCst);
            PRIOR_FAILURES.store(failures.total()as usize,Ordering::SeqCst);CASE_THREAD.store(GetCurrentThreadId()as usize,Ordering::SeqCst);
            let mut out=[0u32;10];ARMED.store(true,Ordering::SeqCst);
            call(code.at(2),object,argument,out.as_mut_ptr());ARMED.store(false,Ordering::SeqCst);
            let row=traces[receiver];let delta=row[6].wrapping_sub(row[5])as i32;
            let forwarded=row[2]==argument;
            failures.stack_cleanup+=u32::from(delta!=0);
            failures.argument_forwarding+=u32::from(!forwarded);
            failures.receiver+=u32::from(row[1]!=object as u32);
            failures.native_count+=u32::from(row[0]!=before[0]+1);
            failures.nonvolatile+=u32::from(row[8]!=SAVED[0]||row[9]!=object as u32||row[10]!=SAVED[2]||!registers_ok(&out));
            failures.caller_stack+=u32::from(!caller_ok(&out));
            // Branch verifies the actual received argument. A wrong argument
            // is separately reported, never concealed as a branch success.
            let branch=if row[2]==0{0}else if row[2]==u32::MAX{2}else{1};
            failures.branch+=u32::from(row[3]!=branch);
            cases+=1;
            println!("CAMERA_ABI_CASE {{\"stage\":{stage},\"hooked\":{hooked},\"repeat\":{repeat},\"receiver\":{receiver},\"argument\":{argument},\"received_argument\":{},\"stack_delta\":{delta},\"branch\":{}}}",row[2],row[3]);
            use std::io::Write;
            std::io::stdout().flush().expect("preserve completed ABI case before next call");
        }}}
    }
    Disable.disable().unwrap();Cleanup.disable().unwrap();
    failures.restoration+=u32::from(!code.unchanged(3)||!code.unchanged(4));
    let f=&failures;
    println!("CAMERA_ABI_RESULT {{\"schema\":1,\"status\":\"{}\",\"cases\":{cases},\"hooked_calls\":32,\"unhooked_calls\":32,\"cleanup_calls\":64,\"negative_controls\":1,\"checks\":{},\"failures\":{{\"stack_cleanup\":{},\"argument_forwarding\":{},\"receiver\":{},\"nonvolatile\":{},\"native_count\":{},\"cleanup\":{},\"caller_stack\":{},\"branch\":{},\"restoration\":{}}},\"game_executed\":false,\"retail_body\":false,\"native_return_type_confirmed\":false}}",if f.total()==0{"passed"}else{"failed"},64*8+3,f.stack_cleanup,f.argument_forwarding,f.receiver,f.nonvolatile,f.native_count,f.cleanup,f.caller_stack,f.branch,f.restoration);
    assert_eq!(f.total(),0,"CAMERA_ABI_CONTRACT_FAILED");
}

#[test]
fn camera_disable_native_stack_contract(){
    if std::env::var_os(CHILD).is_some(){unsafe{exercise();}return;}
    let child=std::process::Command::new(std::env::current_exe().unwrap())
        .args(["--exact",TEST,"--nocapture","--test-threads=1"]).env(CHILD,"1")
        .output().expect("isolated owned ABI process");
    use std::io::Write;
    std::io::stdout().write_all(&child.stdout).unwrap();
    std::io::stderr().write_all(&child.stderr).unwrap();
    println!("CAMERA_ABI_CHILD_EXIT code={}",child.status.code().unwrap_or(-1));
    assert!(child.status.success(),"CAMERA_ABI_CONTRACT_FAILED child_exit={:?}",child.status.code());
}
