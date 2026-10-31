#[path="../../../../docs/dependency-patches/easytier-2.6.4-windows-owned-read/source/owned_read.rs"]
pub mod owned_read;

#[cfg(windows)]
#[path="../../../../docs/dependency-patches/easytier-2.6.4-windows-owned-read/source/owned_read_windows.rs"]
pub mod owned_read_windows;

#[cfg(test)]
mod tests {
    use super::owned_read as wire;
    use serde_json::json;
    const ID: &str="12345678-1234-4321-9234-123456789abc";
    fn descriptor()->String {json!({"schema":1,"pipe":format!("ff-core-observe-{}","1".repeat(32)),"attempt":"2".repeat(32),"instance":ID,"parent_pid":12,"parent_birth":34,"parent_path":r"C:\owned\owner.exe","parent_sha256":"3".repeat(64)}).to_string()}
    fn request()->serde_json::Value {json!({"schema":1,"kind":"read","attempt":"2".repeat(32),"nonce":"4".repeat(64),"instance":ID,"request":1,"source":"10.66.0.10"})}
    #[test] fn explicit_descriptor_identity_and_bounds() {
        assert!(wire::Descriptor::parse(&descriptor(),ID).is_ok());
        for (field,value) in [("schema",json!(true)),("parent_pid",json!(0)),("parent_birth",json!(0)),("parent_sha256",json!("0".repeat(64))),("pipe",json!("other")),("attempt",json!("A".repeat(32))),("extra",json!(1))] {
            let mut x:serde_json::Value=serde_json::from_str(&descriptor()).unwrap();x[field]=value;assert!(wire::Descriptor::parse(&x.to_string(),ID).is_err());
        }
        assert!(wire::Descriptor::parse(&descriptor(),"12345678-1234-4321-9234-123456789abd").is_err());
        assert!(wire::Descriptor::parse(&" ".repeat(4097),ID).is_err());
    }
    #[test] fn duplicate_fields_are_not_last_wins() {
        let d=descriptor().replacen("{","{\"schema\":1,",1);assert!(wire::Descriptor::parse(&d,ID).is_err());
        let r=request().to_string().replacen("{","{\"request\":1,",1);assert!(serde_json::from_str::<wire::Read>(&r).is_err());
    }
    #[test] fn source_requires_canonical_unicast_ipv4() {
        assert_eq!(wire::source("10.66.0.10").unwrap().to_string(),"10.66.0.10");
        for s in ["127.1","10.01.0.1","0.0.0.0","127.0.0.1","169.254.0.1","224.0.0.1","10.0.0.256","::1"] {assert!(wire::source(s).is_err());}
    }
    #[test] fn request_binds_attempt_instance_fresh_nonce_and_sequence() {
        let d=wire::Descriptor::parse(&descriptor(),ID).unwrap();
        let r:wire::Read=serde_json::from_value(request()).unwrap();assert!(r.validate(&d,0,"").is_ok());assert!(r.validate(&d,1,"").is_err());assert!(r.validate(&d,0,&"4".repeat(64)).is_err());assert!(r.validate(&d,u64::MAX,"").is_err());
        for (f,v) in [("attempt",json!("5".repeat(32))),("instance",json!("12345678-1234-4321-9234-123456789abd")),("request",json!(0)),("nonce",json!("0".repeat(64))),("kind",json!("ready"))] {let mut x=request();x[f]=v;let r:wire::Read=serde_json::from_value(x).unwrap();assert!(r.validate(&d,0,"").is_err());}
    }
    #[test] fn hello_and_observation_exact_wire() {
        let d=wire::Descriptor::parse(&descriptor(),ID).unwrap();let c:wire::Challenge=serde_json::from_value(json!({"schema":1,"kind":"challenge","attempt":d.attempt,"nonce":"6".repeat(64),"instance":ID})).unwrap();c.validate(&d).unwrap();let h=serde_json::to_value(wire::Hello::new(&d,&c)).unwrap();assert_eq!(h["kind"],"hello");assert_eq!(h.as_object().unwrap().len(),5);
        let r:wire::Read=serde_json::from_value(request()).unwrap();let value=serde_json::to_value(wire::Observed::new(&d,&r,"synthetic",1,2,3).unwrap()).unwrap();assert_eq!(value["luid"],"0000000000000002");assert_eq!(value["guid"],"00000000000000000000000000000001");assert!(wire::Observed::new(&d,&r,"synthetic",0,2,3).is_err());
        let u=serde_json::to_value(wire::Unavailable::new(&d,&r)).unwrap();assert_eq!(u["code"],"NIC_UNAVAILABLE");assert!(!u.as_object().unwrap().contains_key("name"));
    }
    #[tokio::test] async fn real_duplex_frame_roundtrip() {
        let (mut send,mut receive)=tokio::io::duplex(4096);let value=request();wire::write(&mut send,&value).await.unwrap();let parsed:wire::Read=wire::read(&mut receive).await.unwrap();assert_eq!(parsed.source,"10.66.0.10");
    }
    #[tokio::test] async fn oversized_truncated_unknown_and_invalid_utf8_frames_reject() {
        use tokio::io::AsyncWriteExt;
        for body in [vec![1,8,0,0],vec![0,0,0,0],vec![5,0,0,0,b'{'],vec![1,0,0,0,255],{let mut v=request();v["unexpected"]=json!(true);let b=v.to_string().into_bytes();let mut x=(b.len() as u32).to_le_bytes().to_vec();x.extend(b);x}] {
            let (mut send,mut receive)=tokio::io::duplex(4096);send.write_all(&body).await.unwrap();drop(send);assert!(wire::read::<wire::Read>(&mut receive).await.is_err());
        }
    }
    #[tokio::test(start_paused=true)] async fn cancellation_drops_real_pending_read_without_detached_worker() {
        use tokio::io::AsyncWriteExt;
        let (mut send,mut receive)=tokio::io::duplex(4096);send.write_all(&[20,0,0,0,b'{']).await.unwrap();let task=tokio::spawn(async move{wire::read::<wire::Read>(&mut receive).await});tokio::task::yield_now().await;assert!(!task.is_finished());task.abort();assert!(matches!(task.await,Err(error) if error.is_cancelled()));assert!(send.write_all(b"late").await.is_err());
    }
}
