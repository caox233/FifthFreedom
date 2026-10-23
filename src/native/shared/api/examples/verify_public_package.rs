//! Read-only CI/offline verifier. This does not start a server, open a listener,
//! write a package, or enable the native public deployment profile.
#[cfg(target_os = "linux")]
fn main() -> std::process::ExitCode {
    let mut args = std::env::args_os().skip(1);
    let Some(root) = args.next() else {
        eprintln!("PUBLIC_PACKAGE_ARGUMENT_INVALID");
        return std::process::ExitCode::FAILURE;
    };
    if args.next().is_some() {
        eprintln!("PUBLIC_PACKAGE_ARGUMENT_INVALID");
        return std::process::ExitCode::FAILURE;
    }
    match api::public_package::verify_public_package(std::path::Path::new(&root)) {
        Ok(package) => {
            let service = package.service_identity();
            println!("{}", serde_json::json!({
                "schema": 1, "result": "verified", "platform": service.platform(),
                "service": {
                    "release_id": service.release_id(), "compat_id": service.compat_id(),
                    "manifest_sha256": service.manifest_sha256(),
                    "executable_sha256": service.executable_sha256(),
                },
                "admitted_client": {"storage_key": package.admitted_client().storage_key()},
            }));
            std::process::ExitCode::SUCCESS
        }
        Err(error) => { eprintln!("{}", error.reason()); std::process::ExitCode::FAILURE }
    }
}

#[cfg(not(target_os = "linux"))]
fn main() -> std::process::ExitCode {
    eprintln!("PUBLIC_PACKAGE_PLATFORM_UNSUPPORTED");
    std::process::ExitCode::FAILURE
}
