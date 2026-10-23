//! Synthetic socket-policy probe for an owned, isolated Linux namespace test.
//! This is not a native game server, overlay launcher or deployment entry point.
#[cfg(target_os = "linux")]
fn run() -> Result<(), &'static str> {
    use quazal::socket_policy::SocketPolicy;
    use std::io::{BufRead, Read, Write};
    use std::net::{IpAddr, Ipv4Addr, SocketAddr, TcpListener, UdpSocket};
    use std::sync::{Arc, atomic::{AtomicBool, Ordering}, mpsc};
    use std::time::Duration;
    const PAYLOAD: &[u8] = b"ff-socket-probe-v1";
    let args: Vec<_> = std::env::args().collect();
    if args.len() != 5 || args[1] != "serve" {
        return Err("PROBE_ARGUMENTS_INVALID");
    }
    let address: Ipv4Addr = args[3].parse().map_err(|_| "PROBE_ADDRESS_INVALID")?;
    let policy = match args[2].as_str() {
        "ip-only" => SocketPolicy::lan(),
        "interface-bound" => SocketPolicy::interface_bound(address, &args[4]).map_err(|_| "PROBE_POLICY_FAILED")?,
        _ => return Err("PROBE_MODE_INVALID"),
    };
    let tcp = policy.bind_tcp(SocketAddr::new(IpAddr::V4(address), 0)).map_err(|_| "PROBE_TCP_BIND_FAILED")?;
    let udp = policy.bind_udp(SocketAddr::new(IpAddr::V4(address), 0)).map_err(|_| "PROBE_UDP_BIND_FAILED")?;
    let ephemeral = policy.bind_ephemeral_udp().map_err(|_| "PROBE_EPHEMERAL_BIND_FAILED")?;
    let ports = (tcp.local_addr().map_err(|_| "PROBE_ADDRESS_FAILED")?.port(),
        udp.local_addr().map_err(|_| "PROBE_ADDRESS_FAILED")?.port(),
        ephemeral.local_addr().map_err(|_| "PROBE_ADDRESS_FAILED")?.port());
    tcp.set_nonblocking(true).map_err(|_| "PROBE_SOCKET_CONFIG_FAILED")?;
    for socket in [&udp, &ephemeral] {
        socket.set_read_timeout(Some(Duration::from_millis(100))).map_err(|_| "PROBE_SOCKET_CONFIG_FAILED")?;
        socket.set_write_timeout(Some(Duration::from_millis(100))).map_err(|_| "PROBE_SOCKET_CONFIG_FAILED")?;
    }
    let stop = Arc::new(AtomicBool::new(false));
    fn serve_tcp(socket: TcpListener, stop: &AtomicBool) -> Result<(), &'static str> {
        while !stop.load(Ordering::Acquire) {
            match socket.accept() {
                Ok((mut peer, _)) => {
                    peer.set_read_timeout(Some(Duration::from_millis(250))).map_err(|_| "PROBE_SOCKET_CONFIG_FAILED")?;
                    peer.set_write_timeout(Some(Duration::from_millis(250))).map_err(|_| "PROBE_SOCKET_CONFIG_FAILED")?;
                    let mut payload = [0u8; PAYLOAD.len()];
                    if peer.read_exact(&mut payload).is_ok() && payload == PAYLOAD {
                        let _ = peer.write_all(PAYLOAD);
                    }
                }
                Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => std::thread::sleep(Duration::from_millis(10)),
                Err(_) => return Err("PROBE_TCP_IO_FAILED"),
            }
        }
        Ok(())
    }
    fn serve_udp(socket: UdpSocket, stop: &AtomicBool) -> Result<(), &'static str> {
        while !stop.load(Ordering::Acquire) {
            let mut payload = [0u8; 128];
            match socket.recv_from(&mut payload) {
                Ok((count, peer)) if &payload[..count] == PAYLOAD => {
                    let _ = socket.send_to(PAYLOAD, peer);
                }
                Ok(_) => (),
                Err(error) if matches!(error.kind(), std::io::ErrorKind::TimedOut | std::io::ErrorKind::WouldBlock) => (),
                Err(_) => return Err("PROBE_UDP_IO_FAILED"),
            }
        }
        Ok(())
    }
    let tcp_stop = Arc::clone(&stop);
    let udp_stop = Arc::clone(&stop);
    let ephemeral_stop = Arc::clone(&stop);
    let workers = [std::thread::spawn(move || serve_tcp(tcp, &tcp_stop)),
        std::thread::spawn(move || serve_udp(udp, &udp_stop)),
        std::thread::spawn(move || serve_udp(ephemeral, &ephemeral_stop))];
    let (sender, receiver) = mpsc::channel();
    std::thread::spawn(move || {
        let mut line = String::new();
        let result = std::io::stdin().lock().take(16).read_line(&mut line);
        let _ = sender.send(result.is_ok() && line.trim_end() == "STOP");
    });
    println!("{{\"schema\":1,\"event\":\"READY\",\"mode\":\"{}\",\"tcp_port\":{},\"udp_port\":{},\"udp_ephemeral_port\":{}}}", args[2], ports.0, ports.1, ports.2);
    std::io::stdout().flush().map_err(|_| "PROBE_OUTPUT_FAILED")?;
    let stopped = receiver.recv_timeout(Duration::from_secs(120)).unwrap_or(false);
    stop.store(true, Ordering::Release);
    for worker in workers {
        worker.join().map_err(|_| "PROBE_WORKER_PANIC")??;
    }
    if !stopped {
        return Err("PROBE_STOP_NOT_CONFIRMED");
    }
    Ok(())
}

fn main() {
    #[cfg(target_os = "linux")]
    if let Err(code) = run() {
        eprintln!("{code}");
        std::process::exit(1);
    }
    #[cfg(not(target_os = "linux"))]
    {
        eprintln!("PROBE_REQUIRES_LINUX");
        std::process::exit(1);
    }
}
