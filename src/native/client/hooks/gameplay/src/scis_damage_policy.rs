//! Absolute SC-IS unarmored targets, before the existing victim armor pass.
//! Do not mutate shared archetypes, health, bone tables, or the damage event.
pub fn eligible(
    logic: usize, victim: usize, distance: f32, bone: u32, attenuation: f32,
    read: impl Fn(usize, usize) -> Option<u32>, named: impl FnOnce(u32) -> bool,
) -> Option<f32> {
    if !distance.is_finite() || distance < 0.0 || !attenuation.is_finite()
        || attenuation <= 0.0 || !(1..=17).contains(&bone) { return None; }
    let world = read(0x339e5a0, 0)? as usize;
    let level = read(world, 0x144)?;
    let weapon = read(logic, 0x28)? as usize;
    let props = read(logic, 0x2c)? as usize;
    let owner = read(weapon, 0x8c)? as usize;
    if level < 0x10000 || read(world, 0x330)? & 0x10 == 0
        || read(victim, 0)? != 0x02ad3234 || read(victim, 0x144)? != level
        || read(owner, 0)? != 0x02ad2abc || read(owner, 0x144)? != level
        || read(weapon, 0)? != 0x02a85b7c || read(weapon, 0x144)? != level
        || read(weapon, 0x748)? as usize != logic || read(weapon, 0x744)? as usize != props
        || read(props, 0)? != 0x02a176c4 || (read(props, 0x38)? >> 8) & 0xff != 11
        || read(logic, 0x150)? != 40 || !named(read(weapon, 0x740)?) { return None; }
    // C70B10 converts the actual victim's integer bone percentage to a factor.
    // Neutralize only that factor here: D30D80 still applies armor afterwards.
    let percent = read(victim, 0x310 + bone as usize * 4)?;
    (1..=10000).contains(&percent).then_some(percent as f32 / 100.0)
}

// DD81B0 uses ADV fields C0/C4 in MP, not the campaign B8/BC fields.
// Retail Rifle_Ammo_AP has B8=1.15 but C0=C4=0, so its native MP damage
// contribution is neutral. Bind the actual equipped/enabled AP identity;
// do not infer AP from damage=44/46 or modify shared campaign properties.
pub fn active_ap(
    logic: usize, read: impl Fn(usize, usize) -> Option<u32>,
    named: impl Fn(u32) -> bool,
) -> Option<bool> {
    let weapon=read(logic,0x28)? as usize;
    let count=read(weapon,0x768)? as usize;
    if count==0 {return Some(false);}
    let capacity=read(weapon,0x76c)? as usize;
    if count>32 || capacity<count || capacity>256 {return None;}
    let list=read(weapon,0x764)? as usize;
    if list<0x10000 {return None;}
    let level=read(weapon,0x144)?;
    let mut found=false;
    for i in 0..count {
        let attachment=read(list,i*4)? as usize;
        if attachment==0 {continue;}
        if read(attachment,0x144)?!=level {return None;}
        if read(attachment,0x708)?&1==0 {continue;}
        let props=read(attachment,0x700)? as usize;
        if read(props,0)?!=0x02a1772c {return None;}
        if !named(read(props,0x28)?) {continue;}
        if found {return None;}
        // Reject changed/unknown tuning instead of applying two AP bonuses.
        let sp=f32::from_bits(read(props,0xb8)?);
        if !sp.is_finite() || (sp-1.15).abs()>0.000001
            || read(props,0xbc)?!=0 || read(props,0xc0)?!=0 || read(props,0xc4)?!=0 {return None;}
        found=true;
    }
    Some(found)
}

pub fn pre_armor(
    distance: f32, bone: u32, bone_factor: f32, attachment_damage: i32,
    attenuation: f32, far_relative: f32, piercing: bool,
) -> Option<f32> {
    if !distance.is_finite() || distance<0.0 || !attenuation.is_finite() || attenuation<=0.0
        || attachment_damage <= 0 || !bone_factor.is_finite() || bone_factor <= 0.0
        || !far_relative.is_finite() || !(0.0..=1.0).contains(&far_relative) { return None; }
    // AP extends the curve by 5m. Negative effective distance is intentional:
    // close-range AP still beats extra health instead of saturating at100 HP.
    // Beyond50m the caller supplies the native relative far-range attenuation.
    let m=distance.min(5000.0)/100.0-if piercing {5.0} else {0.0};
    let t=((m-30.0)/20.0).clamp(0.0,1.0);
    // Retail DD7270's 17-entry table: 2/3/4 torso, 5 head, the others limbs.
    let desired = match bone {
        5 => 160.0-40.0*t,
        2..=4 if m<=30.0 => 130.0-2.0*m,
        2..=4 => 70.0-(m-30.0)*(2.0/3.0),
        1 | 6..=17 if m<=5.0 => 100.0-4.0*m,
        1 | 6..=17 => 85.0-m,
        _ => return None,
    } * (attachment_damage as f32/40.0) * attenuation * far_relative;
    if !desired.is_finite() || desired<0.0 {return None;}
    // E127C9 truncates to integer before the victim's bone factor is applied.
    // Round up to preserve an exact lethal boundary, but never round across
    // the next advertised spy health tier (80 + 10 per armor-health item).
    // No target HP reads, health-scaled execution or armor cancellation.
    let mut value=(desired/bone_factor).ceil();
    for hp in [80.0f32,90.0,100.0,110.0] {
        if desired<hp && value*bone_factor>=hp {
            value=value.min((f32::from_bits(hp.to_bits()-1)/bone_factor).floor());
        }
    }
    (value.is_finite() && value>=0.0).then_some(value)
}

