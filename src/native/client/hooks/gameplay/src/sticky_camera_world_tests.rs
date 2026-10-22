//! Regression from the authorized 2026-10-22 selected-camera read.
//! GameInfo and WorldInfo are distinct objects in the SAME world. The retail
//! controller/pawn/camera/mapping values were valid; pointer equality was not.
//! Heap/fixed globals below belong only to this isolated test process.
use super::*;
#[derive(Default)]
struct Mem(Vec<u32>);
impl Mem {
    fn new(bytes:usize)->Self { Self(vec![0;bytes/4+1]) }
    fn p(&self)->usize { self.0.as_ptr() as usize }
    fn w(&mut self,o:usize,v:u32) { self.0[o/4]=v; }
}
struct Scene { world:Mem, world_info:Mem, game_info:Mem, pawn:Mem, camera:Mem,
    pc:Mem, health:Mem, inventory:Mem, rows:Mem, row:Mem, objects:Mem,
    controllers:Mem, mapping:Mem, records:Mem }
impl Scene {
    fn new()->Self {
        let mut s=Self {world:Mem::new(0x700),world_info:Mem::new(0x400),game_info:Mem::new(0x400),
            pawn:Mem::new(0x1500),camera:Mem::new(0xa00),pc:Mem::new(0x900),health:Mem::new(0x80),
            inventory:Mem::new(0x50),rows:Mem::new(12),row:Mem::new(0x40),objects:Mem::new(8),
            controllers:Mem::new(16),mapping:Mem::new(0x40),records:Mem::new(12)};
        let w=s.world.p() as u32;let p=s.pawn.p() as u32;let c=s.camera.p() as u32;let pc=s.pc.p() as u32;
        s.world.w(0x20,120893);s.world.w(0x6d0,s.world_info.p() as u32);
        s.world_info.w(0,44851364);s.world_info.w(0x144,w);s.world_info.w(0x37c,7);
        s.world_info.w(0x36c,s.controllers.p() as u32);s.world_info.w(0x370,1);s.world_info.w(0x374,4);
        s.controllers.w(0,pc);
        s.game_info.w(0,44882284);s.game_info.w(0x144,w);s.game_info.w(0x330,82);
        s.pc.w(0,44848108);s.pc.w(0x20,535342);s.pc.w(0x4c,256);s.pc.w(0x60,393216);
        s.pc.w(0x144,w);s.pc.w(0x310,p);s.pc.w(0x520,p);s.pc.w(0x550,s.mapping.p() as u32);
        s.pawn.w(0,44905012);s.pawn.w(0x20,591545);s.pawn.w(0x4c,256);s.pawn.w(0x5c,14680128);
        s.pawn.w(0x60,0);s.pawn.w(0x78,213);s.pawn.w(0x144,w);s.pawn.w(0x938,pc);
        s.pawn.w(0x950,807420024);s.pawn.w(0x4dc,s.health.p() as u32);s.health.w(0x6c,80f32.to_bits());
        s.pawn.w(0x1410,s.inventory.p() as u32);s.inventory.w(0x30,16);
        s.inventory.w(0x38,s.rows.p() as u32);s.inventory.w(0x3c,3);s.rows.w(0,s.row.p() as u32);
        s.row.w(0x28,16);s.row.w(0x2c,s.objects.p() as u32);s.row.w(0x30,2);s.objects.w(0,c);
        s.camera.w(0,44763484);s.camera.w(0x20,535533);s.camera.w(0x4c,256);s.camera.w(0x5c,192);
        s.camera.w(0x78,213);s.camera.w(0x144,w);s.camera.w(0x49c,50273552);s.camera.w(0x68c,p);
        s.camera.w(0x858,18);s.camera.w(0x860,2);s.camera.w(0x8d4,p);
        s.mapping.w(0x28,s.records.p() as u32);s.mapping.w(0x2c,3);
        for(i,v)in[9239,9572,9814].into_iter().enumerate(){s.records.w(i*4,v);}
        s
    }
}
#[test]
fn captured_deployed_camera_uses_world_identity_not_info_object_equality() { unsafe {
    let mut s=Scene::new();
    assert_ne!(s.world_info.p(),s.game_info.p());
    assert_eq!(word(s.world_info.p(),0x144),word(s.game_info.p(),0x144));
    let expected=Owner{world:identity(s.world.p()).unwrap(),pawn:identity(s.pawn.p()).unwrap(),camera:identity(s.camera.p()).unwrap()};
    assert_eq!(device_at(s.camera.p(),s.game_info.p() as u32),Some(expected),
        "valid deployed owned camera was rejected: GameInfo != WorldInfo is normal");
    println!("CAPTURE_REPLAY deployed=2 owner=local GameInfo!=WorldInfo same_world=true accepted=true");
    s.game_info.w(0x144,0);assert!(device_at(s.camera.p(),s.game_info.p() as u32).is_none(),"foreign/stale world must remain rejected");
    s.game_info.w(0x144,s.world.p() as u32);s.world_info.w(0x37c,0);
    assert!(device_at(s.camera.p(),s.game_info.p() as u32).is_none(),"campaign remains native");
    s.world_info.w(0x37c,7);s.camera.w(0x860,1);
    assert!(device_at(s.camera.p(),s.game_info.p() as u32).is_none(),"first deployment remains native");
}}
#[test]
fn captured_deployment_gate_drives_tap_hold_double_not_native_view() { unsafe {
    let s=Scene::new();let owner=device_at(s.camera.p(),s.game_info.p() as u32);
    let mut g=Gestures::default();
    let first=g.step(owner,Edge::Press,1000);
    assert!(first.consume,"deployed camera press fell through into native camera view");
    assert_eq!(first.command,None);
    assert!(g.step(owner,Edge::Release,1030).consume);
    assert_eq!(g.step(owner,Edge::Tick,1300).command,Some(Command::Shot));
    assert_eq!(g.step(owner,Edge::Tick,1500).command,None);
    g.cancel();assert!(g.step(owner,Edge::Press,2000).consume);
    assert_eq!(g.step(owner,Edge::Tick,2250).command,Some(Command::Shot));
    assert_eq!(g.step(owner,Edge::Tick,2320).command,Some(Command::Shot));
    g.step(owner,Edge::Release,2340);assert_eq!(g.step(owner,Edge::Tick,2500).command,None);
    g.cancel();g.step(owner,Edge::Press,3000);g.step(owner,Edge::Release,3030);
    let second=g.step(owner,Edge::Press,3100);
    assert!(second.consume);assert_eq!(second.command,Some(Command::Detonate));
    println!("GATE_GESTURE_REPLAY tap=one-shot hold=repeat double=detonate native-view-fallthrough=false");
}}
