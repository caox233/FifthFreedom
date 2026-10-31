//! Deployment selection is separate from LAN Host/Join and public transports.
//!
//! Resolve this policy before package access, data creation, logging, database
//! migrations or listeners. Public must not fall back to the LAN runtime while
//! the authenticated control-plane and package adapters are still unimplemented.
use std::fmt;
use std::path::{Path, PathBuf};
use std::str::FromStr;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DeploymentProfile {
    Lan,
    Public,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ProfileError {
    UnknownProfile,
    PublicNotIntegrated,
    MissingExecutableDirectory,
}

impl fmt::Display for ProfileError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::UnknownProfile => "deployment profile must be 'lan' or 'public'; transport and host role are separate settings",
            Self::PublicNotIntegrated => "PUBLIC_PROFILE_NOT_READY: public authentication, package admission and lifecycle adapters are not integrated; refusing to start the LAN runtime as a public server",
            Self::MissingExecutableDirectory => "the server executable has no parent directory",
        })
    }
}

impl std::error::Error for ProfileError {}

impl FromStr for DeploymentProfile {
    type Err = ProfileError;

    fn from_str(value: &str) -> Result<Self, Self::Err> {
        match value {
            "lan" => Ok(Self::Lan),
            "public" => Ok(Self::Public),
            _ => Err(ProfileError::UnknownProfile),
        }
    }
}

/// A validated policy, not a claim that sockets or package admission are ready.
/// It cannot be constructed by parsing an untrusted deployment name directly.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct StartupPlan {
    default_data_directory: &'static str,
}

impl DeploymentProfile {
    pub fn startup_plan(self) -> Result<StartupPlan, ProfileError> {
        match self {
            Self::Lan => Ok(StartupPlan { default_data_directory: "lan-server-data" }),
            Self::Public => Err(ProfileError::PublicNotIntegrated),
        }
    }
}

impl StartupPlan {
    /// Compute the existing LAN path without creating or reading any files.
    pub fn data_root(self, explicit: Option<PathBuf>, executable: &Path) -> Result<PathBuf, ProfileError> {
        if let Some(path) = explicit {
            return Ok(path);
        }
        let parent = executable.parent().ok_or(ProfileError::MissingExecutableDirectory)?;
        Ok(parent.join(self.default_data_directory))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn deployment_names_are_not_roles_or_transports() {
        assert_eq!("lan".parse(), Ok(DeploymentProfile::Lan));
        assert_eq!("public".parse(), Ok(DeploymentProfile::Public));
        for value in ["", "LAN", "Public", "host", "join", "fast", "compatible", " lan", "public\n", "other"] {
            assert_eq!(value.parse::<DeploymentProfile>(), Err(ProfileError::UnknownProfile), "{value:?}");
        }
    }

    #[test]
    fn public_has_no_startup_plan_and_cannot_fall_back_to_lan() {
        assert_eq!(DeploymentProfile::Public.startup_plan(), Err(ProfileError::PublicNotIntegrated));
        assert!(ProfileError::PublicNotIntegrated.to_string().starts_with("PUBLIC_PROFILE_NOT_READY:"));
    }

    #[test]
    fn lan_preserves_the_executable_relative_data_directory() {
        let executable = Path::new("package").join("FifthFreedom.Server.exe");
        let plan = DeploymentProfile::Lan.startup_plan().unwrap();
        assert_eq!(plan.data_root(None, &executable).unwrap(), Path::new("package").join("lan-server-data"));
    }

    #[test]
    fn explicit_data_directory_remains_owned_by_the_caller() {
        let plan = DeploymentProfile::Lan.startup_plan().unwrap();
        for explicit in [PathBuf::from("custom-data"), Path::new("nested").join("server-data")] {
            assert_eq!(plan.data_root(Some(explicit.clone()), Path::new("server.exe")).unwrap(), explicit);
        }
    }

    #[test]
    fn missing_executable_parent_is_an_error_not_a_panic() {
        let plan = DeploymentProfile::Lan.startup_plan().unwrap();
        assert_eq!(plan.data_root(None, Path::new("")), Err(ProfileError::MissingExecutableDirectory));
    }
}
