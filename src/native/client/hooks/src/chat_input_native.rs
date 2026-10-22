//! DX11 retail chat and scoped loadout rename adapters. The existing core window owner calls
//! before/after; this module does NOT detour or subclass the window procedure.
use crate::chat_input::{self, Composition, Context};
use crate::input_mouse::memory::{readable, signature, word};
use retour::static_detour;
use std::{cell::Cell, sync::atomic::{AtomicBool, AtomicUsize, Ordering}};
#[cfg(feature="scis-runtime-diagnostics")]
#[path="chat_input_trace.rs"]
mod trace;
#[path="rename_input.rs"]
mod rename_input;

static READY: AtomicBool = AtomicBool::new(false);
static ACTIVE_WINDOW: AtomicUsize = AtomicUsize::new(0);
const CLEANUP_MESSAGE:u32=0x8000+0x3b1;
#[derive(Default, Clone, Copy)]
struct Session { chat: usize, window: usize, context: Option<Context>, input: Composition }
thread_local! { static SESSION: Cell<Session> = Cell::new(Session::default()); }
static_detour! {
    static Open: unsafe extern "thiscall" fn(usize,usize,i32)->u8;
    static Close: unsafe extern "thiscall" fn(usize,u8);
    static Key: unsafe extern "thiscall" fn(usize,usize,usize)->u8;
    static Char: unsafe extern "thiscall" fn(usize,usize,usize)->u8;
    static Popup: unsafe extern "thiscall" fn(usize);
    static PopupDestroy: unsafe extern "thiscall" fn(usize);
    static PopupChar: unsafe extern "thiscall" fn(usize,usize,usize)->u8;
    static PopupBackspace: unsafe extern "thiscall" fn(usize,usize,usize)->u8;
    static PopupAccept: unsafe extern "thiscall" fn(usize,usize,usize)->u8;
    static PopupCancel: unsafe extern "thiscall" fn(usize,usize,usize)->u8;
}
#[link(name="user32")]
extern "system" {
    fn GetFocus()->usize; fn IsWindow(hwnd:usize)->i32;
    fn DefWindowProcA(hwnd:usize,message:u32,wp:usize,lp:isize)->isize;
    fn SendMessageTimeoutW(hwnd:usize,msg:u32,wp:usize,lp:isize,flags:u32,timeout:u32,result:*mut usize)->isize;
}
#[link(name="imm32")]
extern "system" { fn ImmGetCompositionStringW(context:usize,index:u32,out:*mut u8,bytes:u32)->i32; }

#[cfg(feature="scis-runtime-diagnostics")]
unsafe fn trace_state(stage:trace::Stage,chat:usize,units:u32) {
    if !trace::active(){return;}
    // Only bounded scalar lifecycle metadata. Never read/log the FString or
    // the IME composition buffer from this diagnostic function.
    let known=word(chat,0)==Some(chat_input::CHAT_VTABLE);
    let mode=if known{word(chat,0x2c0).unwrap_or(u32::MAX)}else{u32::MAX};
    let widget=if known{word(chat,0x2a4).unwrap_or(0)as usize}else{0};
    let gfx=word(widget,0x1a8).unwrap_or(0)as usize;
    let visible=if readable(gfx+0x40,1){*(gfx as *const u8).add(0x40)as u32}else{u32::MAX};
    let locked=if known&&readable(chat+0x310,1){*(chat as *const u8).add(0x310)as u32}else{u32::MAX};
    let s=SESSION.with(Cell::get);
    #[link(name="imm32")] extern "system" {fn ImmGetContext(w:usize)->usize;fn ImmReleaseContext(w:usize,c:usize)->i32;}
    let associated=if let Some(c)=s.context {let got=ImmGetContext(s.window);if got!=0{ImmReleaseContext(s.window,got);}u32::from(got==c.handle())}else{0};
    trace::record(stage,[known as u32,mode,visible,locked,(s.window!=0)as u32,(s.window!=0&&GetFocus()==s.window)as u32,s.context.is_some()as u32,associated,units]);
}

/// Conservative shortcut suppression while any native chat/rename owner is open.
/// Retain this gate even if IME association fails or the native widget is locked.
pub(crate) fn text_entry_active() -> bool { SESSION.with(|s| s.get().chat != 0) }

