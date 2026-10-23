//! Explicit, fail-closed key sharing for a trusted Linux control-plane process.
//!
//! The default LAN key never touches disk. This optional input is provisioned by
//! an operator/service manager; this module neither generates nor writes files.
use std::path::Path;
use sodiumoxide::crypto::auth::hmacsha256;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum KeyFileError {
    #[cfg(any(test, not(target_os = "linux")))]
    Unsupported,
    #[cfg(any(test, target_os = "linux"))]
    Invalid,
}

impl std::fmt::Display for KeyFileError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            #[cfg(any(test, not(target_os = "linux")))]
            Self::Unsupported => "AUTH_KEY_FILE_UNSUPPORTED: explicit key files require Linux",
            #[cfg(any(test, target_os = "linux"))]
            Self::Invalid => "AUTH_KEY_FILE_INVALID: require an absolute, private, owner-only regular 32-byte key file",
        })
    }
}
impl std::error::Error for KeyFileError {}

#[cfg(not(target_os = "linux"))]
pub(crate) fn load(_path: &Path) -> Result<hmacsha256::Key, KeyFileError> {
    Err(KeyFileError::Unsupported)
}

#[cfg(target_os = "linux")]
fn normalized_absolute(path: &Path) -> bool {
    use std::os::unix::ffi::OsStrExt;
    path.is_absolute() && !path.as_os_str().as_bytes().split(|byte| *byte == b'/').skip(1)
        .any(|part| part.is_empty() || part == b"." || part == b"..")
}

/// Open an existing owner-only directory without following any path component.
/// Shared by key loading and the read-only public deployment preflight.
#[cfg(target_os = "linux")]
pub(crate) fn private_directory(path: &Path) -> Result<std::fs::File, KeyFileError> {
    use std::ffi::CString;
    use std::fs::File;
    use std::os::fd::{AsRawFd, FromRawFd};
    use std::os::unix::ffi::OsStrExt;
    use std::os::unix::fs::MetadataExt;
    use std::path::Component;
    if !normalized_absolute(path) { return Err(KeyFileError::Invalid); }
    let mut directory = File::open("/").map_err(|_| KeyFileError::Invalid)?;
    let owner = unsafe { libc::geteuid() };
    for component in path.components().skip(1) {
        let Component::Normal(part) = component else { return Err(KeyFileError::Invalid); };
        let part = CString::new(part.as_bytes()).map_err(|_| KeyFileError::Invalid)?;
        let ancestor = directory.metadata().map_err(|_| KeyFileError::Invalid)?;
        if !trusted_ancestor(ancestor.uid(), ancestor.mode(), owner) { return Err(KeyFileError::Invalid); }
        // SAFETY: parent FD remains live and CString is NUL terminated. Only a
        // nonnegative uniquely owned descriptor is transferred to File below.
        let fd = unsafe { libc::openat(directory.as_raw_fd(), part.as_ptr(),
            libc::O_RDONLY | libc::O_DIRECTORY | libc::O_NOFOLLOW | libc::O_CLOEXEC) };
        if fd < 0 { return Err(KeyFileError::Invalid); }
        directory = unsafe { File::from_raw_fd(fd) };
    }
    let metadata = directory.metadata().map_err(|_| KeyFileError::Invalid)?;
    if metadata.uid() != owner || !matches!(metadata.mode() & 0o7777, 0o500 | 0o700) {
        return Err(KeyFileError::Invalid);
    }
    Ok(directory)
}

#[cfg(target_os = "linux")]
fn open_private(path: &Path, maximum: u64) -> Result<(std::fs::File, std::fs::Metadata), KeyFileError> {
    use std::ffi::CString;
    use std::fs::File;
    use std::os::fd::{AsRawFd, FromRawFd};
    use std::os::unix::ffi::OsStrExt;
    use std::os::unix::fs::MetadataExt;
    if !normalized_absolute(path) { return Err(KeyFileError::Invalid); }
    let directory = private_directory(path.parent().ok_or(KeyFileError::Invalid)?)?;
    let name = CString::new(path.file_name().ok_or(KeyFileError::Invalid)?.as_bytes()).map_err(|_| KeyFileError::Invalid)?;
    let fd = unsafe { libc::openat(directory.as_raw_fd(), name.as_ptr(),
        libc::O_RDONLY | libc::O_NOFOLLOW | libc::O_NONBLOCK | libc::O_CLOEXEC) };
    if fd < 0 { return Err(KeyFileError::Invalid); }
    let file = unsafe { File::from_raw_fd(fd) };
    let before = file.metadata().map_err(|_| KeyFileError::Invalid)?;
    if !before.is_file() || before.uid() != unsafe { libc::geteuid() } || before.nlink() != 1
        || !matches!(before.mode() & 0o7777, 0o400 | 0o600) || before.len() == 0 || before.len() > maximum {
        return Err(KeyFileError::Invalid);
    }
    Ok((file, before))
}