#[cfg(test)] mod tests {
    use super::*;
    use std::collections::HashMap;
    fn fixture() -> HashMap<(usize,usize),u32> { HashMap::from([
        ((0x339e5a0,0),0x10000),((0x10000,0x144),0x50000),((0x10000,0x330),0x10),
        ((0x30000,0x28),0x20000),((0x30000,0x2c),0x40000),((0x30000,0x150),40),
        ((0x20000,0),0x02a85b7c),((0x20000,0x8c),0x60000),((0x20000,0x144),0x50000),
        ((0x20000,0x748),0x30000),((0x20000,0x744),0x40000),((0x20000,0x740),123),
        ((0x40000,0),0x02a176c4),((0x40000,0x38),0x0b00),
        ((0x60000,0),0x02ad2abc),((0x60000,0x144),0x50000),
        ((0x70000,0),0x02ad3234),((0x70000,0x144),0x50000),((0x70000,0x31c),100),
    ]) }
    #[test] fn damage_identity_missing_or_stale_is_native() {
        let f=fixture(); let check=|f:&HashMap<_,_>| eligible(0x30000,0x70000,3000.0,3,1.0,|p,o|f.get(&(p,o)).copied(),|n|n==123);
        assert_eq!(check(&f),Some(1.0));
        for key in f.keys() { let mut bad=f.clone();bad.remove(key);assert_eq!(check(&bad),None,"missing {key:?}"); }
        for (key,value) in [((0x10000,0x330),0),((0x20000,0x740),124),((0x20000,0x748),0x30004),((0x70000,0),0x02ad2abc),((0x70000,0x31c),0),((0x30000,0x150),41)] {
            let mut bad=f.clone();bad.insert(key,value);assert_eq!(check(&bad),None);
        }
    }
    #[test] fn damage_target_curve_and_native_modifiers() {
        for (d,b,ap,expected) in [(2500.0,3,false,80.0),(3000.0,3,true,80.0),(5000.0,3,true,60.0),
            (3000.0,1,true,60.0),(5000.0,1,true,40.0),(5000.0,5,false,120.0)] {
            assert_eq!(pre_armor(d,b,1.0,40,1.0,1.0,ap),Some(expected));
        }
        assert_eq!(pre_armor(3000.0,3,1.0,44,0.5,1.0,true),Some(44.0));
        assert_eq!(pre_armor(5000.0,5,2.0,40,1.0,1.0,false),Some(60.0));
        assert_eq!(pre_armor(3000.0,0,1.0,40,1.0,1.0,false),None);
        assert_eq!(pre_armor(3000.0,3,0.0,40,1.0,1.0,false),None);
    }
    #[test] fn exact_lethal_boundaries_survive_native_integer_conversion() {
        for ap in [false,true] {for armor in 0..=3 {
            let hp=80.0+armor as f32*10.0;
            for bone in 1..=17 {
                let threshold=if (2..=4).contains(&bone) {25.0-5.0*armor as f32}
                    else if bone==5 {continue;} else {5.0-2.5*armor as f32}
                    +if ap {5.0} else {0.0};
                for factor in [0.37,0.75,1.0,1.5,2.0] {
                    for distance in [threshold*100.0,threshold*100.0+1.0] {
                        if distance<0.0 {continue;}
                        let out=pre_armor(distance,bone,factor,40,1.0,1.0,ap).unwrap().trunc()*factor;
                        assert_eq!(out>=hp,distance<=threshold*100.0,"ap{ap} armor{armor} bone{bone} f{factor} d{distance} out{out}");
                    }
                }
            }
        }}
    }
    #[test] fn active_ap_requires_real_enabled_identity_and_pinned_tuning() {
        let mut f=HashMap::from([((1,0x28),2),((2,0x764),0x10000),((2,0x768),1),((2,0x76c),1),((2,0x144),3),
            ((0x10000,0),4),((4,0x144),3),((4,0x708),1),((4,0x700),5),
            ((5,0),0x02a1772c),((5,0x28),77),((5,0xb8),1.15f32.to_bits()),((5,0xbc),0),((5,0xc0),0),((5,0xc4),0)]);
        let check=|f:&HashMap<_,_>| active_ap(1,|p,o|f.get(&(p,o)).copied(),|n|n==77);
        assert_eq!(check(&f),Some(true));let base=f.clone();
        for key in base.keys() {f=base.clone();f.remove(key);assert_eq!(check(&f),None,"missing {key:?}");}
        for (key,value,expected) in [((2,0x768),0,Some(false)),((4,0x708),0,Some(false)),((5,0x28),78,Some(false)),
            ((5,0xc0),1.1f32.to_bits(),None),((5,0xb8),f32::NAN.to_bits(),None),((4,0x144),7,None),
            ((2,0x768),33,None),((2,0x76c),0,None),((5,0),0,None),((5,0xc4),1,None)] {
            f=base.clone();f.insert(key,value);assert_eq!(check(&f),expected);
        }
    }
}
