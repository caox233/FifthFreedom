//! Only the launcher's private stdin pipe changes this server-run policy.
//! Never load it from service.toml, account data or a network request.
use std::io::{self, BufRead, Read};
use std::sync::atomic::{AtomicBool, Ordering};

static ALLOWED: AtomicBool = AtomicBool::new(false);
pub const HEADER: &str = server_api::observer_contract::REQUEST_HEADER;
pub fn allowed() -> bool { ALLOWED.load(Ordering::Acquire) }

fn command(line: &[u8]) -> Option<bool> {
    match line {
        b"OB_ALLOW=1\n" | b"OB_ALLOW=1\r\n" => Some(true),
        b"OB_ALLOW=0\n" | b"OB_ALLOW=0\r\n" => Some(false),
        _ => None,
    }
}

// Bound reads even if the controlling process sends an invalid/unbounded line.
fn read_commands(reader: &mut impl BufRead, mut publish: impl FnMut(bool)) -> io::Result<()> {
    publish(false);
    loop {
        let mut line = Vec::with_capacity(16);
        let n = (&mut *reader).take(32).read_until(b'\n', &mut line)?;
        if n == 0 { publish(false); return Ok(()); }
        let Some(value) = command(&line) else { publish(false); return Err(io::Error::new(io::ErrorKind::InvalidData, "invalid launcher control frame")); };
        publish(value);
    }
}

pub fn start() -> io::Result<()> {
    // Explicit child-only switch. Standalone servers remain closed by default.
    ALLOWED.store(false, Ordering::Release);
    if std::env::var_os("SCBL_LAUNCHER_CONTROL").as_deref() != Some(std::ffi::OsStr::new("1")) { return Ok(()); }
    std::thread::Builder::new().name("launcher-control".into()).spawn(|| {
        let result = read_commands(&mut io::stdin().lock(), |v| ALLOWED.store(v, Ordering::Release));
        ALLOWED.store(false, Ordering::Release);
        if let Err(e) = result { eprintln!("OB control closed: {e}"); }
    })?;
    Ok(())
}

pub fn admit(header: Option<&str>, game_request: bool, policy: bool) -> Result<(), tonic::Status> {
    match header {
        None => Ok(()),
        Some("1") if !game_request => Err(tonic::Status::invalid_argument("OB is a game-run option, not an account option")),
        Some("1") if policy => Ok(()),
        Some("1") => Err(tonic::Status::permission_denied("OB_NOT_ALLOWED: host has not enabled OB")),
        Some(_) => Err(tonic::Status::invalid_argument("Invalid OB request")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;
    #[test] fn commands_live_only_until_controller_eof() {
        let mut history=vec![];
        read_commands(&mut Cursor::new(b"OB_ALLOW=1\nOB_ALLOW=0\r\nOB_ALLOW=1\n"),|v|history.push(v)).unwrap();
        assert_eq!(history,[false,true,false,true,false]);
    }
    #[test] fn invalid_partial_or_oversized_input_closes_policy() {
        for bytes in [b"OB_ALLOW=1".as_slice(),b"OB_ALLOW=true\n",b"XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX\n"] {
            let mut history=vec![];
            assert!(read_commands(&mut Cursor::new(bytes),|v|history.push(v)).is_err());
            assert_eq!(history.last(),Some(&false));
        }
    }
    #[test] fn ordinary_player_never_requires_ob_permission() {
        for allowed in [false,true] { assert!(admit(None,true,allowed).is_ok()); }
    }
    #[test] fn host_permission_is_checked_before_run_creation() {
        assert_eq!(admit(Some("1"),true,false).unwrap_err().code(),tonic::Code::PermissionDenied);
        assert!(admit(Some("1"),true,true).is_ok());
        assert!(admit(Some("1"),false,true).is_err());
        assert!(admit(Some("true"),true,true).is_err());
    }
}