unsafe fn eligible(chat: usize)->bool {
    if word(chat,0)==Some(rename_input::VTABLE) {return rename_input::eligible(chat);}
    let test=||->Option<bool>{
        let vt=word(chat,0)?;
        if vt!=chat_input::CHAT_VTABLE {return Some(false);}
        let widget=word(chat,0x2a4)? as usize;
        let gfx=word(widget,0x1a8)? as usize;
        if !readable(gfx+0x40,1)||!readable(chat+0x310,1){return None;}
        Some(chat_input::active(vt,word(chat,0x2c0)?,*(gfx as *const u8).add(0x40)!=0,
            *(chat as *const u8).add(0x310)!=0))
    };
    test()==Some(true)
}
unsafe fn suspend(forget: bool) {
    // Publish the detached state before Win32 calls: IMM can re-enter the window.
    let old=SESSION.with(|s|{let old=s.get();s.set(Session{chat:if forget{0}else{old.chat},
        window:old.window,..Session::default()});old.context});
    if old.is_some(){ACTIVE_WINDOW.store(0,Ordering::Release);}
    if let Some(c)=old {if !c.close(){tracing::warn!("SCBL_CHAT_IME context_cleanup_failed=true");}}
}
unsafe fn resume() {
    let s=SESSION.with(Cell::get);
    if s.context.is_some()||s.window==0||GetFocus()!=s.window||!eligible(s.chat){return;}
    let context=Context::open(s.window);
    SESSION.with(|slot|{let now=slot.get();if now.chat==s.chat&&now.window==s.window{
        slot.set(Session{context,..now});
        if context.is_some(){ACTIVE_WINDOW.store(s.window,Ordering::Release);}
    }else if let Some(c)=context{let _=c.close();}});
    #[cfg(feature="scis-runtime-diagnostics")] trace_state(trace::Stage::Resume,s.chat,0);
}
fn opened(chat:usize,sender:usize,mode:i32)->u8 {unsafe{
    let result=Open.call(chat,sender,mode);
    #[cfg(feature="scis-runtime-diagnostics")] trace_state(trace::Stage::Open,chat,0);
    if READY.load(Ordering::Acquire) && eligible(chat) {
        let previous=SESSION.with(Cell::get);
        if previous.chat!=chat {suspend(true);}
        SESSION.with(|s|s.set(Session{chat,..s.get()}));
        crate::plugin_loader::notify(hooks_plugin_api::EVENT_GAMEPLAY_INPUT_BLOCKED);
        resume();
    }
    result
}}
fn closed(chat:usize,hide:u8){unsafe{
    #[cfg(feature="scis-runtime-diagnostics")] trace_state(trace::Stage::Close,chat,0);
    if SESSION.with(|s|s.get().chat==chat){suspend(true);}
    Close.call(chat,hide);
}}
// The popup class is shared by many dialogs. Adopt only the top descriptor
// whose completion callback is the retail loadout rename consumer.
fn popup_updated(p:usize){unsafe{
    Popup.call(p);
    if !READY.load(Ordering::Acquire){return;}
    if rename_input::eligible(p){
        if SESSION.with(|s|s.get().chat!=p){suspend(true);}
        SESSION.with(|s|s.set(Session{chat:p,..s.get()}));
        crate::plugin_loader::notify(hooks_plugin_api::EVENT_GAMEPLAY_INPUT_BLOCKED);
        resume();
    }else if SESSION.with(|s|s.get().chat==p){suspend(true);}
}}
fn popup_destroyed(p:usize){unsafe{
    if SESSION.with(|s|s.get().chat==p){suspend(true);}
    PopupDestroy.call(p);
}}
fn popup_character(p:usize,sender:usize,event:usize)->u8 {unsafe{
    if owned(p)&&SESSION.with(|s|s.get().input.composing){return 1;}
    PopupChar.call(p,sender,event)
}}
fn popup_backspace(p:usize,sender:usize,event:usize)->u8 {unsafe{
    if owned(p){
        if SESSION.with(|s|s.get().input.composing){return 1;}
        let query:unsafe extern "C" fn()->u32=std::mem::transmute(0x1a47bd0usize);
        if query()==8&&matches!(word(event,8),Some(2|5))&&rename_input::backspace(p){return 1;}
    }
    PopupBackspace.call(p,sender,event)
}}
fn popup_accept(p:usize,sender:usize,event:usize)->u8 {unsafe{
    if owned(p)&&SESSION.with(|s|s.get().input.blocks_native(13)){return 1;}
    PopupAccept.call(p,sender,event)
}}
fn popup_cancel(p:usize,sender:usize,event:usize)->u8 {unsafe{
    if owned(p)&&SESSION.with(|s|s.get().input.blocks_native(27)){return 1;}
    PopupCancel.call(p,sender,event)
}}
unsafe fn owned(chat:usize)->bool {
    READY.load(Ordering::Acquire)&&SESSION.with(|s|{let s=s.get();s.chat==chat&&s.context.is_some()&&GetFocus()==s.window})
        && eligible(chat)
}
fn key(chat:usize,sender:usize,event:usize)->u8 {unsafe{
    if owned(chat) {
        // Native 1A47BD0 obtains the current key from the input manager. Its
        // comparison to ESI cannot change the returned value; it has no args.
        let query:unsafe extern "C" fn()->u32=std::mem::transmute(0x1a47bd0usize);
        let code=query() as usize;
        if SESSION.with(|s|s.get().input.blocks_native(code)){return 1;}
        // Native Backspace removes one UTF-16 unit, not one Unicode scalar.
        if code==8 && matches!(word(event,8),Some(2|5)) {
            if let Some(mut text)=current_text(chat){
                if text.len()>=2 && (0xdc00..=0xdfff).contains(text.last().unwrap())
                    && (0xd800..=0xdbff).contains(&text[text.len()-2]) {
                    text.truncate(text.len()-2);text.push(0);
                    let set:unsafe extern "thiscall" fn(usize,*const u16)->usize=std::mem::transmute(0x4637a0usize);
                    set(chat+0x314,text.as_ptr());refresh(chat);return 1;
                }
            }
        }
    }
    Key.call(chat,sender,event)
}}
fn character(chat:usize,sender:usize,input:usize)->u8 {unsafe{
    if owned(chat)&&SESSION.with(|s|s.get().input.composing){return 1;}
    Char.call(chat,sender,input)
}}
unsafe fn current_text(chat:usize)->Option<Vec<u16>> {
    let count=word(chat,0x318)? as usize;
    if count==0{return Some(Vec::new());}
    if count>145{return None;}
    let p=word(chat,0x314)? as usize;
    if !readable(p,count*2){return None;}
    let text=std::slice::from_raw_parts(p as *const u16,count);
    (text[count-1]==0).then(||text[..count-1].to_vec())
}
unsafe fn refresh(chat:usize){
    let refresh:unsafe extern "thiscall" fn(usize)=std::mem::transmute(0x1a88010usize);refresh(chat);
}
unsafe fn commit(chat:usize,text:&[u16])->bool {
    if !owned(chat){return false;}
    if word(chat,0)==Some(rename_input::VTABLE){return rename_input::commit(chat,text);}
    let Some(current)=current_text(chat)else{return false;};
    let Some(limit)=word(chat,0x2ec).filter(|n|(1..=144).contains(n))else{return false;};
    // The constructor already subtracts player name and channel prefix from
    // its 78-unit budget. Preserve that native bound including final NUL.
    let room=(limit as usize).saturating_sub(current.len()+1);
    let mut accepted=chat_input::commit(text,room);
    if accepted.is_empty(){return false;}
    accepted.push(0);
    let append:unsafe extern "thiscall" fn(usize,*const u16)->usize=std::mem::transmute(0x463f80usize);
    append(chat+0x314,accepted.as_ptr());refresh(chat);
    #[cfg(feature="scis-runtime-diagnostics")] trace_state(trace::Stage::Commit,chat,(accepted.len()-1)as u32);
    true
}

