#![deny(clippy::pedantic)]
#![feature(iter_intersperse)]

#[macro_use]
extern crate quazal_macros;
#[macro_use]
extern crate slog;

use std::fs;
use std::io;
use std::io::Write;
use std::net::SocketAddr;
use std::net::UdpSocket;
use std::path::Path;
use std::path::PathBuf;
use std::sync::mpsc;
use std::sync::Arc;

use quazal::prudp::packet::QPacket;
use quazal::ClientInfo;
use quazal::Context;
use quazal::socket_policy::SocketPolicy;
use quazal::source_policy::SourcePolicy;
use sc_bl_protocols as protocols;
use slog::Drain;
use slog::Logger;
use sloggers::Build;
use storage::Storage;

const DEFAULT_MP_DATA: &str = include_str!("../assets/mp_balancing.ini");

const SERVER_PID: u32 = 0x1000;

macro_rules! rmc_err {
    ($e:expr, $log:expr, $msg:literal) => (
        $e.map_err(|e| {
            slog::error!($log, $msg; "error" => ?e);
            quazal::rmc::Error::InternalError
        })
    )
}

/// Checks if a client is logged in and returns their user ID.
///
/// Returns an `AccessDenied` error if the client is not logged in.
fn login_required<T>(ci: &ClientInfo<T>) -> quazal::rmc::Result<u32> {
    ci.user_id.ok_or(quazal::rmc::Error::AccessDenied)
}

mod api;
mod auth_key_file;
#[cfg(target_os = "linux")]
mod instance_lock;
#[cfg(target_os = "linux")]
mod public_preflight;
#[cfg(target_os = "linux")]
mod public_lease;
mod challenge;
mod clan;
mod config;
mod deployment_profile;
mod lan_runtime;
mod observer_policy;
mod game_session;
mod game_session_ex;
mod gameplay_modes;
mod ladder;
mod locale;
mod nat_traversal;
mod overlord_challenge;
mod overlord_core;
mod overlord_news;
mod player_stats;
mod privileges;
mod secure;
mod simple_http;
mod storage;
mod ticket;
mod tracking;
mod tracking_ext;
mod ubi_acc_mgmt;
mod uplay_win;
mod user_storage;

use crate::config::Config;
use crate::deployment_profile::DeploymentProfile;

type NativeServer<'a> = quazal::prudp::Server<'a, fn(ClientInfo), fn(ClientInfo)>;

// Shared by the actual secure listener and maintenance expiry fixtures. The
// transport may forget this exact owner only after its close transaction commits.
fn configure_secure_close_handler<'a>(server: &mut NativeServer<'a>, logger: &'a Logger, storage: &'a Storage) {
    server.close_handler = Some(Box::new(move |ci, reason| {
        let Some(user_id) = ci.user_id else { return true; };
        match storage.try_close_secure_ticket(user_id, ci.ticket_fingerprint) {
            Ok(storage::SecureCloseAttempt::Committed { preserved }) => {
                info!(logger, "Secure close committed"; "user_id" => user_id, "reason" => ?reason, "preserved" => preserved);
                true
            }
            Ok(storage::SecureCloseAttempt::Deferred) => false,
            Err(e) => {
                error!(logger, "Secure close deferred; retaining ticket owner"; "user_id" => user_id, "reason" => ?reason, "error" => %e);
                false
            }
        }
    }));
}

#[cfg(all(test, feature = "maintenance-test-seams"))]
mod secure_close_retry_tests;

