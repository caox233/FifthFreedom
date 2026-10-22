//! Publish all cooperating callsites before enabling their callbacks.
//! Retain code allocations for process lifetime, even after an install failure.
#[derive(Clone,Debug)]
pub(super) struct Site { pub address:usize, pub original:Vec<u8>, pub replacement:Vec<u8> }
pub(super) fn call(site:usize,target:usize)->Vec<u8> {
    let mut b=vec![0xe8];b.extend((target as u32).wrapping_sub(site as u32+5).to_le_bytes());b
}
pub(super) fn jump(site:usize,target:usize,len:usize)->Vec<u8> {
    let mut b=vec![0x90;len];if len<5{return b;}
    b[0]=0xe9;b[1..5].copy_from_slice(&(target as u32).wrapping_sub(site as u32+5).to_le_bytes());b
}
pub(super) fn valid(sites:&[Site])->bool {
    if sites.is_empty(){return false;}
    for (i,s) in sites.iter().enumerate() {
        if s.address<0x10000 || s.original.len()<5 || s.original.len()!=s.replacement.len()
            || s.original==s.replacement {return false;}
        let Some(end)=s.address.checked_add(s.original.len())else{return false;};
        if sites[..i].iter().any(|p|p.address<end && p.address.saturating_add(p.original.len())>s.address){return false;}
    }
    true
}
/// write must compare exact expected bytes before writing. Do not overwrite a
/// third-party owner on rollback. A failed write may already have written bytes;
/// include that slot in the attempted reverse restoration.
pub(super) fn publish(sites:&[Site],mut write:impl FnMut(usize,&[u8],&[u8])->bool)->bool {
    if !valid(sites){return false;}
    for (i,s) in sites.iter().enumerate(){
        if !write(s.address,&s.original,&s.replacement){
            for s in sites[..=i].iter().rev(){let _=write(s.address,&s.replacement,&s.original);}
            return false;
        }
    }
    true
}
pub(super) fn restore(sites:&[Site],mut write:impl FnMut(usize,&[u8],&[u8])->bool)->Vec<Site>{
    let mut pending=Vec::new();
    for s in sites.iter().rev(){if !write(s.address,&s.replacement,&s.original){pending.push(s.clone());}}
    pending.reverse();pending
}
#[cfg(test)] mod tests {
    use super::*;
    #[test]fn partial_failures_restore_without_stealing_changed_bytes(){
        let sites:Vec<_>=(0..4).map(|i|Site{address:0x10000+i*8,original:vec![i as u8;5],replacement:vec![i as u8+10;5]}).collect();
        for fail in 0..=4 {for failure_after_write in [false,true]{
            let mut mem:Vec<_>=sites.iter().map(|s|s.original.clone()).collect();let mut calls=0;
            let ok=publish(&sites,|a,b,n|{let i=(a-0x10000)/8;if mem[i]!=b{return false;}
                let failing=calls==fail;calls+=1;if !failing||failure_after_write{mem[i]=n.to_vec();}!failing});
            assert_eq!(ok,fail==4);
            assert_eq!(mem,sites.iter().map(|s|if ok{s.replacement.clone()}else{s.original.clone()}).collect::<Vec<_>>());
        }}
        let mut mem:Vec<_>=sites.iter().map(|s|s.replacement.clone()).collect();mem[2]=vec![99;5];
        restore(&sites,|a,b,n|{let i=(a-0x10000)/8;if mem[i]!=b{return false;}mem[i]=n.to_vec();true});
        assert_eq!(mem[2],vec![99;5]);assert_eq!(mem[0],sites[0].original);
        let mut overlap=sites.clone();overlap[1].address=overlap[0].address+4;assert!(!valid(&overlap));
        assert!(!valid(&[]));
    }
}