pub(crate) unsafe fn before(view:usize,message:u32,wp:usize,lp:isize)->Option<isize>{
    // Teardown may be requested from a non-window thread. This private message
    // only releases our context, and stays serviceable until that release runs.
    if message==CLEANUP_MESSAGE && wp==before as usize && lp==0
        && SESSION.with(|s|s.get().context.is_some()) {
        suspend(true);return Some(1);
    }
    if !READY.load(Ordering::Acquire){return None;}
    #[cfg(feature="scis-runtime-diagnostics")] trace::window(message,wp,lp);
    // No native memory queries or Win32 calls for WM_INPUT/WM_MOUSEMOVE. High
    // polling-rate mouse reports must not pay for a chat feature when playing.
    if !matches!(message,2|7|8|0x1c|0x82|0x100|0x101|0x104|0x105|0x102|0x106|0x109|0x10d|0x10e|0x10f|0x201|0x286){return None;}
    if word(view,0)!=Some(0x2c4ea4c){return None;}
    let hwnd=word(view,0x82c).and_then(|p|word(p as usize,4)).unwrap_or(0)as usize;
    if hwnd==0||IsWindow(hwnd)==0{return None;}
    let old=SESSION.with(Cell::get);
    if old.window!=0&&old.window!=hwnd{return None;} // ignore offscreen viewports
    SESSION.with(|s|s.set(Session{window:hwnd,..s.get()}));
    if matches!(message,2|0x82){suspend(true);SESSION.with(|s|s.set(Session::default()));return None;}
    if message==8||(message==0x1c&&wp==0){suspend(false);return None;}
    if message==7 {suspend(false);return None;} // retail SETFOCUS detaches HIMC
    // Open can precede discovery of the game HWND. Once this existing window
    // owner supplies it, attach the pending scoped context. Mouse messages never
    // reach here, and focus/blur teardown still precedes this retry.
    resume();
    let s=SESSION.with(Cell::get);
    #[cfg(feature="scis-runtime-diagnostics")]
    match message {
        0x10d=>trace_state(trace::Stage::ImeStart,s.chat,0),
        0x10e=>trace_state(trace::Stage::ImeEnd,s.chat,0),
        0x10f if lp as usize&0x800!=0=>trace_state(trace::Stage::ImeResult,s.chat,0),
        _=>{},
    }
    if s.context.is_some()&&!eligible(s.chat){suspend(true);return None;}
    if !owned(s.chat){return None;}
    match message {
        0x100|0x101|0x104|0x105 if wp==0xe5=>{
            // VK_PROCESSKEY belongs to the IME, not to the game's key enum.
            // Retail Window forwards it unchanged as 0xE5 / MouseY; InputKey
            // then sets the held-axis state and dispatches its binding even
            // before WM_IME_STARTCOMPOSITION. Keep the Windows default path,
            // but never feed this transport key into the native game parser.
            // Ordinary keys, native mouse-axis events and unowned windows are
            // unchanged. Do not substitute the underlying composing letter.
            Some(DefWindowProcA(hwnd,message,wp,lp))
        }
        0x201 if !s.input.composing&&word(s.chat,0)==Some(rename_input::VTABLE)=>{
            // A fresh click on this game window is an independent confirmation,
            // not the deferred Enter/Escape that selected an IME candidate.
            // Candidate-window clicks are not routed through this window owner.
            SESSION.with(|x|{let mut s=x.get();s.input.key_down(13,false);s.input.key_down(27,false);x.set(s);});None
        }
        0x10d=>{SESSION.with(|x|{let mut s=x.get();s.input.start();x.set(s);});None}
        0x10e=>{SESSION.with(|x|{let mut s=x.get();s.input.end();x.set(s);});None}
        0x10f if lp as usize&0x800!=0=>{
            let himc=s.context?.handle();
            let bytes=ImmGetCompositionStringW(himc,0x800,std::ptr::null_mut(),0);
            // Consume this result message exactly once. Never pass its result
            // to DefWindowProcA to create a second, ANSI-truncated WM_CHAR.
            if bytes>0 && bytes<=512 && bytes%2==0 {
                let mut text=vec![0u16;bytes as usize/2];
                if ImmGetCompositionStringW(himc,0x800,text.as_mut_ptr()as _,bytes as u32)==bytes{
                    let _=commit(s.chat,&text);
                }
            }
            SESSION.with(|x|{let mut s=x.get();s.input.result();x.set(s);});Some(0)
        }
        0x286=>Some(0), // WM_IME_CHAR fallback must not duplicate wide commit
        0x100|0x104=>{
            let consume=SESSION.with(|x|{let mut s=x.get();let v=s.input.key_down(wp,lp as usize&(1<<30)!=0);x.set(s);v});
            // Do not swallow the IME's keydown: it drives preedit/candidate UI.
            // The native chat-key callback is gated separately.
            let _=consume;None
        }
        0x102|0x106 if s.input.composing=>Some(0),
        0x109=>{
            if wp==0xffff{return Some(1);}
            if let Some(ch)=char::from_u32(wp as u32){let mut b=[0u16;2];let _=commit(s.chat,ch.encode_utf16(&mut b));}
            Some(0)
        }
        _=>None,
    }
}
pub(crate) unsafe fn after(view:usize,message:u32){
    if !READY.load(Ordering::Acquire)||message!=7{return;}
    let hwnd=word(view,0x82c).and_then(|p|word(p as usize,4)).unwrap_or(0)as usize;
    if SESSION.with(|s|s.get().window==hwnd){resume();}
}

