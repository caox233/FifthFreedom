use std::{ffi::c_void, path::Path};
#[link(name="kernel32")]
extern "system" {
    fn AddVectoredExceptionHandler(first:u32,handler:unsafe extern "system" fn(*mut c_void)->i32)->*mut c_void;
    fn RaiseException(code:u32,flags:u32,count:u32,args:*const usize);
    fn SetErrorMode(mode:u32)->u32;
}
unsafe extern "system" fn handled(_: *mut c_void)->i32 { -1 }
fn main() {
    let args:Vec<_>=std::env::args().collect();
    let path=Path::new(&args[1]);std::fs::create_dir_all(path).unwrap();
    unsafe { SetErrorMode(0x8003); }
    #[cfg(observer)] assert!(exception_record::init(path));
    if args[2]=="handled" {
        unsafe { AddVectoredExceptionHandler(0,handled); }
        for _ in 0..12 { unsafe { RaiseException(0xc0000005,0,0,std::ptr::null()); } }
        println!("HANDLED_CONTINUED count=12");
        #[cfg(observer)] unsafe { exception_record::deinit(); }
    } else {
        unsafe { std::ptr::write_volatile(1usize as *mut u8,42); }
    }
}
