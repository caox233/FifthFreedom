//! Private diagnostic coordinator. Read-only callbacks in existing hooks only.
use super::{Gate, Ticket};
#[path="vss_emf_witness_capture.rs"]
mod capture;
#[path="vss_emf_witness_request.rs"]
mod request;
use std::{cell::RefCell, sync::Mutex};
use capture::Context;
#[link(name="kernel32")]
extern "system" { fn GetTickCount64()->u64; }
fn now()->u64 { unsafe{GetTickCount64()} }
struct Control { gate:Gate, ipc:Option<request::Ipc>, last:Option<request::Request>, owner:Option<Context> }
static CONTROL:Mutex<Control>=Mutex::new(Control{gate:Gate::new(),ipc:None,last:None,owner:None});
#[derive(Clone)]
struct Frame { ticket:Ticket, context:Context, pass:usize, remaining:usize,
    phases:u8, material_count:u8, surfaces:[usize;16], surface_count:usize }
#[derive(Default)]
struct Local { depth:u32, frame:Option<Frame> }
thread_local! { static LOCAL:RefCell<Local>=RefCell::new(Local::default()); }
struct Restore { depth:u32, frame:Option<Frame> }
impl Drop for Restore {fn drop(&mut self){LOCAL.with(|s|{let mut s=s.borrow_mut();s.depth=self.depth;s.frame=self.frame.take();});}}

