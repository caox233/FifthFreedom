//! A process-local request is not admission. Only the matching native-login
//! response may authorize the joining client, after host policy and credentials.
pub const REQUEST_HEADER: &str = "x-scbl-observer";
pub const ADMITTED_RUN_HEADER: &str = "x-scbl-observer-run";
pub const REQUEST_ENV: &str = "SCBL_OBSERVER";

pub fn accepted(requested: bool, instance: &str, admitted: Option<&str>) -> bool {
    !requested || (crate::gameplay_contract::valid_game_instance(instance) && admitted == Some(instance))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn observer_admission_requires_the_exact_requested_game_run() {
        let run="0123456789abcdef0123456789abcdef";
        assert!(accepted(true,run,Some(run)));
        for ack in [None,Some("1"),Some(""),Some("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] {
            assert!(!accepted(true,run,ack));
        }
        assert!(!accepted(true,"",Some("")));
        assert!(accepted(false,run,None));
    }
}