#[cfg(target_os = "linux")]
fn unchanged(before: &std::fs::Metadata, after: &std::fs::Metadata) -> bool {
    use std::os::unix::fs::MetadataExt;
    (before.dev(), before.ino(), before.len(), before.uid(), before.gid(), before.mode(), before.nlink(),
        before.mtime(), before.mtime_nsec(), before.ctime(), before.ctime_nsec())
        == (after.dev(), after.ino(), after.len(), after.uid(), after.gid(), after.mode(), after.nlink(),
            after.mtime(), after.mtime_nsec(), after.ctime(), after.ctime_nsec())
}

/// Bounded operator document read using the exact same file/ancestor policy as
/// the signing key. This is not used for key bytes, which retain sodium wiping.
#[cfg(target_os = "linux")]
pub(crate) fn read_private_document(path: &Path, maximum: u64) -> Result<Vec<u8>, KeyFileError> {
    use std::io::Read;
    let (mut file, before) = open_private(path, maximum)?;
    let mut bytes = Vec::with_capacity(before.len() as usize);
    (&mut file).take(maximum + 1).read_to_end(&mut bytes).map_err(|_| KeyFileError::Invalid)?;
    if bytes.len() as u64 != before.len() || !unchanged(&before, &file.metadata().map_err(|_| KeyFileError::Invalid)?) {
        return Err(KeyFileError::Invalid);
    }
    Ok(bytes)
}

#[cfg(target_os = "linux")]
pub(crate) fn load(path: &Path) -> Result<hmacsha256::Key, KeyFileError> {
    use std::io::Read;
    let (mut file, before) = open_private(path, 32)?;
    if before.len() != 32 { return Err(KeyFileError::Invalid); }
    // Read directly into the sodium key wrapper, whose Drop clears the bytes.
    let mut key = hmacsha256::Key([0; 32]);
    file.read_exact(&mut key.0).map_err(|_| KeyFileError::Invalid)?;
    let mut extra = [0; 1];
    if file.read(&mut extra).map_err(|_| KeyFileError::Invalid)? != 0
        || !unchanged(&before, &file.metadata().map_err(|_| KeyFileError::Invalid)?) {
        return Err(KeyFileError::Invalid);
    }
    Ok(key)
}