pub(crate) unsafe fn init(){
    if READY.load(Ordering::Acquire){return;}
    if !SIGNATURES.iter().all(|&(p,n,h)|signature(p,n,h)){
        tracing::warn!("SCBL_CHAT_IME enabled=false reason=native-signature");return;
    }
    let ok=Open.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize,usize,i32)->u8>(0x1a879f0),opened).is_ok()
        &&Close.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize,u8)>(0x1a59820),closed).is_ok()
        &&Key.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize,usize,usize)->u8>(0x1a88610),key).is_ok()
        &&Char.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize,usize,usize)->u8>(0x1a887c0),character).is_ok()
        &&Popup.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize)>(0x19b1130),popup_updated).is_ok()
        &&PopupDestroy.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize)>(0x1992f90),popup_destroyed).is_ok()
        &&PopupChar.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize,usize,usize)->u8>(0x1945bf0),popup_character).is_ok()
        &&PopupBackspace.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize,usize,usize)->u8>(0x1945fb0),popup_backspace).is_ok()
        &&PopupAccept.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize,usize,usize)->u8>(0x19c5550),popup_accept).is_ok()
        &&PopupCancel.initialize(std::mem::transmute::<usize,unsafe extern "thiscall" fn(usize,usize,usize)->u8>(0x19c5720),popup_cancel).is_ok();
    if !ok||Close.enable().is_err()||Key.enable().is_err()||Char.enable().is_err()||Open.enable().is_err()
        ||PopupDestroy.enable().is_err()||PopupChar.enable().is_err()||PopupBackspace.enable().is_err()
        ||PopupAccept.enable().is_err()||PopupCancel.enable().is_err()||Popup.enable().is_err(){
        let _=Popup.disable();let _=PopupCancel.disable();let _=PopupAccept.disable();
        let _=PopupBackspace.disable();let _=PopupChar.disable();let _=PopupDestroy.disable();
        let _=Open.disable();let _=Char.disable();let _=Key.disable();let _=Close.disable();
        tracing::warn!("SCBL_CHAT_IME enabled=false reason=hooks");return;
    }
    READY.store(true,Ordering::Release);
    tracing::info!("SCBL_CHAT_IME enabled=true scope=native-chat-and-loadout-rename commit=UTF16 global_keyboard=false");
}
pub(crate) unsafe fn shutdown()->bool {
    READY.store(false,Ordering::Release);
    if SESSION.with(|s|s.get().context.is_some()){suspend(true);}
    let hwnd=ACTIVE_WINDOW.load(Ordering::Acquire);
    if hwnd!=0 {
        let mut result=0;
        let _=SendMessageTimeoutW(hwnd,CLEANUP_MESSAGE,before as usize,0,3,500,&mut result);
    }
    // On timeout the resident core leaves its otherwise-pass-through window
    // callback installed, so a delayed cleanup cannot target unloaded code.
    ACTIVE_WINDOW.load(Ordering::Acquire)==0
}
// Filled from the pinned retail executable, not an address-only assumption.
const SIGNATURES:&[(usize,usize,&str)]=&[
    (0x19b1130,2702,"0e3f9f61287cf6339256e70ba81c2ae95168a4337deae8487c4076cc9cffdbde"),
    (0x1945bf0,959,"acdbfacf50652e2d8397c9e925957bfacf17ae19d2b102bf6a296ed1fe316850"),
    (0x1945fb0,227,"a8e76762723acb9b2d475ad50794fcb841a13b4bbcdb860781b3d1318444e6fa"),
    (0x19c5550,455,"d4f10565d6d9a42cadfb55f5e2b93dae8dda9a437287427a9d1d31da043a73dc"),
    (0x19c5720,341,"c5c16e975d5bfaf1533c155b713f98969619de9aa68255f9b8c7de8f819a4cc2"),
    // SceneMessageBox lifetime and constructor identity, not SceneLoading.
    (0x1992f90,808,"2e66ca56439ced3ef0b843698b8da29abb8e0bbd98a60a2276aa08dd33dabb8f"),
    (0x19d1aee,6,"2d52a818e7a7c9231d28b26ef8d95644ce9866a16ba702f9f7c9e53b89e33b17"),
    (0x2c30444,372,"50613f9e0cf9cd3a50cc8ddfdc31f853dab8a7ecd4c5973b4cac88bda8b402a1"),
    (0x18bf320,60,"5125847850556767bc858280c4541dbf892a28ad36c6864e5fc29be2ed1495de"),
    (0x18f2e70,79,"74e8f5482ab2def821275d9464afcb8e1fd41b8ce404d16f12424cfc323e6161"),
    (0x1a879f0,1552,"8eddf5839f2ec331a113987b30a307148f78f2a032214a8b78f1f6d49eabda65"),
    (0x1a59820,286,"aed6cf49cc0020f54c7618527a4cd950f2beba806c3a5ed5b3cd34ff72f29c6c"),
    (0x1a88610,421,"a6f3d8deffcbe3e58efe882a468886003b4bda9060a4dcaa24fd465bacd5ced7"),
    (0x1a887c0,263,"f5399f34814eb1c75123103011a8204ed16f08cee884995c0901c75fc2f703da"),
    (0x463f80,212,"c2f2a085297680e72477579e602eb0f1c389c4141737ec08b6cc7c4c7db259cf"),
    (0x4637a0,32,"d067ab544aaf84699c999268a9c2f6ab97e92920c21e635f545828d1812aac75"),
    (0x1a88010,1536,"fa687073ae60108a941812ea43a53de03363749df9568bcb5c2c49a81915c57d"),
    (0x1a47bd0,28,"773d7e64f838fa0ca59e02977c373d22dcaaed6c207a798edb398afa0ebf3bd6"),
    (0x1839ca0,20,"3defa9099584e64adb626a2b7311519d90df547893720b0ad5a3313ca092322d"),
];