/// Starts a Quazal server (either secure or authentication).
///
/// This function sets up the necessary protocols and handlers for the server
/// and then enters the server loop.
fn start_server(logger: &slog::Logger, ctx: &Context, storage: &Arc<Storage>, is_secure: bool, socket_policy: &SocketPolicy, source_policy: SourcePolicy) -> io::Result<()> {
    use quazal::prudp::packet::StreamHandlerRegistry;
    use quazal::prudp::packet::StreamType;
    use quazal::prudp::packet::VPort;
    use quazal::prudp::Server;
    use quazal::rmc::RVSecHandler;

    let mut handler = RVSecHandler::<()>::new(logger.clone());

    if is_secure {
        handler.register_protocol(challenge::new_protocol());
        handler.register_protocol(clan::new_protocol());
        handler.register_protocol(game_session_ex::new_protocol(Arc::clone(storage)));
        handler.register_protocol(game_session::new_protocol(Arc::clone(storage)));
        handler.register_protocol(ladder::new_protocol());
        handler.register_protocol(locale::new_protocol());
        handler.register_protocol(nat_traversal::new_protocol());
        handler.register_protocol(overlord_challenge::new_protocol());
        handler.register_protocol(overlord_core::new_protocol());
        handler.register_protocol(overlord_news::new_protocol());
        handler.register_protocol(player_stats::new_protocol());
        handler.register_protocol(privileges::new_protocol());
        handler.register_protocol(secure::new_protocol(Arc::clone(storage)));
        handler.register_protocol(tracking_ext::new_protocol());
        handler.register_protocol(tracking::new_protocol());
        handler.register_protocol(ubi_acc_mgmt::new_protocol(Arc::clone(storage)));
        handler.register_protocol(uplay_win::new_protocol());
        handler.register_protocol(user_storage::new_protocol());
    } else {
        handler.register_protocol(ticket::new_protocol_with_source_policy(Arc::clone(storage), source_policy));
    }

    let mut registry = StreamHandlerRegistry::new(logger.clone());
    registry.register(
        VPort {
            stream_type: StreamType::RVSec,
            port: ctx.vport,
        },
        Box::new(handler),
    );

    let mut server: NativeServer<'_> = Server::new_with_source_policy(logger.clone(), ctx, registry, source_policy);
    if is_secure {
        // Only the secure/game connection owns online presence and room state.
        // The short-lived authentication connection must not clear an active
        // secure session when it disconnects or expires.
        configure_secure_close_handler(&mut server, logger, storage);
        server.user_handler = Some(handle_user_packet);
    }
    server.bind_with_policy(ctx.listen, socket_policy)?;
    lan_runtime::mark_ready(if is_secure { 8 } else { 4 });
    server.serve();
    Ok(())
}

/// Handles user-specific RMC packets.
///
/// This function is a placeholder for handling user-specific RMC packets.
fn handle_user_packet(logger: &Logger, packet: QPacket, client: SocketAddr, socket: &UdpSocket) {
    if packet.source.port != 1 || packet.destination.port != 1 {
        warn!(
            logger,
            "Dropping user packet with unexpected virtual ports";
            "client" => client.to_string(),
            "source_port" => packet.source.port,
            "destination_port" => packet.destination.port,
        );
        return;
    }

    let payload = packet.payload;

    let mut response = payload;
    if let Err(error) = write!(&mut response, "udp:/address={};port={}\0", client.ip(), client.port()) {
        error!(logger, "Failed to build user packet response"; "client" => client.to_string(), "error" => %error);
        return;
    }
    if let Err(error) = socket.send_to(&response, client) {
        warn!(logger, "Failed to send user packet response"; "client" => client.to_string(), "error" => %error);
    }
}

/// Builds a terminal logger with a configurable log level.
fn build_term_logger() -> Logger {
    sloggers::terminal::TerminalLoggerBuilder::new()
        .level(
            #[allow(clippy::match_same_arms)]
            match std::env::var("RUST_LOG").unwrap_or_else(|_| String::from("info")).as_str() {
                "debug" => sloggers::types::Severity::Debug,
                "trace" => sloggers::types::Severity::Trace,
                "info" => sloggers::types::Severity::Info,
                "error" => sloggers::types::Severity::Error,
                "critical" => sloggers::types::Severity::Critical,
                "warning" => sloggers::types::Severity::Warning,
                _ => sloggers::types::Severity::Trace,
            },
        )
        .format(sloggers::types::Format::Compact)
        .build()
        .unwrap()
}

