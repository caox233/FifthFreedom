// Exact same bytes inserted into the production instance module by the pinned
// additive patch. These controls exercise ownership, not a synthetic driver.
#[path = "../../../../docs/dependency-patches/easytier-2.6.4-windows-session-read/source/windows_session_read.rs"]
mod windows_session_read;

#[cfg(test)]
mod tests {
    use super::windows_session_read::{self as real, CurrentContext, SessionRead};
    use std::{any::Any, io, net::Ipv4Addr, sync::{Arc, atomic::{AtomicBool, AtomicUsize, Ordering}}, time::Duration};
    use tokio::sync::{Mutex, Semaphore};

    struct Observation {
        active: AtomicBool,
        dropped: AtomicBool,
        calls: AtomicUsize,
        entered: Semaphore,
        release: Semaphore,
        generation: u64,
    }
    struct Context(Arc<Observation>);
    impl Drop for Context { fn drop(&mut self) { self.0.dropped.store(true, Ordering::SeqCst); } }
    impl SessionRead for Context {
        type Snapshot = (u64, Ipv4Addr);
        async fn read_session(&self, source: Ipv4Addr) -> io::Result<Self::Snapshot> {
            self.0.calls.fetch_add(1, Ordering::SeqCst);
            self.0.entered.add_permits(1);
            self.0.release.acquire().await.unwrap().forget();
            if !self.0.active.load(Ordering::SeqCst) { return Err(real::unavailable()); }
            Ok((self.0.generation, source))
        }
    }
    struct Container(Option<Box<dyn Any + Send>>);
    impl CurrentContext for Container {
        type Context = Context;
        fn context(&self) -> io::Result<&Context> { real::typed_context(self.0.as_deref()) }
    }
    type Slot = Arc<Mutex<Option<Container>>>;
    const IP: Ipv4Addr = Ipv4Addr::new(10, 87, 0, 2);
    fn observation(generation: u64, ready: bool) -> Arc<Observation> {
        Arc::new(Observation { active: AtomicBool::new(true), dropped: AtomicBool::new(false),
            calls: AtomicUsize::new(0), entered: Semaphore::new(0), release: Semaphore::new(usize::from(ready)), generation })
    }
    fn slot(generation: u64, ready: bool) -> (Slot, Arc<Observation>) {
        let observation = observation(generation, ready);
        let context = Container(Some(Box::new(Context(observation.clone()))));
        (Arc::new(Mutex::new(Some(context))), observation)
    }
    fn read(slot: Slot) -> tokio::task::JoinHandle<io::Result<(u64, Ipv4Addr)>> {
        tokio::spawn(async move { real::read_current(&slot, IP).await })
    }

    #[tokio::test]
    async fn absent_slot_or_empty_or_fallback_context_is_unavailable() {
        for value in [None, Some(Container(None)), Some(Container(Some(Box::new(17u32))))] {
            let slot = Mutex::new(value);
            assert_eq!(real::read_current(&slot, IP).await.unwrap_err().kind(), io::ErrorKind::NotConnected);
        }
    }
    #[tokio::test]
    async fn current_typed_context_is_actually_called_with_explicit_source() {
        let (slot, observation) = slot(7, true);
        assert_eq!(real::read_current(&slot, IP).await.unwrap(), (7, IP));
        assert_eq!(observation.calls.load(Ordering::SeqCst), 1);
        assert!(!observation.dropped.load(Ordering::SeqCst));
    }
    #[tokio::test]
    async fn replacement_waits_for_read_then_next_read_uses_new_context() {
        let (slot, old) = slot(1, false);
        let pending = read(slot.clone()); old.entered.acquire().await.unwrap().forget();
        let replacement = { let slot = slot.clone(); tokio::spawn(async move {
            let next = observation(2, true);
            let mut guard = slot.lock().await;
            *guard = Some(Container(Some(Box::new(Context(next.clone())))));
            next
        }) };
        tokio::task::yield_now().await;
        assert!(!replacement.is_finished());
        old.release.add_permits(1);
        assert_eq!(pending.await.unwrap().unwrap(), (1, IP));
        let next = replacement.await.unwrap();
        assert!(old.dropped.load(Ordering::SeqCst));
        assert_eq!(real::read_current(&slot, IP).await.unwrap(), (2, IP));
        assert_eq!(next.calls.load(Ordering::SeqCst), 1);
    }
    #[tokio::test]
    async fn independent_revocation_during_held_read_still_rejects_late_success() {
        let (slot, observation) = slot(3, false);
        let pending = read(slot.clone()); observation.entered.acquire().await.unwrap().forget();
        observation.active.store(false, Ordering::SeqCst); observation.release.add_permits(1);
        assert_eq!(pending.await.unwrap().unwrap_err().kind(), io::ErrorKind::NotConnected);
        slot.lock().await.take();
        assert!(observation.dropped.load(Ordering::SeqCst));
    }
    #[tokio::test(start_paused = true)]
    async fn timeout_waiting_for_outer_container_never_observes_or_keeps_owner() {
        let (slot, observation) = slot(4, true);
        let guard = slot.lock().await;
        let pending = read(slot.clone()); tokio::task::yield_now().await;
        tokio::time::advance(Duration::from_secs(3)).await;
        assert_eq!(pending.await.unwrap().unwrap_err().kind(), io::ErrorKind::TimedOut);
        assert_eq!(observation.calls.load(Ordering::SeqCst), 0);
        drop(guard); slot.lock().await.take();
        assert!(observation.dropped.load(Ordering::SeqCst));
    }
    #[tokio::test(start_paused = true)]
    async fn timeout_of_inner_read_releases_container_and_owner() {
        let (slot, observation) = slot(5, false);
        let pending = read(slot.clone()); observation.entered.acquire().await.unwrap().forget();
        tokio::time::advance(Duration::from_secs(3)).await;
        assert_eq!(pending.await.unwrap().unwrap_err().kind(), io::ErrorKind::TimedOut);
        slot.try_lock().expect("timeout released the exact container guard").take();
        assert!(observation.dropped.load(Ordering::SeqCst));
    }
    #[tokio::test(start_paused = true)]
    async fn outer_and_inner_wait_share_one_absolute_budget() {
        let (slot, observation) = slot(8, false);
        let guard = slot.lock().await;
        let start = tokio::time::Instant::now();
        let pending = read(slot.clone()); tokio::task::yield_now().await;
        tokio::time::advance(Duration::from_millis(1500)).await;
        drop(guard); observation.entered.acquire().await.unwrap().forget();
        tokio::time::advance(Duration::from_millis(600)).await;
        assert_eq!(pending.await.unwrap().unwrap_err().kind(), io::ErrorKind::TimedOut);
        // A separate inner budget would auto-advance paused time to 3.5s here.
        assert_eq!(start.elapsed(), Duration::from_millis(2100));
        slot.try_lock().expect("shared deadline released outer ownership").take();
        assert!(observation.dropped.load(Ordering::SeqCst));
    }
    #[tokio::test]
    async fn cancellation_of_inner_read_releases_container_and_owner() {
        let (slot, observation) = slot(6, false);
        let pending = read(slot.clone()); observation.entered.acquire().await.unwrap().forget();
        pending.abort(); assert!(pending.await.unwrap_err().is_cancelled());
        slot.try_lock().expect("cancellation released the exact container guard").take();
        assert!(observation.dropped.load(Ordering::SeqCst));
    }
}
