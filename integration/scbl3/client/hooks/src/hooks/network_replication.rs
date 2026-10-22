use std::sync::atomic::AtomicBool;
use std::sync::atomic::Ordering;

use tracing::error;
use tracing::info;

use crate::addresses::Addresses;
const SUPPORTED_WORLD_RAYCAST: usize = 0x0058_9860;

const PLAYER_MOTION_HZ: f32 = 20.0;
const WEAPON_STATUS_HZ: f32 = 10.0;
const MOBILE_MOTION_HZ: f32 = 10.0;

static PLAYER_MOTION_INTERVAL: f32 = 1.0 / PLAYER_MOTION_HZ;
static WEAPON_STATUS_INTERVAL: f32 = 1.0 / WEAPON_STATUS_HZ;

const RETAIL_PLAYER_MOTION_INTERVAL_POINTER: [u8; 4] = 0x0299_3AD0u32.to_le_bytes();
const RETAIL_WEAPON_STATUS_INTERVAL_POINTER: [u8; 4] = 0x0299_9F24u32.to_le_bytes();
const PLAYER_MOTION_INTERVAL_LOAD: usize = 0x00FD_9B51;
const WEAPON_STATUS_INTERVAL_LOAD: usize = 0x00FD_9A4C;

static REPLICATION_TUNING_ENABLED: AtomicBool = AtomicBool::new(false);

fn interval_pointer(interval: &'static f32) -> Option<[u8; 4]> {
    u32::try_from(std::ptr::from_ref(interval) as usize).ok().map(u32::to_le_bytes)
}

fn player_motion_interval_pointer() -> Option<[u8; 4]> {
    interval_pointer(&PLAYER_MOTION_INTERVAL)
}

fn weapon_status_interval_pointer() -> Option<[u8; 4]> {
    interval_pointer(&WEAPON_STATUS_INTERVAL)
}

unsafe fn set_replication_intervals(enabled: bool) -> bool {
    let Some(player_motion_pointer) = player_motion_interval_pointer() else {
        error!("SCBL_NETWORK_REPLICATION enabled=false reason=player_interval_pointer_out_of_range");
        return false;
    };
    let Some(weapon_status_pointer) = weapon_status_interval_pointer() else {
        error!("SCBL_NETWORK_REPLICATION enabled=false reason=weapon_interval_pointer_out_of_range");
        return false;
    };

    let player_motion = if enabled {
        (&RETAIL_PLAYER_MOTION_INTERVAL_POINTER, &player_motion_pointer)
    } else {
        (&player_motion_pointer, &RETAIL_PLAYER_MOTION_INTERVAL_POINTER)
    };
    let weapon_status = if enabled {
        (&RETAIL_WEAPON_STATUS_INTERVAL_POINTER, &weapon_status_pointer)
    } else {
        (&weapon_status_pointer, &RETAIL_WEAPON_STATUS_INTERVAL_POINTER)
    };
    let patches = [
        (PLAYER_MOTION_INTERVAL_LOAD, player_motion.0, player_motion.1, "player_motion"),
        (WEAPON_STATUS_INTERVAL_LOAD, weapon_status.0, weapon_status.1, "weapon_status"),
    ];

    for (address, expected, _, kind) in patches {
        let operand = (address + 4) as *mut u8;
        let current = std::slice::from_raw_parts(operand, expected.len());
        if current != expected {
            error!("SCBL_NETWORK_REPLICATION enabled=false kind={kind} address={address:08X} reason=unexpected_operand current={current:02X?}");
            return false;
        }
    }

    let mut patched = 0usize;
    for (address, _expected, target, kind) in patches {
        let operand = (address + 4) as *mut u8;
        crate::writemem(operand, target);
        if std::slice::from_raw_parts(operand, target.len()) != target {
            error!("SCBL_NETWORK_REPLICATION enabled=false kind={kind} address={address:08X} reason=write_verification_failed");
            for (rollback_address, rollback_value, _, _) in patches.into_iter().take(patched + 1).rev() {
                crate::writemem((rollback_address + 4) as *mut u8, rollback_value);
            }
            return false;
        }
        patched += 1;
    }
    true
}

pub(crate) unsafe fn init(addresses: &Addresses, enabled: bool) {
    if !enabled {
        info!(
            "SCBL_NETWORK_REPLICATION enabled=false source=Official player_motion_hz={PLAYER_MOTION_HZ:.0} weapon_status_hz={WEAPON_STATUS_HZ:.0} mobile_motion_hz={MOBILE_MOTION_HZ:.0}"
        );
        return;
    }
    if addresses.func_world_raycast != Some(SUPPORTED_WORLD_RAYCAST) {
        error!(
            "SCBL_NETWORK_REPLICATION enabled=false reason=unsupported_executable world_raycast={:?}",
            addresses.func_world_raycast
        );
        return;
    }
    if !set_replication_intervals(true) {
        error!("SCBL_NETWORK_REPLICATION enabled=false reason=replication_patch_failed");
        return;
    }

    REPLICATION_TUNING_ENABLED.store(true, Ordering::Release);
    info!(
        "SCBL_NETWORK_REPLICATION enabled=true source=MOD player_motion_hz={PLAYER_MOTION_HZ:.0} player_motion_interval_ms={:.1} weapon_status_hz={WEAPON_STATUS_HZ:.0} weapon_status_interval_ms={:.1} mobile_motion_hz={MOBILE_MOTION_HZ:.0} mobile_motion_interval_ms={:.1}",
        PLAYER_MOTION_INTERVAL * 1000.0,
        WEAPON_STATUS_INTERVAL * 1000.0,
        1000.0 / MOBILE_MOTION_HZ
    );
}

pub(crate) unsafe fn deinit() {
    if REPLICATION_TUNING_ENABLED.swap(false, Ordering::AcqRel) && !set_replication_intervals(false) {
        error!("SCBL_NETWORK_REPLICATION restore=false");
    }
}

#[cfg(test)]
mod tests {
    use super::player_motion_interval_pointer;
    use super::weapon_status_interval_pointer;
    use super::MOBILE_MOTION_HZ;
    use super::PLAYER_MOTION_HZ;
    use super::PLAYER_MOTION_INTERVAL;
    use super::PLAYER_MOTION_INTERVAL_LOAD;
    use super::RETAIL_PLAYER_MOTION_INTERVAL_POINTER;
    use super::RETAIL_WEAPON_STATUS_INTERVAL_POINTER;
    use super::WEAPON_STATUS_HZ;
    use super::WEAPON_STATUS_INTERVAL;
    use super::WEAPON_STATUS_INTERVAL_LOAD;
    #[test]
    fn layered_intervals_match_the_selected_game_specific_targets() {
        assert_eq!(PLAYER_MOTION_HZ, 20.0);
        assert_eq!(PLAYER_MOTION_INTERVAL, 0.05);
        assert_eq!(WEAPON_STATUS_HZ, 10.0);
        assert_eq!(WEAPON_STATUS_INTERVAL, 0.1);
        assert_eq!(MOBILE_MOTION_HZ, 10.0);
    }

    #[test]
    fn only_player_motion_and_weapon_status_loads_are_patched() {
        assert_eq!(PLAYER_MOTION_INTERVAL_LOAD, 0x00FD_9B51);
        assert_eq!(WEAPON_STATUS_INTERVAL_LOAD, 0x00FD_9A4C);
        assert_ne!(player_motion_interval_pointer().unwrap(), RETAIL_PLAYER_MOTION_INTERVAL_POINTER);
        assert_ne!(weapon_status_interval_pointer().unwrap(), RETAIL_WEAPON_STATUS_INTERVAL_POINTER);
    }
}