/// Rotates log files, keeping a specified number of backups.
fn rotate_log_files<S: AsRef<Path>>(fname: S, i: i32) -> io::Result<PathBuf> {
    let fname = fname.as_ref();
    if fname.exists() {
        let maybe_iteration = fname.extension().and_then(|s| s.to_str()).and_then(|s| s.parse::<i32>().ok());
        let new_fname = match maybe_iteration {
            Some(j) if j == i - 1 => format!("{}.{}", fname.file_stem().and_then(|f| f.to_str()).unwrap(), i),
            _ => format!("{}.{}", fname.display(), i),
        };
        if i < 10 {
            rotate_log_files(&new_fname, i + 1)?;
        }
        fs::rename(fname, new_fname)?;
    }
    Ok(fname.to_path_buf())
}

/// Builds a file logger that writes to a JSON file.
fn build_file_logger() -> Logger {
    let fname = rotate_log_files("server.log.json", 1).unwrap();
    sloggers::file::FileLoggerBuilder::new(fname)
        .truncate()
        .level(sloggers::types::Severity::Info)
        .format(sloggers::types::Format::Json)
        .build()
        .unwrap()
}

/// Ensures that the necessary data directory and files exist.
fn ensure_data_dir() -> io::Result<()> {
    let mp_ini = std::env::current_dir()?.join("data").join("mp_balancing.ini");
    if mp_ini.exists() {
        return Ok(());
    }
    fs::create_dir_all(mp_ini.parent().unwrap())?;
    fs::write(mp_ini, DEFAULT_MP_DATA)?;
    Ok(())
}

#[derive(argh::FromArgs)]
/// dedicated server
struct Args {
    /// deployment profile: lan (default) or public (not integrated; fails closed)
    #[argh(option, default = "DeploymentProfile::Lan")]
    deployment_profile: DeploymentProfile,
    /// optional absolute private 32-byte key file for trusted Linux control-plane sharing
    #[argh(option)]
    auth_key_file: Option<PathBuf>,
    /// read-only Linux public deployment check; never starts any service
    #[argh(option)]
    check_public_deployment: Option<PathBuf>,
    /// inherited trusted owner lease; valid only with explicit public deployment config
    #[argh(option)]
    owned_public_lease_fd: Option<i32>,
    /// private Linux public deployment config for inherited owner lease startup
    #[argh(option)]
    public_deployment_config: Option<PathBuf>,
    /// path to config file (default: service.toml)
    #[argh(option, short = 'c', long = "config")]
    config_path: Option<PathBuf>,
    /// create a new independent LAN config for this IPv4, then exit
    #[argh(option)]
    init_lan: Option<std::net::Ipv4Addr>,
    /// independent server data directory (default: executable directory/lan-server-data)
    #[argh(option)]
    data_dir: Option<PathBuf>,
    /// use alternate ports only for automated development smoke tests
    #[argh(switch)]
    smoke_ports: bool,

    /// started through launcher
    #[argh(switch)]
    launcher: bool,
}

#[derive(Debug, PartialEq, Eq)]
struct RequiredServiceExit {
    name: String,
    reason: String,
}

fn spawn_required_service<F>(name: String, exit_sender: mpsc::Sender<RequiredServiceExit>, run: F) -> io::Result<std::thread::JoinHandle<()>>
where
    F: FnOnce() -> Result<(), String> + Send + 'static,
{
    std::thread::Builder::new().name(name.clone()).spawn(move || {
        let reason = match std::panic::catch_unwind(std::panic::AssertUnwindSafe(run)) {
            Ok(Ok(())) => String::from("returned unexpectedly"),
            Ok(Err(error)) => error,
            Err(_) => String::from("panicked"),
        };
        let _ = exit_sender.send(RequiredServiceExit { name, reason });
    })
}