#[cfg(target_os = "linux")]
fn trusted_ancestor(uid: u32, mode: u32, owner: u32) -> bool {
    (uid == 0 || uid == owner) && (mode & 0o022 == 0 || (uid == 0 && mode & 0o1000 != 0))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn key_file_error_never_contains_input_or_key() {
        assert_eq!(KeyFileError::Unsupported.to_string(), "AUTH_KEY_FILE_UNSUPPORTED: explicit key files require Linux");
        assert!(KeyFileError::Invalid.to_string().starts_with("AUTH_KEY_FILE_INVALID:"));
    }

    #[cfg(not(target_os = "linux"))]
    #[test]
    fn explicit_key_file_is_unsupported_on_this_platform() {
        assert!(matches!(load(Path::new("synthetic-key")), Err(KeyFileError::Unsupported)));
    }

    #[cfg(target_os = "linux")]
    mod linux {
        use super::*;
        use std::fs;
        use std::io::Write;
        use std::os::unix::fs::{symlink, OpenOptionsExt, PermissionsExt};
        use std::path::PathBuf;
        use std::sync::atomic::{AtomicU64, Ordering};

        static NEXT: AtomicU64 = AtomicU64::new(0);
        struct Fixture(PathBuf);
        impl Fixture {
            fn new() -> Self {
                let path = std::env::temp_dir().join(format!("fifth-freedom-synthetic-key-{}-{}",
                    std::process::id(), NEXT.fetch_add(1, Ordering::Relaxed)));
                fs::create_dir(&path).unwrap();
                fs::set_permissions(&path, fs::Permissions::from_mode(0o700)).unwrap();
                Self(path)
            }
            fn file(&self, name: &str, bytes: &[u8]) -> PathBuf {
                let path = self.0.join(name);
                let mut file = fs::OpenOptions::new().write(true).create_new(true).mode(0o600).open(&path).unwrap();
                file.write_all(bytes).unwrap();
                path
            }
        }
        impl Drop for Fixture { fn drop(&mut self) { fs::remove_dir_all(&self.0).unwrap(); } }

        #[test]
        fn exact_private_key_can_be_shared_across_process_starts() {
            let fixture = Fixture::new();
            let path = fixture.file("key", &[0x61; 32]);
            assert!(load(&path).unwrap().0 == [0x61; 32]);
            assert!(load(&path).unwrap().0 == [0x61; 32]);
            fs::set_permissions(&path, fs::Permissions::from_mode(0o400)).unwrap();
            assert!(load(&path).is_ok());
        }

        #[test]
        fn size_type_mode_missing_and_parent_access_fail_closed() {
            let fixture = Fixture::new();
            for length in [0, 31, 33, 4096] {
                let path = fixture.file(&format!("size-{length}"), &vec![0x61; length]);
                assert!(load(&path).is_err());
            }
            assert!(load(&fixture.0.join("absent")).is_err());
            assert!(load(&fixture.0).is_err());
            assert!(load(Path::new("relative-key")).is_err());
            let path = fixture.file("key", &[0x61; 32]);
            for mode in [0o644, 0o640, 0o660, 0o000, 0o700, 0o4600] {
                fs::set_permissions(&path, fs::Permissions::from_mode(mode)).unwrap();
                assert!(load(&path).is_err());
            }
            fs::set_permissions(&path, fs::Permissions::from_mode(0o600)).unwrap();
            fs::set_permissions(&fixture.0, fs::Permissions::from_mode(0o755)).unwrap();
            assert!(load(&path).is_err());
        }

        #[test]
        fn file_directory_symlinks_and_hardlinks_are_rejected() {
            let fixture = Fixture::new();
            let path = fixture.file("key", &[0x61; 32]);
            let link = fixture.0.join("key-link");
            symlink(&path, &link).unwrap();
            assert!(load(&link).is_err());
            let directory_link = fixture.0.join("directory-link");
            symlink(&fixture.0, &directory_link).unwrap();
            assert!(load(&directory_link.join("key")).is_err());
            fs::hard_link(&path, fixture.0.join("key-hardlink")).unwrap();
            assert!(load(&path).is_err());
        }

        #[test]
        fn ancestors_must_be_trusted_even_when_direct_parent_is_private() {
            assert!(trusted_ancestor(0, 0o755, 1234));
            assert!(trusted_ancestor(0, 0o1777, 1234));
            assert!(trusted_ancestor(1234, 0o700, 1234));
            assert!(!trusted_ancestor(1234, 0o1777, 1234));
            assert!(!trusted_ancestor(0, 0o777, 1234));
            assert!(!trusted_ancestor(4321, 0o700, 1234));
            let fixture = Fixture::new();
            let private = fixture.0.join("private");
            fs::create_dir(&private).unwrap();
            fs::set_permissions(&private, fs::Permissions::from_mode(0o700)).unwrap();
            let path = private.join("key");
            let mut file = fs::OpenOptions::new().write(true).create_new(true).mode(0o600).open(&path).unwrap();
            file.write_all(&[0x61; 32]).unwrap();
            fs::set_permissions(&fixture.0, fs::Permissions::from_mode(0o777)).unwrap();
            assert!(load(&path).is_err());
        }

        #[test]
        fn rotation_is_explicit_and_old_loaded_epoch_is_not_mutated() {
            let fixture = Fixture::new();
            let path = fixture.file("key", &[0x61; 32]);
            let loaded = load(&path).unwrap();
            let next = fixture.file("next", &[0x62; 32]);
            fs::rename(next, &path).unwrap();
            assert!(loaded.0 == [0x61; 32]);
            assert!(load(&path).unwrap().0 == [0x62; 32]);
        }
    }
}
