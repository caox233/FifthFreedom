//! A non-null texture object can still have no GPU payload. Retail 175D3F0
//! only checks the object; 175E7A9 then binds a null SelfAO SRV. Use the caller's
//! own ready fallback, only for the owned optic. Do not alter shared resources.
pub(super) fn select_texture(
    owned: bool,
    selected: usize,
    fallback: usize,
    read: impl Fn(usize, usize) -> Option<u32>,
) -> usize {
    fn srv(texture: usize, read: &impl Fn(usize, usize) -> Option<u32>) -> Option<u32> {
        if texture < 0x10000 { return None; }
        let rhi = read(texture, 0x2c)? as usize;
        if rhi == 0 { return Some(0); }
        if rhi < 0x10000 { return None; }
        read(rhi, 0x14)
    }
    if owned && selected != fallback && srv(selected, &read) == Some(0)
        && srv(fallback, &read).is_some_and(|p| p >= 0x10000)
    {
        fallback
    } else {
        selected
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn scis_ao_fallback_never_changes_ready_unowned_or_unreadable_resources() {
        let mut map = std::collections::HashMap::from([
            ((0x10000, 0x2c), 0), ((0x20000, 0x2c), 0x30000),
            ((0x30000, 0x14), 0x40000),
        ]);
        let select = |owned, map: &std::collections::HashMap<_,_>| {
            select_texture(owned, 0x10000, 0x20000, |p,o| map.get(&(p,o)).copied())
        };
        assert_eq!(select(true, &map), 0x20000);
        assert_eq!(select(false, &map), 0x10000);
        for (key,value) in [((0x20000,0x2c),0), ((0x30000,0x14),0),
            ((0x10000,0x2c),0x30000), ((0x10000,0x2c),1)] {
            let old=map.insert(key,value).unwrap();
            assert_eq!(select(true,&map),0x10000);
            map.insert(key,old);
        }
        for key in map.keys().copied().collect::<Vec<_>>() {
            let old=map.remove(&key).unwrap();
            assert_eq!(select(true,&map),0x10000);
            map.insert(key,old);
        }
        assert_eq!(select_texture(true,0,0x20000,|p,o|map.get(&(p,o)).copied()),0);
    }
}
