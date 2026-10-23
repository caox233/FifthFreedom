//! Linux ownership of a server data directory lasts exactly as long as this FD.
//! The empty lock file is never deleted: unlinking it could split lock ownership.
#![cfg(target_os = "linux")]
use std::fs::File;
use std::os::fd::{AsRawFd, FromRawFd};
use std::os::unix::fs::{MetadataExt, OpenOptionsExt};
use std::path::Path;

pub(crate) fn acquire(directory: &Path) -> Result<File, &'static str> {
    const INVALID: &str = "SERVER_DATA_LOCK_INVALID: require an owned directory and private regular lock file";
    const BUSY: &str = "SERVER_DATA_LOCK_BUSY: this data directory is already in use";
    let directory = File::options().read(true).custom_flags(libc::O_DIRECTORY | libc::O_NOFOLLOW | libc::O_CLOEXEC)
        .open(directory).map_err(|_| INVALID)?;
    let metadata = directory.metadata().map_err(|_| INVALID)?;
    let owner = unsafe { libc::geteuid() };
    if metadata.uid() != owner || metadata.mode() & 0o022 != 0 { return Err(INVALID); }
    // SAFETY: the live directory FD and static NUL-terminated relative basename
    // are valid. The nonnegative FD is immediately transferred to File ownership.
    let fd = unsafe { libc::openat(directory.as_raw_fd(), c"server.lock".as_ptr(),
        libc::O_RDWR | libc::O_CREAT | libc::O_NOFOLLOW | libc::O_NONBLOCK | libc::O_CLOEXEC, 0o600) };
    if fd < 0 { return Err(INVALID); }
    let file = unsafe { File::from_raw_fd(fd) };
    let metadata = file.metadata().map_err(|_| INVALID)?;
    if !metadata.is_file() || metadata.uid() != owner || metadata.nlink() != 1
        || metadata.mode() & 0o7777 != 0o600 || metadata.len() != 0 {
        return Err(INVALID);
    }
    if unsafe { libc::flock(file.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) } != 0 { return Err(BUSY); }
    Ok(file)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::os::unix::fs::{PermissionsExt, symlink};
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicU64, Ordering};
    static NEXT: AtomicU64 = AtomicU64::new(0);
    struct Fixture(PathBuf);
    impl Fixture {
        fn new() -> Self {
            let path = std::env::temp_dir().join(format!("fifth-freedom-synthetic-lock-{}-{}",
                std::process::id(), NEXT.fetch_add(1, Ordering::Relaxed)));
            fs::create_dir(&path).unwrap();
            fs::set_permissions(&path, fs::Permissions::from_mode(0o700)).unwrap();
            Self(path)
        }
    }
    impl Drop for Fixture { fn drop(&mut self) { fs::remove_dir_all(&self.0).unwrap(); } }

    #[test]
    fn exclusive_handle_releases_on_drop_without_unlinking() {
        let fixture = Fixture::new();
        let first = acquire(&fixture.0).unwrap();
        let original_inode = first.metadata().unwrap().ino();
        assert_eq!(acquire(&fixture.0).unwrap_err(), "SERVER_DATA_LOCK_BUSY: this data directory is already in use");
        drop(first);
        assert!(fixture.0.join("server.lock").exists());
        let next = acquire(&fixture.0).unwrap();
        assert_eq!(next.metadata().unwrap().ino(), original_inode);
    }

    #[test]
    fn separate_data_directories_do_not_share_a_lock() {
        let first = Fixture::new();
        let second = Fixture::new();
        let _first_lock = acquire(&first.0).unwrap();
        let _second_lock = acquire(&second.0).unwrap();
    }

    #[test]
    fn symlink_hardlink_and_permissive_files_fail_without_overwrite() {
        let fixture = Fixture::new();
        let target = fixture.0.join("unique-evidence");
        fs::write(&target, b"preserve original").unwrap();
        let lock = fixture.0.join("server.lock");
        symlink(&target, &lock).unwrap();
        assert!(acquire(&fixture.0).is_err());
        assert_eq!(fs::read(&target).unwrap(), b"preserve original");
        fs::remove_file(&lock).unwrap();
        fs::hard_link(&target, &lock).unwrap();
        assert!(acquire(&fixture.0).is_err());
        fs::remove_file(&lock).unwrap();
        fs::write(&lock, b"").unwrap();
        fs::set_permissions(&lock, fs::Permissions::from_mode(0o644)).unwrap();
        assert!(acquire(&fixture.0).is_err());
        fs::set_permissions(&lock, fs::Permissions::from_mode(0o600)).unwrap();
        fs::set_permissions(&fixture.0, fs::Permissions::from_mode(0o777)).unwrap();
        assert!(acquire(&fixture.0).is_err());
    }
}
