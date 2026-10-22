//! Scoped import context, used only by the exact SC-IS preload callbacks.
//! A lease restores the prior native context even if the importer unwinds.
pub(super) trait Context {
    fn enter_campaign(&mut self) -> bool;
    fn restore(&mut self);
}

pub(super) fn import<C: Context, T>(
    context: &mut C,
    enabled: bool,
    original: impl FnOnce(&mut C) -> T,
) -> T {
    if !enabled || !context.enter_campaign() {
        return original(context);
    }
    struct Lease<'a, C: Context>(&'a mut C);
    impl<C: Context> Drop for Lease<'_, C> {
        fn drop(&mut self) { self.0.restore(); }
    }
    let lease = Lease(context);
    original(lease.0)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[derive(Default)]
    struct Bank { current: u8, available: bool, events: Vec<&'static str> }
    impl Context for Bank {
        fn enter_campaign(&mut self) -> bool {
            if self.current != 1 { return false; }
            self.events.push("mount-campaign");
            if !self.available { self.events.push("restore-after-load-failure");return false; }
            self.current=2;true
        }
        fn restore(&mut self) { self.current=1;self.events.push("restore-multiplayer"); }
    }
    #[test]
    fn scis_render_context_routes_import_not_render_and_restores() {
        let mut b=Bank{current:1,available:true,..Default::default()};
        let bank=import(&mut b,true,|b|{b.events.push("native-import");b.current});
        assert_eq!(bank,2);assert_eq!(b.current,1);
        assert_eq!(b.events,["mount-campaign","native-import","restore-multiplayer"]);
        // Re-entry must acquire a fresh lease, never reuse a cached bank pointer.
        assert_eq!(import(&mut b,true,|b|b.current),2);assert_eq!(b.current,1);
    }
    #[test]
    fn scis_render_context_disabled_campaign_and_missing_bank_keep_native() {
        for (enabled,current,available) in [(false,1,true),(true,2,true),(true,0,true),(true,1,false)] {
            let mut b=Bank{current,available,..Default::default()};let mut calls=0;
            assert_eq!(import(&mut b,enabled,|b|{calls+=1;b.current}),current);
            assert_eq!(calls,1);assert_eq!(b.current,current);
        }
    }
    #[test]
    fn scis_render_context_restores_on_unwind() {
        let mut b=Bank{current:1,available:true,..Default::default()};
        assert!(std::panic::catch_unwind(std::panic::AssertUnwindSafe(||import(&mut b,true,|_|panic!("fixture")))).is_err());
        assert_eq!(b.current,1);assert_eq!(b.events.last(),Some(&"restore-multiplayer"));
    }
}