fn main() -> color_eyre::Result<()> {
    color_eyre::install()?;
    let args = argh::from_env::<Args>();
    if let Some(path) = args.check_public_deployment.as_deref() {
        #[cfg(target_os = "linux")]
        {
            public_preflight::arguments_valid(&args).map_err(|reason| eyre::eyre!(reason))?;
            let report = public_preflight::check(path, &std::env::current_exe()?).map_err(|reason| eyre::eyre!(reason))?;
            println!("{report}");
            return Ok(());
        }
        #[cfg(not(target_os = "linux"))]
        {
            let _ = path;
            return Err(eyre::eyre!("PUBLIC_PREFLIGHT_LINUX_REQUIRED"));
        }
    }
    if args.owned_public_lease_fd.is_some() || args.public_deployment_config.is_some() {
        #[cfg(target_os = "linux")]
        { return run_owned_public(&args).map_err(|code| eyre::eyre!(code)); }
        #[cfg(not(target_os = "linux"))]
        { return Err(eyre::eyre!("PUBLIC_ACTIVATION_LINUX_REQUIRED")); }
    }
    // Resolve deployment before any persistent state or LAN-only startup action.
    // Public deliberately has no startup plan until its secure adapters exist.
    let startup_plan = args.deployment_profile.startup_plan()?;
    // Fail closed before data creation, database mutation, listeners or readiness.
    let verified_release = server_api::release_catalog::verify_server_package(&std::env::current_exe()?)?;
    sodiumoxide::init().map_err(|_| eyre::eyre!("crypto initialization failed"))?;
    // Validate the explicit key input and TTL before data mutation or listeners.
    let token_config = api::AuthTokenConfig::from_startup(verified_release.clone(), args.auth_key_file.as_deref())
        .map_err(|reason| eyre::eyre!(reason))?;
    let root = startup_plan.data_root(args.data_dir.clone(), &std::env::current_exe()?)?;
    std::fs::create_dir_all(&root)?;
    std::env::set_current_dir(std::fs::canonicalize(&root)?)?;
    #[cfg(target_os = "linux")]
    let _instance_lock = instance_lock::acquire(Path::new(".")).map_err(|reason| eyre::eyre!(reason))?;
    #[cfg(target_os = "windows")]
    let _instance_lock = {
        use std::os::windows::fs::OpenOptionsExt;
        std::fs::OpenOptions::new().write(true).create(true).truncate(false).share_mode(0)
            .open("server.lock")
            .map_err(|error| eyre::eyre!("This LAN data directory is already in use or cannot be locked: {error}"))?
    };
    if let Some(ip) = args.init_lan {
        lan_runtime::initialize(ip, args.smoke_ports)?;
        println!("SCBL LAN server initialized. No old data imported.");
        return Ok(());
    }
    if !Path::new("service.toml").is_file() { return Err(eyre::eyre!("Run --init-lan YOUR_IPV4 first")); }

    let logger = if args.launcher {
        Logger::root(build_file_logger(), o!())
    } else {
        let logger = build_term_logger();
        Logger::root(slog::Duplicate(logger, build_file_logger()).fuse(), o!())
    };

    {
        let logger = logger.clone();
        let old_hook = std::panic::take_hook();
        std::panic::set_hook(Box::new(move |panic_info| {
            error!(logger, "Panic occurred: {panic_info}");
            old_hook(panic_info);
        }));
    }

    let storage = Arc::new(Storage::init(logger.clone(), verified_release.clone())?);

    let config_filename = args.config_path.unwrap_or_else(|| PathBuf::from("service.toml"));

    let config = Config::load_from_file(&config_filename)?;
    lan_runtime::configure_ports(&config);
    observer_policy::start()?;

    ensure_data_dir()?;

    warn!(logger, "Clearing stale sessions");
    storage.invalidate_sessions()?;

    run_services(logger, storage, config, token_config, SocketPolicy::lan(), SourcePolicy::lan())
}

