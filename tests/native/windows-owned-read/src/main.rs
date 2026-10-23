// Test-only owned helper. No EasyTier, NIC, driver, route or network execution.
#[cfg(windows)]
#[tokio::main]
async fn main() {
    use ff_windows_owned_read_contract::{owned_read as wire,owned_read_windows::{Owner,Observation}};
    async fn run()->std::io::Result<()> {
        let args:Vec<_>=std::env::args().skip(1).collect();
        if args.len()!=7 || args[0]!="--config-file" || args[2..]!=["--disable-env-parsing","--console-log-level","off","--file-log-level","off"] {return Err(wire::rejected());}
        let bytes=std::fs::read(&args[1])?;
        if bytes.len()>4096 {return Err(wire::rejected());}
        let cfg:serde_json::Value=serde_json::from_slice(&bytes).map_err(|_|wire::rejected())?;
        let mode=cfg["mode"].as_str().ok_or_else(wire::rejected)?;
        if matches!(mode,"wrong-birth"|"wrong-image") {
            let raw=std::env::var(wire::ENVIRONMENT).map_err(|_|wire::rejected())?;
            let mut d:serde_json::Value=serde_json::from_str(&raw).map_err(|_|wire::rejected())?;
            if mode=="wrong-birth" {d["parent_birth"]=serde_json::json!(d["parent_birth"].as_u64().ok_or_else(wire::rejected)?+1);}
            else {d["parent_sha256"]=serde_json::json!("f".repeat(64));}
            // Deliberately corrupt test-only expected input before the exact
            // production anchor. Never available in the deployed module.
            std::env::set_var(wire::ENVIRONMENT,d.to_string());
        }
        let mut owner=Owner::prepare("12345678-1234-4321-9234-123456789abc").await?.ok_or_else(wire::rejected)?;
        owner.serve(|source|async move{
            if source.to_string()=="10.66.0.11" {return Err(wire::rejected());}
            Ok(Observation{name:"synthetic-rust-nic".into(),guid:1,luid:2,index:7})
        }).await
    }
    // Fixed code only; never display descriptor/path/exception contents.
    let result=tokio::time::timeout(std::time::Duration::from_secs(30),run()).await;
    if !matches!(result,Ok(Ok(()))) {std::process::exit(1);}
}
#[cfg(not(windows))]
fn main(){std::process::exit(2);}
