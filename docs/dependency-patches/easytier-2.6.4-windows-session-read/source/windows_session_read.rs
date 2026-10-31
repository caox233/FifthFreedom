//! One bounded read of the current typed NIC context. Never an admission lease.
use std::{any::Any, future::Future, io, net::Ipv4Addr, time::Duration};
use tokio::sync::Mutex;

const READ_WAIT: Duration = Duration::from_secs(2);

pub(super) fn unavailable() -> io::Error {
    io::Error::new(io::ErrorKind::NotConnected, "FF_WINDOWS_SESSION_UNAVAILABLE")
}

pub(super) fn typed_context<T: 'static>(
    value: Option<&(dyn Any + Send)>,
) -> io::Result<&T> {
    value.and_then(|value| value.downcast_ref::<T>()).ok_or_else(unavailable)
}

pub(super) trait SessionRead: Sync {
    type Snapshot: Send;
    fn read_session(&self, source: Ipv4Addr)
        -> impl Future<Output = io::Result<Self::Snapshot>> + Send;
}

pub(super) trait CurrentContext: Send {
    type Context: SessionRead;
    fn context(&self) -> io::Result<&Self::Context>;
}

/// The outer container guard prevents replacing this context during its inner
/// awaited read. Timeout/cancellation drops the future and guard; no Session Arc,
/// observer or owner is cloned. The backend still independently rechecks owner/
/// worker revocation. This timeout bounds async lock waits, not synchronous OS
/// query duration. A returned snapshot can become stale immediately afterwards.
pub(super) async fn read_current<C: CurrentContext>(
    slot: &Mutex<Option<C>>,
    source: Ipv4Addr,
) -> io::Result<<C::Context as SessionRead>::Snapshot> {
    tokio::time::timeout(READ_WAIT, async {
        let current = slot.lock().await;
        let context = current.as_ref().ok_or_else(unavailable)?.context()?;
        context.read_session(source).await
    }).await.map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "FF_WINDOWS_SESSION_BUSY"))?
}