pub(crate) fn init() {
    let Ok(mut c)=CONTROL.lock() else{return;};
    if c.ipc.is_some(){return;}
    c.gate.stop();c.owner=None;c.last=None;c.ipc=request::Ipc::create();
    tracing::info!("SCBL_VSS_EMF_WITNESS ready={} armed=false manual_only=true hotkeys=false max_ms=5000 max_outer_views=8 max_materials_per_view=16 raw_addresses=false",c.ipc.is_some());
}
pub(crate) fn deinit() {
    if let Ok(mut c)=CONTROL.lock(){c.gate.stop();c.owner=None;c.ipc=None;c.last=None;}
    LOCAL.with(|s|s.borrow_mut().frame=None);
}
fn current(c:&mut Control,t:Ticket)->bool {
    c.ipc.is_some() && c.gate.accepts(now(),t)
}
fn permitted(t:Ticket)->bool {
    CONTROL.try_lock().ok().is_some_and(|mut c|current(&mut c,t))
}
// A final check and bounded metadata emission share the short control lock.
// No native render/game call or target-memory reader runs under this lock.
fn commit(t:Ticket,emit:impl FnOnce(&mut Control))->bool {
    let Ok(mut c)=CONTROL.try_lock() else{return false;};
    if !current(&mut c,t){return false;}
    emit(&mut c);true
}
fn clear_frame(t:Ticket) {
    LOCAL.with(|s|{let mut local=s.borrow_mut();
        if local.frame.as_ref().is_some_and(|f|f.ticket==t){local.frame=None;}
    });
}
fn stop_locked(c:&mut Control,t:Ticket,reason:&'static str) {
    // A delayed old native return must never stop a newly armed run.
    if c.gate.run!=t.run{return;}
    if c.gate.active {tracing::info!("SCBL_VSS_EMF_WITNESS end=true run={} reason={}",t.run,reason);}
    c.gate.stop();c.owner=None;
}
fn stop(t:Ticket,reason:&'static str) {
    if let Ok(mut c)=CONTROL.lock(){stop_locked(&mut c,t,reason);}
    clear_frame(t);
}
fn reader(t:Ticket,limit:usize)->capture::Reader<impl FnMut(usize,usize)->Option<u32>> {
    // Deadline/deinit is checked before EVERY bounded native word read, not only
    // around a queue scan. No target read follows an observed expired ticket.
    capture::Reader::new(move|p,o|{
        if !permitted(t){return None;}
        capture::native_word(p,o)
    },limit)
}
fn reserve()->Option<Ticket> {
    let Ok(mut c)=CONTROL.try_lock() else{return None;};
    let tick=now();
    let request=c.ipc.as_ref()?.take(tick);
    match request {
        Err(e)=>{c.gate.stop();c.owner=None;tracing::info!("SCBL_VSS_EMF_WITNESS request_rejected={:?}",e);return None;},
        Ok(Some(r))=>{
            if c.last!=Some(r) {
                c.last=Some(r);let was=c.gate.active;let run=c.gate.run;
                c.gate.signal_at(tick,r.tick,true);
                if c.gate.run!=run {c.owner=None;tracing::info!("SCBL_VSS_EMF_WITNESS start=true run={} request_age_ms={}",c.gate.run,tick-r.tick);}
                else if was {tracing::info!("SCBL_VSS_EMF_WITNESS request_ignored=active_run run={}",run);}
            }
        },
        Ok(None)=>{},
    }
    let result=c.gate.take(tick);
    if result.is_none(){c.owner=None;}
    result
}
fn bind_owner(t:Ticket,ctx:Context)->bool {
    let Ok(mut c)=CONTROL.try_lock() else{return false;};
    if !current(&mut c,t){return false;}
    if c.owner.is_some_and(|owner|!owner.same_owner(&ctx)) {
        stop_locked(&mut c,t,"owner_changed");return false;
    }
    c.owner=Some(ctx);
    tracing::info!("SCBL_VSS_EMF_WITNESS outer={} run={} owner_token=1 scope_token=1 mode={} view_index={} association=outer_call_not_present_frame",t.view,t.run,ctx.mode(),ctx.view_index());
    true
}
pub(crate) unsafe fn with_view(pass:usize,native:impl FnOnce()) {
    // Nested native views shadow evidence, never reserve another view or borrow
    // an outer token. Restoring an outer context still requires revalidation.
    let mut guard=LOCAL.with(|s|{let mut s=s.borrow_mut();let g=Restore{depth:s.depth,frame:s.frame.take()};s.depth=s.depth.saturating_add(1);g});
    if guard.depth!=0 {native();return;}
    if let Some(ticket)=reserve() {
        let mut reader=reader(ticket,32768);
        let bound=capture::bind(&mut reader,pass,|name|{
            permitted(ticket) && crate::mac11_catalog::vss_name(name) && permitted(ticket)
        });
        match bound {
            Ok(context) if bind_owner(ticket,context)=>{
                LOCAL.with(|s|s.borrow_mut().frame=Some(Frame{ticket,context,pass,remaining:reader.remaining(),phases:0,material_count:0,surfaces:[0;16],surface_count:0}));
            },
            Ok(_)=>stop(ticket,"owner_or_deadline_changed"),
            Err(reason)=>{
                commit(ticket,|c|{
                    tracing::info!("SCBL_VSS_EMF_WITNESS bind_rejected={:?} run={}",reason,ticket.run);
                    stop_locked(c,ticket,"no_matching_scope");
                });
                clear_frame(ticket);
            },
        }
    }
    native();
    let frame=LOCAL.with(|s|s.borrow_mut().frame.take());
    if let Some(f)=frame {
        if let Ok(mut c)=CONTROL.try_lock(){
            if current(&mut c,f.ticket){
                // Consume any pending active-run signal. An invalid request is
                // fail-closed even on the final view, never silently ignored.
                let request=c.ipc.as_ref().map(|ipc|ipc.take(now()));
                match request {
                    Some(Err(e))=>{
                        tracing::info!("SCBL_VSS_EMF_WITNESS request_rejected={:?} run={}",e,f.ticket.run);
                        stop_locked(&mut c,f.ticket,"end_frame_request_invalid");
                    },
                    Some(Ok(Some(r)))=>{c.last=Some(r);},
                    _=>{},
                }
                // IPC reads may cross the deadline: recheck before emitting.
                if current(&mut c,f.ticket){
                    let outcome=if f.context.mode()==0 && f.phases==0 {"normal_no_vision_route"}else{"observed"};
                    tracing::info!("SCBL_VSS_EMF_WITNESS outer_end={} run={} phases={} materials={} reads={} complete={} outcome={}",f.ticket.view,f.ticket.run,f.phases,f.material_count,32768-f.remaining,f.ticket.view==8,outcome);
                    if f.ticket.view==8 {c.gate.stop();c.owner=None;}
                }
            } else {stop_locked(&mut c,f.ticket,"deadline_or_deinit");}
        }
    }
    // Explicit restoration remains in the RAII guard for native unwinding too.
    guard.frame=None;
}
pub(crate) fn classified(pass:usize,phase:u8) {
    if phase>1{return;}
    let candidate=LOCAL.with(|s|s.borrow().frame.clone());
    let Some(mut f)=candidate else{return;};
    if f.pass!=pass || f.phases&(1<<phase)!=0 || !permitted(f.ticket){return;}
    let mut reader=reader(f.ticket,f.remaining);
    let result=capture::queues(&mut reader,&f.context);
    f.remaining=reader.remaining();f.phases|=1<<phase;
    let ticket=f.ticket;
    let committed=commit(ticket,|c|match result {
        Ok(snapshot)=>{
            tracing::info!("SCBL_VSS_EMF_WITNESS queues run={} outer={} phase={} owner_token=1 scope_token=1 snapshot={:?}",f.ticket.run,f.ticket.view,phase,snapshot);
            LOCAL.with(|s|s.borrow_mut().frame=Some(f));
        },
        Err(reason)=>{
            tracing::info!("SCBL_VSS_EMF_WITNESS queues_rejected={:?} run={} outer={}",reason,f.ticket.run,f.ticket.view);
            stop_locked(c,ticket,"queue_context_invalid");clear_frame(ticket);
        },
    });
    if !committed{stop(ticket,"deadline_or_deinit_during_queue");}
}
pub(crate) fn material(root:usize,render:usize,node:usize,surface:usize,selected:usize) {
    let candidate=LOCAL.with(|s|s.borrow().frame.clone());
    let Some(mut f)=candidate else{return;};
    if !f.context.matches_material(root,render,node) || f.material_count>=16 || !permitted(f.ticket){return;}
    let ordinal=if let Some(index)=f.surfaces[..f.surface_count].iter().position(|p|*p==surface){index}
        else {let index=f.surface_count;f.surfaces[index]=surface;f.surface_count+=1;index};
    let mut reader=reader(f.ticket,f.remaining);
    let result=capture::after_material(&mut reader,&f.context,root,render,node,surface,selected);
    f.remaining=reader.remaining();f.material_count+=1;
    let ticket=f.ticket;
    let committed=commit(ticket,|c|match result {
        Ok(snapshot)=>{
            tracing::info!("SCBL_VSS_EMF_WITNESS material run={} outer={} surface_ordinal={} scope_token=1 snapshot={:?}",f.ticket.run,f.ticket.view,ordinal,snapshot);
            LOCAL.with(|s|s.borrow_mut().frame=Some(f));
        },
        Err(reason)=>{
            tracing::info!("SCBL_VSS_EMF_WITNESS material_rejected={:?} run={} outer={}",reason,f.ticket.run,f.ticket.view);
            stop_locked(c,ticket,"material_context_invalid");clear_frame(ticket);
        },
    });
    if !committed{stop(ticket,"deadline_or_deinit_during_material");}
}
