
use std::sync::{OnceLock,atomic::{AtomicU8,Ordering}};
use std::net::Ipv4Addr;
use crate::config::Config;
static READY: AtomicU8 = AtomicU8::new(0);
static PORTS: OnceLock<[u16;4]> = OnceLock::new();
#[cfg(target_os = "linux")]
static PUBLIC_READY: OnceLock<Box<dyn Fn(u8) + Send + Sync>> = OnceLock::new();
#[cfg(target_os = "linux")]
static API_READY: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
#[cfg(target_os = "linux")]
pub(crate) fn register_public_readiness(callback: impl Fn(u8) + Send + Sync + 'static) -> Result<(), ()> {
    if READY.load(Ordering::SeqCst) != 0 || API_READY.load(Ordering::SeqCst) { return Err(()); }
    PUBLIC_READY.set(Box::new(callback)).map_err(|_| ())
}
pub fn mark_ready(bit:u8) {
    READY.fetch_or(bit,Ordering::SeqCst);
    #[cfg(target_os = "linux")]
    if let Some(callback) = PUBLIC_READY.get() { callback(bit); }
}
pub(crate) fn mark_api_ready() {
    #[cfg(target_os = "linux")]
    {
        API_READY.store(true, Ordering::SeqCst);
        if let Some(callback) = PUBLIC_READY.get() { callback(16); }
    }
}
pub fn is_ready()->bool {
    let ready = READY.load(Ordering::SeqCst)==15;
    #[cfg(target_os = "linux")]
    if PUBLIC_READY.get().is_some() { return ready && API_READY.load(Ordering::SeqCst); }
    ready
}
pub fn ports()->[u16;4] { *PORTS.get().unwrap_or(&[80,8000,21126,21127]) }
pub fn configure_ports(c:&Config) {
    let mut ports=[80,8000,21126,21127];
    for svc in c.quazal.service.values() {
        match svc {
            quazal::Service::Config(s)=>ports[0]=s.listen.port(),
            quazal::Service::Content(s)=>ports[1]=s.listen.port(),
            quazal::Service::Authentication(s)=>ports[2]=s.listen.port(),
            quazal::Service::Secure(s)=>ports[3]=s.listen.port(),
        }
    }
    let _=PORTS.set(ports);
}
pub fn initialize(ip:Ipv4Addr,smoke:bool)->eyre::Result<()> {
    if ip.octets()[0]==0 || ip.octets()[0]>=224 { return Err(eyre::eyre!("A unicast local IPv4 is required")); }
    if std::path::Path::new("service.toml").exists() { return Err(eyre::eyre!("Refusing to overwrite existing server configuration")); }
    let mut value=toml::Value::try_from(Config::default())?;
    let (config,content,auth,secure,api)=if smoke {(18080,18000,22126,22127,15051)}else{(80,8000,21126,21127,50051)};
    value["api_server"]=toml::Value::String(format!("{ip}:{api}"));
    let services=value["service"].as_table_mut().ok_or_else(||eyre::eyre!("Missing service table"))?;
    for (name,port) in [("onlineconfig",config),("content",content),("sc_bl_auth",auth),("sc_bl_secure",secure)] {
        services[name]["listen"]=toml::Value::String(format!("{ip}:{port}"));
    }
    let online=services.get_mut("onlineconfig").unwrap();
    online["content"]=toml::Value::String(format!("[{{\"Name\":\"SandboxUrl\",\"Values\":[\"prudp:/address={ip};port={auth}\"]}},{{\"Name\":\"SandboxUrlWS\",\"Values\":[\"{ip}:{auth}\"]}}]"));
    services.get_mut("sc_bl_auth").unwrap()["secure_server_addr"]=toml::Value::String(format!("{ip}:{secure}"));
    services.get_mut("sc_bl_secure").unwrap()["settings"]["storage_host"]=toml::Value::String(format!("{ip}:{content}"));
    // Content paths are relative to this isolated working directory, not an old install.
    let files=services.get_mut("content").unwrap()["files"].as_table_mut().unwrap();
    for (_, file) in files.iter_mut() { *file=toml::Value::String("data/mp_balancing.ini".into()); }
    let c:Config=value.try_into()?;
    c.save_to_file("service.toml")?;
    std::fs::create_dir_all("data")?;
    std::fs::write("data/mp_balancing.ini",crate::DEFAULT_MP_DATA)?;
    Ok(())
}
