//! Read-only layout and call-preservation seams for the manual observer.
//! These tests validate observation, not animation evaluation or visual repair.
pub(super) fn around<T, S>(before: impl FnOnce()->Option<S>, native: impl FnOnce()->T,
    after: impl FnOnce(S)) -> T {
    let saved=before();let result=native();if let Some(saved)=saved{after(saved);}result
}
pub(super) fn phase(anim:usize,offset:usize,read:impl Fn(usize,usize)->Option<u32>)->Option<u32>{
    let data=read(anim,offset)? as usize;let count=read(anim,offset+4)?;
    let capacity=read(anim,offset+8)?;
    if data<0x10000 || count>8 || count>capacity || capacity>64{return None;}
    for i in 0..count as usize {
        // Native AF32F0: index byte 99, type byte 5, value at +4, stride 24.
        if read(data,i*24)?&0xffff==0x0563{return read(data,i*24+4);}
    }None
}
pub(super) fn component(hands:usize,read:impl Fn(usize,usize)->Option<u32>)->Option<usize>{
    // Exact 685490, without invoking getters or evaluating the animation graph.
    let primary=read(hands,0x1f0)? as usize;
    let instance=if primary!=0 {read(primary,0xc4)? as usize}
        else {let fallback=read(hands,0x1f8)? as usize;if fallback==0{return None;}read(fallback,0x64)? as usize};
    (instance>=0x10000).then(||instance.checked_add(0x460)).flatten()
}
#[cfg(test)]mod tests{
    use super::*;use std::{cell::RefCell,collections::BTreeMap};
    #[test]fn native_exactly_once_and_exact_return_on_accept_or_reject(){
        for enabled in [false,true]{for result in [0u8,1,255]{
            let events=RefCell::new(Vec::new());
            let got=around(||{events.borrow_mut().push(0);enabled.then_some(23)},
                ||{events.borrow_mut().push(1);result},|s|{assert_eq!(s,23);events.borrow_mut().push(2);});
            assert_eq!(got,result);assert_eq!(*events.borrow(),if enabled{vec![0,1,2]}else{vec![0,1]});
        }}
    }
    #[test]fn read_only_model_chain_matches_both_native_branches(){
        let mut m=BTreeMap::from([((0x10000,0x1f0),0x20000),((0x20000,0xc4),0x30000)]);
        assert_eq!(component(0x10000,|p,o|m.get(&(p,o)).copied()),Some(0x30460));
        m.insert((0x10000,0x1f0),0);assert_eq!(component(0x10000,|p,o|m.get(&(p,o)).copied()),None);
        m.insert((0x10000,0x1f8),0x40000);m.insert((0x40000,0x64),0x50000);
        assert_eq!(component(0x10000,|p,o|m.get(&(p,o)).copied()),Some(0x50460));
        m.insert((0x10000,0x1f0),0x20000);m.insert((0x20000,0xc4),0);
        assert_eq!(component(0x10000,|p,o|m.get(&(p,o)).copied()),None); // no invented fallback
    }
    #[test]fn bounded_typed_phase_and_missing_is_not_zero(){
        let mut m=BTreeMap::from([((0x10000,0x40),0x20000),((0x10000,0x44),1),((0x10000,0x48),8),((0x20000,0),0x563),((0x20000,4),2)]);
        assert_eq!(phase(0x10000,0x40,|p,o|m.get(&(p,o)).copied()),Some(2));
        m.insert((0x20000,0),0x463);assert_eq!(phase(0x10000,0x40,|p,o|m.get(&(p,o)).copied()),None);
        m.insert((0x20000,0),0x563);m.insert((0x10000,0x44),9);
        assert_eq!(phase(0x10000,0x40,|p,o|m.get(&(p,o)).copied()),None);
        m.insert((0x10000,0x44),1);m.insert((0x10000,0x48),0);
        assert_eq!(phase(0x10000,0x40,|p,o|m.get(&(p,o)).copied()),None);
    }
}