#[cfg(test)] mod tests {
    use super::*;
    // These two real hidden-window tests exercise the shared READY/active
    // window owner. Keep their lifecycle isolated under parallel cargo tests.
    static WINDOW_TEST_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
    #[test] fn every_native_chat_channel_resumes_pending_ime_and_restores_on_close(){unsafe{
        let _guard=WINDOW_TEST_LOCK.lock().unwrap();
        #[link(name="user32")] extern "system" {
            fn CreateWindowExW(ex:u32,class:*const u16,name:*const u16,style:u32,x:i32,y:i32,w:i32,h:i32,parent:usize,menu:usize,instance:usize,param:usize)->usize;
            fn DestroyWindow(window:usize)->i32;fn SetFocus(window:usize)->usize;
        }
        #[link(name="imm32")] extern "system" {
            fn ImmGetContext(w:usize)->usize;fn ImmReleaseContext(w:usize,c:usize)->i32;
        }
        unsafe fn associated(w:usize)->usize{let c=ImmGetContext(w);if c!=0{ImmReleaseContext(w,c);}c}
        let class:Vec<u16>="STATIC\0".encode_utf16().collect();
        let hwnd=CreateWindowExW(0,class.as_ptr(),std::ptr::null(),0x80000000,0,0,1,1,0,0,0,0);
        assert_ne!(hwnd,0);let old_focus=SetFocus(hwnd);assert_eq!(GetFocus(),hwnd);
        let previous=associated(hwnd);
        let mut chat=vec![0u32;0x350/4];let mut widget=vec![0u32;0x1b0/4];let mut gfx=vec![0u32;0x50/4];
        let mut view=vec![0u32;0x850/4];let window=[0u32,hwnd as u32];
        chat[0]=chat_input::CHAT_VTABLE;chat[0x2a4/4]=widget.as_ptr()as u32;
        widget[0x1a8/4]=gfx.as_ptr()as u32;view[0]=0x2c4ea4c;view[0x82c/4]=window.as_ptr()as u32;
        let p=chat.as_ptr()as usize;let v=view.as_ptr()as usize;let mut rows=Vec::new();
        READY.store(true,Ordering::Release);
        for mode in 0..=2{
            chat[0x2c0/4]=mode;gfx[0x40/4]=1;
            SESSION.with(|s|s.set(Session{chat:p,..Session::default()}));
            // A mouse report must not trigger any IME work, even when pending.
            before(v,0xff,0,0);
            let mouse_pass=SESSION.with(|s|s.get().window==0&&s.get().context.is_none());
            before(v,0x100,65,0);
            let attached=owned(p);
            if attached{
                // IME transport keys can precede STARTCOMPOSITION. They must
                // never enter the retail MouseY key slot, including releases.
                for message in [0x100,0x101,0x104,0x105] {
                    assert!(before(v,message,0xe5,0).is_some());
                }
                before(v,0x10d,0,0);before(v,0x100,13,0);
                assert_eq!(character(p,0,0),1);
                before(v,0x10e,0,0);
            }
            before(v,8,0,0);
            let restored=associated(hwnd)==previous&&SESSION.with(|s|s.get().context.is_none()&&s.get().chat==p);
            before(v,7,0,0);after(v,7);
            let resumed=owned(p);
            gfx[0x40/4]=0;before(v,0x100,65,0);
            // In a baseline failing channel no ownership was acquired; still
            // release/reset fixture state before reporting any failed assertion.
            suspend(true);
            let hidden=associated(hwnd)==previous&&ACTIVE_WINDOW.load(Ordering::Acquire)==0;
            rows.push((mode,mouse_pass,attached,restored,resumed,hidden));
        }
        before(v,0x82,0,0);READY.store(false,Ordering::Release);
        SetFocus(old_focus);assert_ne!(DestroyWindow(hwnd),0);
        assert!(rows.iter().all(|r|r.1&&r.2&&r.3&&r.4&&r.5),"native channel IME lifecycle: {rows:?}");
    }}
    #[test] fn rename_context_routes_composition_and_cleans_up_on_hidden_fixture_window(){unsafe{
        let _guard=WINDOW_TEST_LOCK.lock().unwrap();
        // A private invisible STATIC window, not the game window. Exercise the
        // real IMM owner and callback gates without calling native game code.
        #[link(name="user32")] extern "system" {
            fn CreateWindowExW(ex:u32,class:*const u16,name:*const u16,style:u32,x:i32,y:i32,w:i32,h:i32,parent:usize,menu:usize,instance:usize,param:usize)->usize;
            fn DestroyWindow(window:usize)->i32;
            fn SetFocus(window:usize)->usize;
        }
        let class:Vec<u16>="STATIC\0".encode_utf16().collect();
        let hwnd=CreateWindowExW(0,class.as_ptr(),std::ptr::null(),0x80000000,0,0,1,1,0,0,0,0);
        assert_ne!(hwnd,0);let old_focus=SetFocus(hwnd);assert_eq!(GetFocus(),hwnd);
        let mut popup=vec![0u32;0x400/4];let mut desc=vec![0u32;0x84/4];
        let mut widget=vec![0u32;0x1b0/4];let mut gfx=vec![0u32;0x50/4];
        let mut delegate=vec![0u32;5];let mut view=vec![0u32;0x850/4];
        let window=[0u32,hwnd as u32];
        // Literal pinned constructor identity prevents a self-confirming guard fixture.
        popup[0]=0x02c30444;popup[0x350/4]=desc.as_ptr()as u32;popup[0x354/4]=1;
        popup[0x2ac/4]=widget.as_ptr()as u32;popup[0x2c0/4]=14;popup[0x304/4]=1;
        desc[0x18/4]=5;desc[0x1c/4]=3;desc[0x70/4]=delegate.as_ptr()as u32;desc[0x78/4]=14;
        delegate[4]=0x1933cd0;widget[0x1a8/4]=gfx.as_ptr()as u32;gfx[0x40/4]=1;
        view[0]=0x2c4ea4c;view[0x82c/4]=window.as_ptr()as u32;
        let p=popup.as_ptr()as usize;let v=view.as_ptr()as usize;
        assert_eq!(rename_input::VTABLE,0x02c30444);
        assert!(rename_input::eligible(p));
        popup[0]=0x02c32eb4;assert!(!rename_input::eligible(p));
        popup[0]=0x02c30444;
        SESSION.with(|s|s.set(Session{chat:p,..Session::default()}));READY.store(true,Ordering::Release);
        // Open-before-HWND discovery used to leave an eligible owner detached.
        assert_eq!(before(v,0x100,65,0),None);assert!(owned(p));
        assert_eq!(before(v,0x10d,0,0),None);assert_eq!(before(v,0x100,13,0),None);
        assert_eq!(popup_accept(p,0,0),1);assert_eq!(popup_character(p,0,0),1);
        assert_eq!(popup_backspace(p,0,0),1);
        before(v,0x10e,0,0);assert_eq!(popup_accept(p,0,0),1);
        before(v,0x100,13,1<<30);assert!(SESSION.with(|s|s.get().input.blocks_native(13)));
        before(v,0x100,13,0);assert!(!SESSION.with(|s|s.get().input.blocks_native(13)));
        before(v,0x10d,0,0);before(v,0x100,27,0);before(v,0x10e,0,0);
        assert_eq!(popup_cancel(p,0,0),1);
        before(v,0x201,0,0);
        assert!(!SESSION.with(|s|s.get().input.blocks_native(27)),"new mouse click must release a completed IME selection gate");
        before(v,8,0,0);assert!(SESSION.with(|s|s.get().context.is_none()));
        assert_eq!(SESSION.with(|s|s.get().chat),p);assert_eq!(ACTIVE_WINDOW.load(Ordering::Acquire),0);
        before(v,7,0,0);after(v,7);assert!(owned(p));
        before(v,0x82,0,0);assert_eq!(SESSION.with(|s|s.get().chat),0);
        assert!(SESSION.with(|s|s.get().context.is_none()));assert_eq!(ACTIVE_WINDOW.load(Ordering::Acquire),0);
        READY.store(false,Ordering::Release);SetFocus(old_focus);assert_ne!(DestroyWindow(hwnd),0);
    }}
    #[test] fn active_requires_native_chat_visible_valid_channel_and_no_rate_lock(){unsafe{
        let mut chat=vec![0u32;0x350/4];let mut widget=vec![0u32;0x1b0/4];let mut gfx=vec![0u32;0x50/4];
        chat[0]=chat_input::CHAT_VTABLE;chat[0x2a4/4]=widget.as_ptr()as u32;
        widget[0x1a8/4]=gfx.as_ptr()as u32;gfx[0x40/4]=1;
        let p=chat.as_ptr()as usize;
        for m in [0,1,2]{chat[0x2c0/4]=m;assert!(eligible(p));}
        for m in [3,u32::MAX]{chat[0x2c0/4]=m;assert!(!eligible(p));}
        chat[0x2c0/4]=1;gfx[0x40/4]=0;assert!(!eligible(p));gfx[0x40/4]=1;
        chat[0x310/4]=1;assert!(!eligible(p));chat[0x310/4]=0;
        chat[0]=0;assert!(!eligible(p));assert!(!eligible(0));
    }}
    #[test] fn bounded_native_text_requires_wide_terminator(){unsafe{
        let mut chat=vec![0u32;0x350/4];let text=[0x4e2du16,0x6587,0];
        chat[0x314/4]=text.as_ptr()as u32;chat[0x318/4]=3;
        let p=chat.as_ptr()as usize;assert_eq!(current_text(p),Some(vec![0x4e2d,0x6587]));
        chat[0x318/4]=2;assert_eq!(current_text(p),None);
        chat[0x318/4]=146;assert_eq!(current_text(p),None);
        chat[0x318/4]=0;assert_eq!(current_text(p),Some(vec![]));
    }}
}