fn run_services(logger: Logger, storage: Arc<Storage>, config: Config, token_config: api::AuthTokenConfig,
    socket_policy: SocketPolicy, source_policy: SourcePolicy) -> color_eyre::Result<()> {
    let (service_exit_sender, service_exit_receiver) = mpsc::channel();
    let mut threads = Vec::new();
    for (name, svc) in config.quazal.into_services()? {
        let logger = logger.new(o!("service" => name.clone()));
        info!(logger, "Loaded service {}", name);
        let storage = Arc::clone(&storage);
        let socket_policy = socket_policy.clone();
        let handle = match svc {
            quazal::Service::Authentication(ctx) => spawn_required_service(name, service_exit_sender.clone(), move || {
                start_server(&logger, &ctx, &storage, false, &socket_policy, source_policy).map_err(|error| format!("authentication server failed: {error}"))
            }),
            quazal::Service::Secure(ctx) => spawn_required_service(name, service_exit_sender.clone(), move || {
                start_server(&logger, &ctx, &storage, true, &socket_policy, source_policy).map_err(|error| format!("secure server failed: {error}"))
            }),
            quazal::Service::Config(cfg) => spawn_required_service(name, service_exit_sender.clone(), move || {
                if cfg.listen.port() != 80 {
                    warn!(
                        logger,
                        "Unexpected port {} used for the config server. Clients are expecting port 80. Adjust in the service config or make sure to redirect traffic accordingly",
                        cfg.listen.port()
                    );
                }
                simple_http::serve(&logger, cfg.listen, &cfg.content(), &socket_policy, source_policy).map_err(|error| format!("config server failed: {error}"))
            }),
            quazal::Service::Content(srv) => spawn_required_service(name, service_exit_sender.clone(), move || {
                simple_http::serve_many(&logger, srv.listen, &srv.files, &socket_policy, source_policy).map_err(|error| format!("content server failed: {error}"))
            }),
        };
        threads.push(handle?);
    }

    threads.push(spawn_required_service(String::from("api"), service_exit_sender.clone(), move || {
        let logger = logger.new(o!("service" => "api"));
        let runtime = tokio::runtime::Runtime::new().map_err(|error| format!("API runtime creation failed: {error}"))?;
        runtime
            .block_on(api::start_server(logger, storage, config.api_server, Arc::new(config.debug), false, token_config, socket_policy, source_policy))
            .map_err(|error| format!("API server failed: {error:?}"))
    })?);

    drop(service_exit_sender);
    let exit = service_exit_receiver
        .recv()
        .map_err(|error| eyre::eyre!("required service supervision channel failed: {error}"))?;
    Err(eyre::eyre!("required service {} exited: {}", exit.name, exit.reason))
}

/// This narrow path trusts an explicitly inherited local-owner capability, not
/// a CLI flag, environment value or network name. The production supervisor's
/// admission provider remains closed pending actual runtime/profile proof.
#[cfg(target_os = "linux")]
fn run_owned_public(args: &Args) -> Result<(), &'static str> {
    use std::os::fd::AsRawFd;
    const INVALID: &str = "PUBLIC_ACTIVATION_ARGUMENT_INVALID";
    if args.deployment_profile != DeploymentProfile::Public || args.init_lan.is_some()
        || args.auth_key_file.is_some() || args.config_path.is_some() || args.data_dir.is_some()
        || args.check_public_deployment.is_some() || args.smoke_ports || args.launcher {
        return Err(INVALID);
    }
    let fd = args.owned_public_lease_fd.ok_or(INVALID)?;
    let path = args.public_deployment_config.as_ref().ok_or(INVALID)?;
    let lease = public_lease::UnadmittedLease::inherited(fd).map_err(|_| "PUBLIC_OWNER_LEASE_INVALID")?;
    let executable = std::env::current_exe().map_err(|_| "PUBLIC_PREFLIGHT_PACKAGE_INVALID")?;
    let plan = public_preflight::prepare(path, &executable)?;
    let (binding, socket_policy, source_policy) = plan.activation_binding()?;
    let interface = plan.interface_name();
    let boundary = binding.clone();
    let active = lease.admit(binding).map_err(|_| "PUBLIC_OWNER_LEASE_INVALID")?;
    // Recheck after the bounded handshake and on every renewal. Each socket
    // independently enforces device/index/address at bind; the owner supervises
    // actual encrypted runtime health and revokes this lease when it changes.
    let boundary_current = move || public_lease::boundary_current(&boundary, &interface);
    if !boundary_current() { return Err("PUBLIC_ACTIVATION_BOUNDARY_INVALID"); }
    let reporter = active.activate(boundary_current).map_err(|_| "PUBLIC_OWNER_LEASE_INVALID")?;
    // The raw private document (including this default-false option) is bound
    // into ADMIT. Keep the policy local to this admitted server-process lifetime.
    let _observer_policy = observer_policy::owned_public(plan.observer_allowed());
    lan_runtime::register_public_readiness(move |bit| reporter.bound(bit)).map_err(|_| "PUBLIC_ACTIVATION_READINESS_INVALID")?;
    if unsafe { libc::fchdir(plan.data.as_raw_fd()) } != 0 { return Err("PUBLIC_ACTIVATION_DATA_INVALID"); }
    let _instance_lock = instance_lock::acquire(Path::new(".")).map_err(|_| "PUBLIC_ACTIVATION_DATA_LOCK_INVALID")?;
    // All package/key/private data/interface/owner gates and the watchdog precede
    // the first persistent mutation. No LAN stdin/environment switch runs.
    let logger = Logger::root(slog::Discard, o!());
    let storage = Arc::new(Storage::init(logger.clone(), plan.admitted_client).map_err(|_| "PUBLIC_ACTIVATION_STORAGE_INVALID")?);
    lan_runtime::configure_ports(&plan.config);
    ensure_data_dir().map_err(|_| "PUBLIC_ACTIVATION_DATA_INVALID")?;
    storage.invalidate_sessions().map_err(|_| "PUBLIC_ACTIVATION_STORAGE_INVALID")?;
    run_services(logger, storage, plan.config, plan.auth, socket_policy, source_policy)
        .map_err(|_| "PUBLIC_REQUIRED_SERVICE_EXITED")
}

#[cfg(test)]
mod user_packet_tests {
    use super::*;

    #[test]
    fn deployment_cli_defaults_to_lan_without_reinterpreting_transport_names() {
        use argh::FromArgs;
        let args = Args::from_args(&["FifthFreedom.Server"], &[]).unwrap();
        assert_eq!(args.deployment_profile, DeploymentProfile::Lan);
        for invalid in ["fast", "compatible", "host", "join", "unknown"] {
            assert!(Args::from_args(&["FifthFreedom.Server"], &["--deployment-profile", invalid]).is_err());
        }
    }

    #[test]
    fn explicit_public_cannot_enable_lan_initialization() {
        use argh::FromArgs;
        let args = Args::from_args(&["FifthFreedom.Server"], &["--deployment-profile", "public", "--init-lan", "127.0.0.1"]).unwrap();
        assert_eq!(args.init_lan, Some(std::net::Ipv4Addr::LOCALHOST));
        assert_eq!(args.deployment_profile.startup_plan(), Err(crate::deployment_profile::ProfileError::PublicNotIntegrated));
    }

    #[test]
    fn untrusted_virtual_ports_are_dropped_without_panicking() {
        let logger = Logger::root(slog::Discard, slog::o!());
        let socket = UdpSocket::bind("127.0.0.1:0").unwrap();
        let client: SocketAddr = "127.0.0.1:9".parse().unwrap();

        for (source_port, destination_port) in [(0, 1), (1, 0), (15, 15)] {
            let mut packet = QPacket::default();
            packet.source.port = source_port;
            packet.destination.port = destination_port;
            handle_user_packet(&logger, packet, client, &socket);
        }
    }

    #[test]
    fn required_service_return_is_reported_to_the_main_supervisor() {
        let (sender, receiver) = mpsc::channel();
        let handle = spawn_required_service(String::from("test-service"), sender, || Ok(())).unwrap();
        handle.join().unwrap();
        assert_eq!(
            receiver.recv().unwrap(),
            RequiredServiceExit {
                name: String::from("test-service"),
                reason: String::from("returned unexpectedly"),
            }
        );
    }
}

#[cfg(test)]
#[path = "remaining_auth_logging_red.rs"]
mod remaining_auth_logging_red;

#[cfg(test)]
#[path = "remaining_auth_metadata_tests.rs"]
mod remaining_auth_metadata_tests;
