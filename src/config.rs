use std::env;
use std::error::Error;
use std::ffi::OsStr;
use std::fmt::{Display, Formatter};
use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use serde::{Deserialize, Serialize};
use windows::Win32::Foundation::{CloseHandle, HANDLE, WAIT_ABANDONED, WAIT_FAILED, WAIT_OBJECT_0};
use windows::Win32::Storage::FileSystem::{
    MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW, REPLACEFILE_WRITE_THROUGH,
    ReplaceFileW,
};
use windows::Win32::System::Threading::{
    CreateMutexW, INFINITE, ReleaseMutex, WaitForSingleObject,
};
use windows::core::{PCWSTR, w};

use crate::geometry::{AspectRatio, Rotation, SizingRequest};
use crate::limits::{
    MAX_OUTPUTS, MAX_REFRESH_MILLIHZ, MAX_VIRTUAL_DIMENSION, MILLIMETERS_PER_INCH,
    MIN_REFRESH_MILLIHZ, valid_physical_millimeters, valid_refresh_millihz,
};

const CONFIG_VERSION: u32 = 2;
const LEGACY_CONFIG_VERSION: u32 = 1;
const CONFIG_PROFILES_VERSION: u32 = 1;
pub const MAX_CONFIG_PROFILES: usize = 3;
const DISPLAY_OVERRIDES_VERSION: u32 = 1;
const CONFIG_DIRECTORY: &str = "SBMS";
const CONFIG_FILE: &str = "config-v2.json";
const CONFIG_PROFILES_FILE: &str = "config-profiles-v1.json";
const LEGACY_CONFIG_FILE: &str = "config-v1.json";
const DISPLAY_OVERRIDES_FILE: &str = "display-overrides-v1.json";
static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum ReferenceSource {
    Display(String),
    Manual,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct AppConfig {
    pub version: u32,
    pub groups: Vec<GroupConfig>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub selected_group_id: Option<u32>,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct GroupConfig {
    pub id: u32,
    pub route: GroupRouteConfig,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reference_source: Option<ReferenceSource>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sizing: Option<SizingRequest>,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum GroupRouteConfig {
    Mirror {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        target_id: Option<String>,
    },
    StreamOnly {
        screen: StreamScreenConfig,
    },
}

#[derive(Clone, Copy, Debug, Deserialize, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct StreamScreenConfig {
    pub width: u32,
    pub height: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub diagonal_inches: Option<f64>,
    pub refresh_millihz: u32,
    pub aspect_ratio: AspectRatio,
    #[serde(default)]
    pub rotation: Rotation,
}

#[derive(Clone, Debug)]
pub struct ConfigStore {
    path: PathBuf,
    legacy_path: Option<PathBuf>,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ConfigProfile {
    pub id: String,
    pub revision: u64,
    pub config: AppConfig,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ConfigProfiles {
    pub version: u32,
    pub active_profile: String,
    #[serde(default, skip_serializing_if = "is_false")]
    pub launch_intro_disabled: bool,
    pub profiles: Vec<ConfigProfile>,
}

fn is_false(value: &bool) -> bool {
    !*value
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProfileRevision {
    pub id: String,
    pub revision: u64,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ProfileSnapshot {
    pub profile: ProfileRevision,
    pub config: AppConfig,
}

#[derive(Clone, Debug)]
pub struct ConfigProfileStore {
    path: PathBuf,
    legacy_config_path: PathBuf,
}

struct CrossProcessConfigGuard(HANDLE);

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct LegacyAppConfig {
    version: u32,
    #[serde(default)]
    target_id: Option<String>,
    #[serde(default)]
    reference_source: Option<ReferenceSource>,
    #[serde(default)]
    sizing: Option<SizingRequest>,
}

#[derive(Deserialize)]
struct VersionHeader {
    version: u32,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct DisplayOverride {
    pub display_id: String,
    pub diagonal_inches: f64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub aspect_ratio: Option<AspectRatio>,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct DisplayOverrides {
    pub version: u32,
    #[serde(default)]
    pub displays: Vec<DisplayOverride>,
}

#[derive(Clone, Debug)]
pub struct DisplayOverrideStore {
    path: PathBuf,
}

#[derive(Clone, Debug, PartialEq)]
pub struct DisplayOverrideLoadOutcome {
    pub overrides: DisplayOverrides,
    pub warning: Option<String>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct LoadOutcome {
    pub config: AppConfig,
    pub warning: Option<String>,
}

#[derive(Debug)]
pub struct ConfigError(String);

impl ConfigError {
    pub fn new(message: impl Into<String>) -> Self {
        Self(message.into())
    }
}

impl Display for ConfigError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl Error for ConfigError {}

impl Default for AppConfig {
    fn default() -> Self {
        Self {
            version: CONFIG_VERSION,
            groups: vec![GroupConfig::default()],
            selected_group_id: Some(0),
        }
    }
}

impl Default for GroupConfig {
    fn default() -> Self {
        Self {
            id: 0,
            route: GroupRouteConfig::Mirror { target_id: None },
            reference_source: None,
            sizing: None,
        }
    }
}

impl AppConfig {
    pub fn group(&self, id: u32) -> Option<&GroupConfig> {
        self.groups.iter().find(|group| group.id == id)
    }

    pub fn group_mut(&mut self, id: u32) -> Option<&mut GroupConfig> {
        self.groups.iter_mut().find(|group| group.id == id)
    }

    fn validate(&self) -> Result<(), ConfigError> {
        self.validate_with_sizing(true)
    }

    fn validate_with_sizing(&self, validate_sizing: bool) -> Result<(), ConfigError> {
        if self.version != CONFIG_VERSION {
            return Err(ConfigError(format!(
                "refusing unsupported config version {}",
                self.version
            )));
        }
        if self.groups.is_empty() {
            return Err(ConfigError(
                "configuration must contain at least one mapping group".into(),
            ));
        }
        if self.groups.len() > MAX_OUTPUTS {
            return Err(ConfigError(format!(
                "configuration may contain at most {MAX_OUTPUTS} mapping groups"
            )));
        }

        for (index, group) in self.groups.iter().enumerate() {
            group.validate(validate_sizing)?;
            if self.groups[..index]
                .iter()
                .any(|other| other.id == group.id)
            {
                return Err(ConfigError(format!(
                    "mapping group id {} is duplicated",
                    group.id
                )));
            }
            if let GroupRouteConfig::Mirror {
                target_id: Some(target),
            } = &group.route
                && self.groups[..index].iter().any(|other| {
                    matches!(
                        &other.route,
                        GroupRouteConfig::Mirror {
                            target_id: Some(other_target)
                        } if other_target.eq_ignore_ascii_case(target)
                    )
                })
            {
                return Err(ConfigError(format!(
                    "mirror target {target} belongs to more than one mapping group"
                )));
            }
        }

        if let Some(selected) = self.selected_group_id
            && !self.groups.iter().any(|group| group.id == selected)
        {
            return Err(ConfigError(format!(
                "selected mapping group id {selected} does not exist"
            )));
        }
        Ok(())
    }
}

impl GroupConfig {
    fn validate(&self, validate_sizing: bool) -> Result<(), ConfigError> {
        if self.id >= MAX_OUTPUTS as u32 {
            return Err(ConfigError(format!(
                "mapping group id {} must be between 0 and {}",
                self.id,
                MAX_OUTPUTS - 1
            )));
        }
        match &self.route {
            GroupRouteConfig::Mirror {
                target_id: Some(target),
            } if target.trim().is_empty() => {
                return Err(ConfigError(format!(
                    "mapping group {} has an empty target id",
                    self.id
                )));
            }
            GroupRouteConfig::StreamOnly { screen } => screen.validate(self.id)?,
            _ => {}
        }
        if matches!(
            self.reference_source.as_ref(),
            Some(ReferenceSource::Display(id)) if id.trim().is_empty()
        ) {
            return Err(ConfigError(format!(
                "mapping group {} has an empty reference display id",
                self.id
            )));
        }
        if validate_sizing && let Some(sizing) = self.sizing {
            sizing.calculate().map_err(|error| {
                ConfigError(format!(
                    "mapping group {} has invalid sizing parameters: {error}",
                    self.id
                ))
            })?;
        }
        Ok(())
    }
}

impl StreamScreenConfig {
    fn validate(self, group_id: u32) -> Result<(), ConfigError> {
        if self.width == 0
            || self.height == 0
            || self.width > MAX_VIRTUAL_DIMENSION
            || self.height > MAX_VIRTUAL_DIMENSION
        {
            return Err(ConfigError(format!(
                "mapping group {group_id} stream dimensions must be between 1 and {MAX_VIRTUAL_DIMENSION}"
            )));
        }
        if !valid_refresh_millihz(self.refresh_millihz) {
            return Err(ConfigError(format!(
                "mapping group {group_id} stream refresh must be between {MIN_REFRESH_MILLIHZ} and {MAX_REFRESH_MILLIHZ} millihertz"
            )));
        }
        if self.aspect_ratio.width == 0 || self.aspect_ratio.height == 0 {
            return Err(ConfigError(format!(
                "mapping group {group_id} stream aspect ratio dimensions must be positive"
            )));
        }
        if let Some(diagonal_inches) = self.diagonal_inches {
            let diagonal_mm = diagonal_inches * MILLIMETERS_PER_INCH;
            if !valid_physical_millimeters(diagonal_mm) {
                return Err(ConfigError(format!(
                    "mapping group {group_id} stream diagonal must be between 10 and 10000 millimetres"
                )));
            }
        }
        Ok(())
    }
}

impl LegacyAppConfig {
    fn validate(&self) -> Result<(), ConfigError> {
        if self.version != LEGACY_CONFIG_VERSION {
            return Err(ConfigError(format!(
                "unsupported legacy config version {}",
                self.version
            )));
        }
        if self
            .target_id
            .as_ref()
            .is_some_and(|target| target.trim().is_empty())
        {
            return Err(ConfigError("legacy target id cannot be empty".into()));
        }
        if matches!(
            self.reference_source.as_ref(),
            Some(ReferenceSource::Display(id)) if id.trim().is_empty()
        ) {
            return Err(ConfigError(
                "legacy reference display id cannot be empty".into(),
            ));
        }
        if let Some(sizing) = self.sizing {
            sizing.calculate().map_err(|error| {
                ConfigError(format!("legacy sizing parameters are invalid: {error}"))
            })?;
        }
        Ok(())
    }
}

impl From<LegacyAppConfig> for AppConfig {
    fn from(legacy: LegacyAppConfig) -> Self {
        Self {
            version: CONFIG_VERSION,
            groups: vec![GroupConfig {
                id: 0,
                route: GroupRouteConfig::Mirror {
                    target_id: legacy.target_id,
                },
                reference_source: legacy.reference_source,
                sizing: legacy.sizing,
            }],
            selected_group_id: Some(0),
        }
    }
}

fn invalid_config(path: &Path, error: impl Display) -> LoadOutcome {
    LoadOutcome {
        config: AppConfig::default(),
        warning: Some(format!(
            "{} is invalid and was left unchanged: {error}",
            path.display()
        )),
    }
}

fn remove_if_present(path: &Path) -> Result<(), ConfigError> {
    match fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(ConfigError(format!(
            "could not remove {}: {error}",
            path.display()
        ))),
    }
}

impl Default for DisplayOverrides {
    fn default() -> Self {
        Self {
            version: DISPLAY_OVERRIDES_VERSION,
            displays: Vec::new(),
        }
    }
}

impl DisplayOverrides {
    pub fn override_for(&self, display_id: &str) -> Option<&DisplayOverride> {
        self.displays
            .iter()
            .find(|entry| entry.display_id.eq_ignore_ascii_case(display_id))
    }

    pub fn upsert(
        &mut self,
        display_id: String,
        diagonal_inches: f64,
        aspect_ratio: Option<AspectRatio>,
    ) -> Result<(), ConfigError> {
        validate_override(&display_id, diagonal_inches, aspect_ratio)?;
        if let Some(entry) = self
            .displays
            .iter_mut()
            .find(|entry| entry.display_id.eq_ignore_ascii_case(&display_id))
        {
            entry.display_id = display_id;
            entry.diagonal_inches = diagonal_inches;
            entry.aspect_ratio = aspect_ratio;
        } else {
            self.displays.push(DisplayOverride {
                display_id,
                diagonal_inches,
                aspect_ratio,
            });
        }
        self.displays
            .sort_by_key(|entry| entry.display_id.to_ascii_lowercase());
        Ok(())
    }

    pub fn remove(&mut self, display_id: &str) -> bool {
        let before = self.displays.len();
        self.displays
            .retain(|entry| !entry.display_id.eq_ignore_ascii_case(display_id));
        self.displays.len() != before
    }

    fn validate(&self) -> Result<(), ConfigError> {
        if self.version != DISPLAY_OVERRIDES_VERSION {
            return Err(ConfigError(format!(
                "unsupported display override version {}",
                self.version
            )));
        }
        if self.displays.len() > 256 {
            return Err(ConfigError("too many display overrides".into()));
        }
        for (index, entry) in self.displays.iter().enumerate() {
            validate_override(&entry.display_id, entry.diagonal_inches, entry.aspect_ratio)?;
            if self.displays[..index]
                .iter()
                .any(|other| other.display_id.eq_ignore_ascii_case(&entry.display_id))
            {
                return Err(ConfigError(format!(
                    "duplicate display override id {}",
                    entry.display_id
                )));
            }
        }
        Ok(())
    }
}

fn validate_override(
    display_id: &str,
    diagonal_inches: f64,
    aspect_ratio: Option<AspectRatio>,
) -> Result<(), ConfigError> {
    if display_id.trim().is_empty() {
        return Err(ConfigError("display override id cannot be empty".into()));
    }
    let diagonal_mm = diagonal_inches * MILLIMETERS_PER_INCH;
    if !valid_physical_millimeters(diagonal_mm) {
        return Err(ConfigError(
            "display diagonal must be between 10 and 10000 millimetres".into(),
        ));
    }
    if aspect_ratio.is_some_and(|ratio| ratio.width == 0 || ratio.height == 0) {
        return Err(ConfigError(
            "display override aspect ratio dimensions must be positive".into(),
        ));
    }
    Ok(())
}

impl ConfigStore {
    pub fn default_path() -> Result<PathBuf, ConfigError> {
        local_config_path(CONFIG_FILE)
    }

    pub fn default_store() -> Result<Self, ConfigError> {
        let path = Self::default_path()?;
        let legacy_path = path
            .parent()
            .expect("default config path has a parent")
            .join(LEGACY_CONFIG_FILE);
        Ok(Self {
            path,
            legacy_path: Some(legacy_path),
        })
    }

    pub fn new(path: PathBuf) -> Self {
        Self {
            path,
            legacy_path: None,
        }
    }

    #[cfg(test)]
    fn with_legacy_path(path: PathBuf, legacy_path: PathBuf) -> Self {
        Self {
            path,
            legacy_path: Some(legacy_path),
        }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn load(&self) -> Result<LoadOutcome, ConfigError> {
        let bytes = match fs::read(&self.path) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return self.load_legacy();
            }
            Err(error) => {
                return Err(ConfigError(format!(
                    "could not read {}: {error}",
                    self.path.display()
                )));
            }
        };

        let version = match serde_json::from_slice::<VersionHeader>(&bytes) {
            Ok(header) => header.version,
            Err(error) => return Ok(invalid_config(&self.path, error)),
        };
        if version != CONFIG_VERSION {
            return Ok(LoadOutcome {
                config: AppConfig::default(),
                warning: Some(format!(
                    "{} uses unsupported config version {} and was left unchanged",
                    self.path.display(),
                    version
                )),
            });
        }

        let config = match serde_json::from_slice::<AppConfig>(&bytes) {
            Ok(config) => config,
            Err(error) => return Ok(invalid_config(&self.path, error)),
        };
        if let Err(error) = config.validate() {
            return Ok(LoadOutcome {
                config: AppConfig::default(),
                warning: Some(format!(
                    "{} is invalid and was left unchanged: {error}",
                    self.path.display()
                )),
            });
        }
        Ok(LoadOutcome {
            config,
            warning: None,
        })
    }

    pub fn load_strict(&self) -> Result<AppConfig, ConfigError> {
        let bytes = fs::read(&self.path).map_err(|error| {
            ConfigError(format!("could not read {}: {error}", self.path.display()))
        })?;
        let config = serde_json::from_slice::<AppConfig>(&bytes).map_err(|error| {
            ConfigError(format!(
                "{} is not a valid SBMS configuration: {error}",
                self.path.display()
            ))
        })?;
        config.validate()?;
        Ok(config)
    }

    pub fn save(&self, config: &AppConfig) -> Result<(), ConfigError> {
        config.validate()?;
        save_json_atomically(&self.path, config, "config", "config")
    }

    pub fn save_new(&self, config: &AppConfig) -> Result<(), ConfigError> {
        config.validate()?;
        save_json_atomically_new(&self.path, config, "config", "config")
    }

    pub fn reset(&self) -> Result<(), ConfigError> {
        remove_if_present(&self.path)?;
        if let Some(legacy_path) = &self.legacy_path {
            remove_if_present(legacy_path)?;
        }
        Ok(())
    }

    fn load_legacy(&self) -> Result<LoadOutcome, ConfigError> {
        let Some(legacy_path) = &self.legacy_path else {
            return Ok(LoadOutcome {
                config: AppConfig::default(),
                warning: None,
            });
        };
        let bytes = match fs::read(legacy_path) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(LoadOutcome {
                    config: AppConfig::default(),
                    warning: None,
                });
            }
            Err(error) => {
                return Err(ConfigError(format!(
                    "could not read {}: {error}",
                    legacy_path.display()
                )));
            }
        };

        let version = match serde_json::from_slice::<VersionHeader>(&bytes) {
            Ok(header) => header.version,
            Err(error) => return Ok(invalid_config(legacy_path, error)),
        };
        if version != LEGACY_CONFIG_VERSION {
            return Ok(LoadOutcome {
                config: AppConfig::default(),
                warning: Some(format!(
                    "{} uses unsupported config version {} and was left unchanged",
                    legacy_path.display(),
                    version
                )),
            });
        }
        let legacy = match serde_json::from_slice::<LegacyAppConfig>(&bytes) {
            Ok(config) => config,
            Err(error) => return Ok(invalid_config(legacy_path, error)),
        };
        if let Err(error) = legacy.validate() {
            return Ok(LoadOutcome {
                config: AppConfig::default(),
                warning: Some(format!(
                    "{} is invalid and was left unchanged: {error}",
                    legacy_path.display()
                )),
            });
        }
        let config = AppConfig::from(legacy);
        self.save(&config).map_err(|error| {
            ConfigError(format!(
                "could not migrate {} to {}: {error}",
                legacy_path.display(),
                self.path.display()
            ))
        })?;
        Ok(LoadOutcome {
            config,
            warning: None,
        })
    }
}

impl ConfigProfiles {
    fn validate(&self) -> Result<(), ConfigError> {
        if self.version != CONFIG_PROFILES_VERSION {
            return Err(ConfigError(format!(
                "unsupported config profiles version {}",
                self.version
            )));
        }
        if self.profiles.is_empty() {
            return Err(ConfigError(
                "config profiles must contain at least one profile".into(),
            ));
        }
        if self.profiles.len() > MAX_CONFIG_PROFILES {
            return Err(ConfigError(format!(
                "config profiles may contain at most {MAX_CONFIG_PROFILES} profiles"
            )));
        }
        validate_profile_id(&self.active_profile)?;
        for (index, profile) in self.profiles.iter().enumerate() {
            validate_profile_id(&profile.id)?;
            if profile.revision == 0 {
                return Err(ConfigError(format!(
                    "config profile {} has an invalid zero revision",
                    profile.id
                )));
            }
            if self.profiles[..index]
                .iter()
                .any(|other| other.id.eq_ignore_ascii_case(&profile.id))
            {
                return Err(ConfigError(format!(
                    "config profile id {} is duplicated",
                    profile.id
                )));
            }
            profile.config.validate()?;
        }
        if !self
            .profiles
            .iter()
            .any(|profile| profile.id.eq_ignore_ascii_case(&self.active_profile))
        {
            return Err(ConfigError(format!(
                "active config profile {} does not exist",
                self.active_profile
            )));
        }
        Ok(())
    }

    fn profile(&self, id: &str) -> Option<&ConfigProfile> {
        self.profiles
            .iter()
            .find(|profile| profile.id.eq_ignore_ascii_case(id))
    }

    fn profile_mut(&mut self, id: &str) -> Option<&mut ConfigProfile> {
        self.profiles
            .iter_mut()
            .find(|profile| profile.id.eq_ignore_ascii_case(id))
    }

    fn active(&self) -> &ConfigProfile {
        self.profile(&self.active_profile)
            .expect("validated config profiles contain the active profile")
    }
}

impl ConfigProfileStore {
    pub fn default_path() -> Result<PathBuf, ConfigError> {
        local_config_path(CONFIG_PROFILES_FILE)
    }

    pub fn default_store() -> Result<Self, ConfigError> {
        Ok(Self {
            path: Self::default_path()?,
            legacy_config_path: ConfigStore::default_path()?,
        })
    }

    pub fn new(path: PathBuf, legacy_config_path: PathBuf) -> Self {
        Self {
            path,
            legacy_config_path,
        }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn list(&self) -> Result<ConfigProfiles, ConfigError> {
        let _guard = CrossProcessConfigGuard::acquire()?;
        self.load_or_initialize_locked()
    }

    pub fn load_active(&self) -> Result<ProfileSnapshot, ConfigError> {
        let _guard = CrossProcessConfigGuard::acquire()?;
        let profiles = self.load_or_initialize_locked()?;
        Ok(snapshot(profiles.active()))
    }

    pub fn set_launch_intro_disabled(&self, disabled: bool) -> Result<(), ConfigError> {
        let _guard = CrossProcessConfigGuard::acquire()?;
        let mut profiles = self.load_or_initialize_locked()?;
        profiles.launch_intro_disabled = disabled;
        self.save_locked(&profiles)
    }

    pub fn load_profile(&self, id: &str) -> Result<ProfileSnapshot, ConfigError> {
        validate_profile_id(id)?;
        let _guard = CrossProcessConfigGuard::acquire()?;
        let profiles = self.load_or_initialize_locked()?;
        profiles
            .profile(id)
            .map(snapshot)
            .ok_or_else(|| ConfigError(format!("config profile {id} does not exist")))
    }

    pub fn update_active(
        &self,
        update: impl FnOnce(&mut AppConfig) -> Result<(), ConfigError>,
    ) -> Result<ProfileSnapshot, ConfigError> {
        let _guard = CrossProcessConfigGuard::acquire()?;
        let mut profiles = self.load_or_initialize_locked()?;
        let active_id = profiles.active_profile.clone();
        let profile = profiles
            .profile_mut(&active_id)
            .expect("validated config profiles contain the active profile");
        update(&mut profile.config)?;
        profile.config.validate()?;
        profile.revision = next_revision(profile.revision)?;
        let result = snapshot(profile);
        self.save_locked(&profiles)?;
        Ok(result)
    }

    pub fn save_active_if_revision(
        &self,
        expected: &ProfileRevision,
        config: &AppConfig,
    ) -> Result<ProfileSnapshot, ConfigError> {
        self.save_active_if_revision_inner(expected, config, true)
    }

    fn save_active_if_revision_inner(
        &self,
        expected: &ProfileRevision,
        config: &AppConfig,
        validate_sizing: bool,
    ) -> Result<ProfileSnapshot, ConfigError> {
        config.validate_with_sizing(validate_sizing)?;
        let _guard = CrossProcessConfigGuard::acquire()?;
        let mut profiles = self.load_or_initialize_locked()?;
        if !profiles.active_profile.eq_ignore_ascii_case(&expected.id) {
            return Err(ConfigError(format!(
                "active config profile changed from {} to {}; reload before saving",
                expected.id, profiles.active_profile
            )));
        }
        let profile = profiles
            .profile_mut(&expected.id)
            .expect("validated config profiles contain the active profile");
        if profile.revision != expected.revision {
            return Err(ConfigError(format!(
                "config profile {} changed from revision {} to {}; reload before saving",
                profile.id, expected.revision, profile.revision
            )));
        }
        profile.config = config.clone();
        profile.revision = next_revision(profile.revision)?;
        let result = snapshot(profile);
        self.save_locked(&profiles)?;
        Ok(result)
    }

    pub fn save_profile(
        &self,
        id: &str,
        config: &AppConfig,
        replace: bool,
        activate: bool,
    ) -> Result<(ProfileSnapshot, bool), ConfigError> {
        validate_profile_id(id)?;
        config.validate()?;
        let _guard = CrossProcessConfigGuard::acquire()?;
        let mut profiles = self.load_or_initialize_locked()?;
        let profile_index = profiles
            .profiles
            .iter()
            .position(|profile| profile.id.eq_ignore_ascii_case(id));
        let index = if let Some(index) = profile_index {
            if !replace {
                return Err(ConfigError(format!(
                    "config profile {} already exists; use --replace to overwrite it",
                    profiles.profiles[index].id
                )));
            }
            profiles.profiles[index].config = config.clone();
            profiles.profiles[index].revision = next_revision(profiles.profiles[index].revision)?;
            index
        } else {
            if profiles.profiles.len() >= MAX_CONFIG_PROFILES {
                return Err(ConfigError(format!(
                    "cannot create config profile {id}; the {MAX_CONFIG_PROFILES}-profile limit is reached"
                )));
            }
            profiles.profiles.push(ConfigProfile {
                id: id.into(),
                revision: 1,
                config: config.clone(),
            });
            profiles.profiles.len() - 1
        };
        if activate {
            profiles.active_profile = profiles.profiles[index].id.clone();
        }
        let result = snapshot(&profiles.profiles[index]);
        let is_active = profiles
            .active_profile
            .eq_ignore_ascii_case(&result.profile.id);
        self.save_locked(&profiles)?;
        Ok((result, is_active))
    }

    pub fn save_active_as(&self, id: &str) -> Result<ProfileSnapshot, ConfigError> {
        validate_profile_id(id)?;
        let _guard = CrossProcessConfigGuard::acquire()?;
        let mut profiles = self.load_or_initialize_locked()?;
        if profiles.active_profile.eq_ignore_ascii_case(id) {
            return Ok(snapshot(profiles.active()));
        }
        let config = profiles.active().config.clone();
        if let Some(profile) = profiles.profile_mut(id) {
            profile.config = config;
            profile.revision = next_revision(profile.revision)?;
            let result = snapshot(profile);
            self.save_locked(&profiles)?;
            return Ok(result);
        }
        if profiles.profiles.len() >= MAX_CONFIG_PROFILES {
            return Err(ConfigError(format!(
                "cannot create config profile {id}; the {MAX_CONFIG_PROFILES}-profile limit is reached"
            )));
        }
        profiles.profiles.push(ConfigProfile {
            id: id.into(),
            revision: 1,
            config,
        });
        let result = snapshot(profiles.profiles.last().expect("profile was pushed"));
        self.save_locked(&profiles)?;
        Ok(result)
    }

    pub fn activate(&self, id: &str) -> Result<ProfileSnapshot, ConfigError> {
        validate_profile_id(id)?;
        let _guard = CrossProcessConfigGuard::acquire()?;
        let mut profiles = self.load_or_initialize_locked()?;
        let profile = profiles
            .profile(id)
            .ok_or_else(|| ConfigError(format!("config profile {id} does not exist")))?;
        let result = snapshot(profile);
        profiles.active_profile = result.profile.id.clone();
        self.save_locked(&profiles)?;
        Ok(result)
    }

    pub fn delete(&self, id: &str) -> Result<(), ConfigError> {
        validate_profile_id(id)?;
        let _guard = CrossProcessConfigGuard::acquire()?;
        let mut profiles = self.load_or_initialize_locked()?;
        if profiles.active_profile.eq_ignore_ascii_case(id) {
            return Err(ConfigError(format!(
                "cannot delete active config profile {}",
                profiles.active_profile
            )));
        }
        let before = profiles.profiles.len();
        profiles
            .profiles
            .retain(|profile| !profile.id.eq_ignore_ascii_case(id));
        if profiles.profiles.len() == before {
            return Err(ConfigError(format!("config profile {id} does not exist")));
        }
        self.save_locked(&profiles)
    }

    pub fn reset_active(&self) -> Result<ProfileSnapshot, ConfigError> {
        let _guard = CrossProcessConfigGuard::acquire()?;
        let mut profiles = if self.path.exists() {
            self.load_or_initialize_locked()?
        } else {
            ConfigProfiles {
                version: CONFIG_PROFILES_VERSION,
                active_profile: "default".into(),
                launch_intro_disabled: false,
                profiles: vec![ConfigProfile {
                    id: "default".into(),
                    revision: 1,
                    config: AppConfig::default(),
                }],
            }
        };
        let active_id = profiles.active_profile.clone();
        let profile = profiles
            .profile_mut(&active_id)
            .expect("validated config profiles contain the active profile");
        profile.config = AppConfig::default();
        profile.revision = next_revision(profile.revision)?;
        let result = snapshot(profile);
        self.save_locked(&profiles)?;
        Ok(result)
    }

    fn load_or_initialize_locked(&self) -> Result<ConfigProfiles, ConfigError> {
        match fs::read(&self.path) {
            Ok(bytes) => {
                let profiles =
                    serde_json::from_slice::<ConfigProfiles>(&bytes).map_err(|error| {
                        ConfigError(format!(
                            "{} is not a valid SBMS config profile store: {error}",
                            self.path.display()
                        ))
                    })?;
                profiles.validate()?;
                Ok(profiles)
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                let legacy_store = ConfigStore {
                    path: self.legacy_config_path.clone(),
                    legacy_path: Some(
                        self.legacy_config_path
                            .parent()
                            .unwrap_or_else(|| Path::new("."))
                            .join(LEGACY_CONFIG_FILE),
                    ),
                };
                let config = match legacy_store.load() {
                    Ok(outcome) => {
                        if let Some(warning) = outcome.warning {
                            return Err(ConfigError(format!(
                                "cannot initialize config profiles: {warning}"
                            )));
                        }
                        outcome.config
                    }
                    Err(error) => return Err(error),
                };
                let profiles = ConfigProfiles {
                    version: CONFIG_PROFILES_VERSION,
                    active_profile: "default".into(),
                    launch_intro_disabled: false,
                    profiles: vec![ConfigProfile {
                        id: "default".into(),
                        revision: 1,
                        config,
                    }],
                };
                self.save_locked(&profiles)?;
                Ok(profiles)
            }
            Err(error) => Err(ConfigError(format!(
                "could not read {}: {error}",
                self.path.display()
            ))),
        }
    }

    fn save_locked(&self, profiles: &ConfigProfiles) -> Result<(), ConfigError> {
        profiles.validate()?;
        save_json_atomically(&self.path, profiles, "config profiles", "config profiles")
    }
}

impl CrossProcessConfigGuard {
    fn acquire() -> Result<Self, ConfigError> {
        let handle = unsafe { CreateMutexW(None, false, w!("Local\\SBMSConfigProfiles-v1")) }
            .map_err(|error| {
                ConfigError(format!("could not create config profiles lock: {error}"))
            })?;
        let wait = unsafe { WaitForSingleObject(handle, INFINITE) };
        if wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED {
            return Ok(Self(handle));
        }
        unsafe {
            let _ = CloseHandle(handle);
        }
        if wait == WAIT_FAILED {
            Err(ConfigError(format!(
                "could not acquire config profiles lock: {}",
                std::io::Error::last_os_error()
            )))
        } else {
            Err(ConfigError(format!(
                "unexpected config profiles lock wait result: {}",
                wait.0
            )))
        }
    }
}

impl Drop for CrossProcessConfigGuard {
    fn drop(&mut self) {
        unsafe {
            let _ = ReleaseMutex(self.0);
            let _ = CloseHandle(self.0);
        }
    }
}

fn snapshot(profile: &ConfigProfile) -> ProfileSnapshot {
    ProfileSnapshot {
        profile: ProfileRevision {
            id: profile.id.clone(),
            revision: profile.revision,
        },
        config: profile.config.clone(),
    }
}

fn validate_profile_id(id: &str) -> Result<(), ConfigError> {
    if id.is_empty()
        || id.len() > 32
        || !id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'_' || byte == b'-')
    {
        return Err(ConfigError(
            "config profile id must contain 1-32 ASCII letters, digits, underscores, or hyphens"
                .into(),
        ));
    }
    Ok(())
}

fn next_revision(revision: u64) -> Result<u64, ConfigError> {
    revision
        .checked_add(1)
        .ok_or_else(|| ConfigError("config profile revision overflow".into()))
}

impl DisplayOverrideStore {
    pub fn default_path() -> Result<PathBuf, ConfigError> {
        local_config_path(DISPLAY_OVERRIDES_FILE)
    }

    pub fn default_store() -> Result<Self, ConfigError> {
        Ok(Self::new(Self::default_path()?))
    }

    pub fn new(path: PathBuf) -> Self {
        Self { path }
    }

    pub fn load(&self) -> Result<DisplayOverrideLoadOutcome, ConfigError> {
        let bytes = match fs::read(&self.path) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(DisplayOverrideLoadOutcome {
                    overrides: DisplayOverrides::default(),
                    warning: None,
                });
            }
            Err(error) => {
                return Err(ConfigError(format!(
                    "could not read {}: {error}",
                    self.path.display()
                )));
            }
        };
        let overrides = match serde_json::from_slice::<DisplayOverrides>(&bytes) {
            Ok(overrides) => overrides,
            Err(error) => {
                return Ok(DisplayOverrideLoadOutcome {
                    overrides: DisplayOverrides::default(),
                    warning: Some(format!(
                        "{} is invalid and was left unchanged: {error}",
                        self.path.display()
                    )),
                });
            }
        };
        if let Err(error) = overrides.validate() {
            return Ok(DisplayOverrideLoadOutcome {
                overrides: DisplayOverrides::default(),
                warning: Some(format!(
                    "{} is invalid and was left unchanged: {error}",
                    self.path.display()
                )),
            });
        }
        Ok(DisplayOverrideLoadOutcome {
            overrides,
            warning: None,
        })
    }

    pub fn save(&self, overrides: &DisplayOverrides) -> Result<(), ConfigError> {
        overrides.validate()?;
        save_json_atomically(
            &self.path,
            overrides,
            "display override",
            "display overrides",
        )
    }
}

fn local_config_path(file_name: &str) -> Result<PathBuf, ConfigError> {
    let local_app_data = env::var_os("LOCALAPPDATA")
        .filter(|value| !value.is_empty())
        .ok_or_else(|| ConfigError("LOCALAPPDATA is not available".into()))?;
    Ok(PathBuf::from(local_app_data)
        .join(CONFIG_DIRECTORY)
        .join(file_name))
}

fn save_json_atomically<T: Serialize>(
    destination: &Path,
    value: &T,
    path_kind: &str,
    artifact_name: &str,
) -> Result<(), ConfigError> {
    save_json_atomically_inner(destination, value, path_kind, artifact_name, true)
}

fn save_json_atomically_new<T: Serialize>(
    destination: &Path,
    value: &T,
    path_kind: &str,
    artifact_name: &str,
) -> Result<(), ConfigError> {
    save_json_atomically_inner(destination, value, path_kind, artifact_name, false)
}

fn save_json_atomically_inner<T: Serialize>(
    destination: &Path,
    value: &T,
    path_kind: &str,
    artifact_name: &str,
    replace: bool,
) -> Result<(), ConfigError> {
    let parent = destination
        .parent()
        .ok_or_else(|| ConfigError(format!("{path_kind} path has no parent directory")))?;
    fs::create_dir_all(parent)
        .map_err(|error| ConfigError(format!("could not create {}: {error}", parent.display())))?;
    let (temporary_path, mut temporary) = create_temporary(parent, destination)?;
    let result = (|| {
        let bytes = serde_json::to_vec_pretty(value).map_err(|error| {
            ConfigError(format!("could not serialize {artifact_name}: {error}"))
        })?;
        temporary
            .write_all(&bytes)
            .and_then(|_| temporary.write_all(b"\n"))
            .and_then(|_| temporary.sync_all())
            .map_err(|error| {
                ConfigError(format!(
                    "could not write temporary {artifact_name} {}: {error}",
                    temporary_path.display()
                ))
            })?;
        drop(temporary);
        if replace {
            atomic_replace(&temporary_path, destination)
        } else {
            atomic_install_new(&temporary_path, destination)
        }
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary_path);
    }
    result
}

fn atomic_install_new(temporary: &Path, destination: &Path) -> Result<(), ConfigError> {
    let temporary_wide = wide(temporary.as_os_str());
    let destination_wide = wide(destination.as_os_str());
    unsafe {
        MoveFileExW(
            PCWSTR(temporary_wide.as_ptr()),
            PCWSTR(destination_wide.as_ptr()),
            MOVEFILE_WRITE_THROUGH,
        )
    }
    .map_err(|error| {
        ConfigError(format!(
            "could not atomically create {}: {error}",
            destination.display()
        ))
    })
}

fn create_temporary(parent: &Path, destination: &Path) -> Result<(PathBuf, File), ConfigError> {
    let file_name = destination
        .file_name()
        .and_then(OsStr::to_str)
        .unwrap_or("config.json");
    for _ in 0..32 {
        let sequence = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let path = parent.join(format!(
            ".{file_name}.{}.{}.tmp",
            std::process::id(),
            sequence
        ));
        match OpenOptions::new().write(true).create_new(true).open(&path) {
            Ok(file) => return Ok((path, file)),
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(error) => {
                return Err(ConfigError(format!(
                    "could not create temporary config {}: {error}",
                    path.display()
                )));
            }
        }
    }
    Err(ConfigError(
        "could not allocate a unique temporary config file".into(),
    ))
}

fn atomic_replace(temporary: &Path, destination: &Path) -> Result<(), ConfigError> {
    let temporary_wide = wide(temporary.as_os_str());
    let destination_wide = wide(destination.as_os_str());
    let temporary_pcwstr = PCWSTR(temporary_wide.as_ptr());
    let destination_pcwstr = PCWSTR(destination_wide.as_ptr());
    let result = if destination.exists() {
        unsafe {
            ReplaceFileW(
                destination_pcwstr,
                temporary_pcwstr,
                PCWSTR::null(),
                REPLACEFILE_WRITE_THROUGH,
                None,
                None,
            )
        }
    } else {
        unsafe {
            MoveFileExW(
                temporary_pcwstr,
                destination_pcwstr,
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
            )
        }
    };
    result.map_err(|error| {
        ConfigError(format!(
            "could not atomically replace {}: {error}",
            destination.display()
        ))
    })
}

fn wide(value: &OsStr) -> Vec<u16> {
    value.encode_wide().chain(Some(0)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::geometry::{DisplayGeometry, PhysicalMeasurement, PixelSize, SizingStrategy};

    fn test_store(name: &str) -> ConfigStore {
        let path = env::temp_dir().join(format!(
            "sbms-config-test-{}-{}-{}.json",
            std::process::id(),
            TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed),
            name
        ));
        ConfigStore::new(path)
    }

    fn test_migration_store(name: &str) -> ConfigStore {
        let root = env::temp_dir().join(format!(
            "sbms-config-migration-test-{}-{}-{name}",
            std::process::id(),
            TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed),
        ));
        ConfigStore::with_legacy_path(
            root.with_extension("v2.json"),
            root.with_extension("v1.json"),
        )
    }

    fn test_sizing() -> SizingRequest {
        SizingRequest {
            reference: DisplayGeometry {
                native_pixels: PixelSize {
                    width: 2560,
                    height: 1440,
                },
                physical: PhysicalMeasurement::DiagonalMm(685.8),
                aspect_ratio: Some(AspectRatio {
                    width: 16,
                    height: 9,
                }),
                rotation: Rotation::Deg0,
            },
            target: DisplayGeometry {
                native_pixels: PixelSize {
                    width: 3840,
                    height: 2160,
                },
                physical: PhysicalMeasurement::DiagonalMm(1219.2),
                aspect_ratio: Some(AspectRatio {
                    width: 16,
                    height: 9,
                }),
                rotation: Rotation::Deg0,
            },
            strategy: SizingStrategy::MatchPhysicalSize,
            alignment: 2,
            preferred_refresh_millihz: Some(120_000),
        }
    }

    fn stream_config(refresh_millihz: u32) -> AppConfig {
        let mut config = AppConfig::default();
        config.groups[0].route = GroupRouteConfig::StreamOnly {
            screen: StreamScreenConfig {
                width: 1920,
                height: 1080,
                diagonal_inches: Some(24.0),
                refresh_millihz,
                aspect_ratio: AspectRatio {
                    width: 16,
                    height: 9,
                },
                rotation: Rotation::Deg0,
            },
        };
        config
    }

    fn test_override_store(name: &str) -> DisplayOverrideStore {
        let path = env::temp_dir().join(format!(
            "sbms-display-override-test-{}-{}-{}.json",
            std::process::id(),
            TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed),
            name
        ));
        DisplayOverrideStore::new(path)
    }

    #[test]
    fn missing_config_loads_defaults() {
        let store = test_store("missing");
        let outcome = store.load().unwrap();
        assert_eq!(outcome.config, AppConfig::default());
        assert_eq!(outcome.warning, None);
    }

    #[test]
    fn round_trip_replaces_existing_file() {
        let store = test_store("round-trip");
        let mut config = AppConfig {
            version: CONFIG_VERSION,
            groups: vec![
                GroupConfig {
                    id: 0,
                    route: GroupRouteConfig::Mirror {
                        target_id: Some("stable-display-id".into()),
                    },
                    reference_source: Some(ReferenceSource::Display("reference-display-id".into())),
                    sizing: Some(test_sizing()),
                },
                GroupConfig {
                    id: 3,
                    route: GroupRouteConfig::StreamOnly {
                        screen: StreamScreenConfig {
                            width: 3840,
                            height: 2160,
                            diagonal_inches: Some(48.0),
                            refresh_millihz: 120_000,
                            aspect_ratio: AspectRatio {
                                width: 16,
                                height: 9,
                            },
                            rotation: Rotation::Deg90,
                        },
                    },
                    reference_source: Some(ReferenceSource::Manual),
                    sizing: Some(test_sizing()),
                },
            ],
            selected_group_id: Some(3),
        };
        store.save(&config).unwrap();
        assert_eq!(store.load().unwrap().config, config);

        let GroupRouteConfig::Mirror { target_id } = &mut config.groups[0].route else {
            unreachable!()
        };
        *target_id = Some("replacement-id".into());
        store.save(&config).unwrap();
        assert_eq!(store.load().unwrap().config, config);
        store.reset().unwrap();
    }

    #[test]
    fn empty_reference_display_id_is_rejected() {
        let store = test_store("empty-reference");
        let mut config = AppConfig::default();
        config.groups[0].reference_source = Some(ReferenceSource::Display(" ".into()));
        assert!(store.save(&config).is_err());
    }

    #[test]
    fn malformed_config_is_preserved_and_reported() {
        let store = test_store("malformed");
        fs::write(store.path(), b"{not json").unwrap();
        let before = fs::read(store.path()).unwrap();
        let outcome = store.load().unwrap();
        assert_eq!(outcome.config, AppConfig::default());
        assert!(outcome.warning.is_some());
        assert_eq!(fs::read(store.path()).unwrap(), before);
        store.reset().unwrap();
    }

    #[test]
    fn unsupported_version_is_preserved_and_reported() {
        let store = test_store("future");
        fs::write(store.path(), br#"{"version":99,"groups":[]}"#).unwrap();
        let before = fs::read(store.path()).unwrap();
        let outcome = store.load().unwrap();
        assert_eq!(outcome.config, AppConfig::default());
        assert!(
            outcome
                .warning
                .unwrap()
                .contains("unsupported config version")
        );
        assert_eq!(fs::read(store.path()).unwrap(), before);
        store.reset().unwrap();
    }

    #[test]
    fn legacy_v1_migrates_to_output_one_and_is_preserved() {
        let store = test_migration_store("legacy");
        let legacy = LegacyAppConfig {
            version: LEGACY_CONFIG_VERSION,
            target_id: Some("legacy-target".into()),
            reference_source: Some(ReferenceSource::Display("legacy-reference".into())),
            sizing: Some(test_sizing()),
        };
        let legacy_path = store.legacy_path.as_ref().unwrap();
        let bytes = serde_json::to_vec_pretty(&serde_json::json!({
            "version": legacy.version,
            "target_id": legacy.target_id,
            "reference_source": legacy.reference_source,
            "sizing": legacy.sizing,
        }))
        .unwrap();
        fs::write(legacy_path, &bytes).unwrap();

        let outcome = store.load().unwrap();
        assert_eq!(outcome.warning, None);
        assert_eq!(outcome.config.version, CONFIG_VERSION);
        assert_eq!(outcome.config.groups.len(), 1);
        assert_eq!(outcome.config.groups[0].id, 0);
        assert_eq!(
            outcome.config.groups[0].route,
            GroupRouteConfig::Mirror {
                target_id: Some("legacy-target".into())
            }
        );
        assert_eq!(outcome.config.groups[0].sizing, Some(test_sizing()));
        assert_eq!(outcome.config.selected_group_id, Some(0));
        assert_eq!(fs::read(legacy_path).unwrap(), bytes);
        assert!(store.path().is_file());
        store.reset().unwrap();
    }

    #[test]
    fn legacy_v1_rounded_scale_fixture_migrates_every_sizing_field() {
        let store = test_migration_store("legacy-rounded-scale");
        let legacy_path = store.legacy_path.as_ref().unwrap();
        let bytes = br#"{
  "version": 1,
  "target_id": "\\\\?\\DISPLAY#LHC907B#UID1028",
  "reference_source": {
    "display": "\\\\?\\DISPLAY#GTR1106#UID1024"
  },
  "sizing": {
    "reference": {
      "native_pixels": {
        "width": 5120,
        "height": 2880
      },
      "physical": {
        "dimensions_mm": {
          "width": 597.0,
          "height": 336.0
        }
      },
      "rotation": "deg0"
    },
    "target": {
      "native_pixels": {
        "width": 2560,
        "height": 1440
      },
      "physical": {
        "dimensions_mm": {
          "width": 541.0,
          "height": 303.0
        }
      },
      "rotation": "deg180"
    },
    "strategy": "rounded_scale",
    "alignment": 2,
    "preferred_refresh_millihz": 240000
  }
}"#;
        fs::write(legacy_path, bytes).unwrap();

        let outcome = store.load().unwrap();
        assert_eq!(outcome.warning, None);
        assert_eq!(outcome.config.version, CONFIG_VERSION);
        assert_eq!(outcome.config.groups.len(), 1);
        assert_eq!(outcome.config.selected_group_id, Some(0));

        let group = &outcome.config.groups[0];
        assert_eq!(group.id, 0);
        assert_eq!(
            group.route,
            GroupRouteConfig::Mirror {
                target_id: Some(r"\\?\DISPLAY#LHC907B#UID1028".into())
            }
        );
        assert_eq!(
            group.reference_source,
            Some(ReferenceSource::Display(
                r"\\?\DISPLAY#GTR1106#UID1024".into()
            ))
        );

        let sizing = group.sizing.unwrap();
        assert_eq!(
            sizing.reference.native_pixels,
            PixelSize {
                width: 5120,
                height: 2880
            }
        );
        assert_eq!(
            sizing.reference.physical,
            PhysicalMeasurement::DimensionsMm {
                width: 597.0,
                height: 336.0
            }
        );
        assert_eq!(sizing.reference.aspect_ratio, None);
        assert_eq!(sizing.reference.rotation, Rotation::Deg0);
        assert_eq!(
            sizing.target.native_pixels,
            PixelSize {
                width: 2560,
                height: 1440
            }
        );
        assert_eq!(
            sizing.target.physical,
            PhysicalMeasurement::DimensionsMm {
                width: 541.0,
                height: 303.0
            }
        );
        assert_eq!(sizing.target.aspect_ratio, None);
        assert_eq!(sizing.target.rotation, Rotation::Deg180);
        assert_eq!(sizing.strategy, SizingStrategy::RoundedScale);
        assert_eq!(sizing.alignment, 2);
        assert_eq!(sizing.preferred_refresh_millihz, Some(240_000));

        assert_eq!(fs::read(legacy_path).unwrap(), bytes);
        assert!(store.path().is_file());
        assert_eq!(store.load().unwrap().config, outcome.config);
        store.reset().unwrap();
    }

    #[test]
    fn empty_legacy_config_migrates_to_unconfigured_output_one() {
        let store = test_migration_store("legacy-empty");
        let legacy_path = store.legacy_path.as_ref().unwrap();
        fs::write(
            legacy_path,
            br#"{"version":1,"target_id":null,"reference_source":null,"sizing":null}"#,
        )
        .unwrap();
        assert_eq!(store.load().unwrap().config, AppConfig::default());
        assert!(legacy_path.is_file());
        assert!(store.path().is_file());
        store.reset().unwrap();
    }

    #[test]
    fn malformed_legacy_is_preserved_without_creating_v2() {
        let store = test_migration_store("legacy-malformed");
        let legacy_path = store.legacy_path.as_ref().unwrap();
        let bytes = b"{not json";
        fs::write(legacy_path, bytes).unwrap();
        let outcome = store.load().unwrap();
        assert!(outcome.warning.is_some());
        assert_eq!(fs::read(legacy_path).unwrap(), bytes);
        assert!(!store.path().exists());
        store.reset().unwrap();
    }

    #[test]
    fn future_legacy_is_preserved_without_creating_v2() {
        let store = test_migration_store("legacy-future");
        let legacy_path = store.legacy_path.as_ref().unwrap();
        let bytes = br#"{"version":9,"target_id":"future"}"#;
        fs::write(legacy_path, bytes).unwrap();
        let outcome = store.load().unwrap();
        assert!(
            outcome
                .warning
                .as_deref()
                .is_some_and(|warning| warning.contains("unsupported config version 9"))
        );
        assert_eq!(fs::read(legacy_path).unwrap(), bytes);
        assert!(!store.path().exists());
        store.reset().unwrap();
    }

    #[test]
    fn failed_migration_preserves_legacy_and_creates_no_v2() {
        let blocker = env::temp_dir().join(format!(
            "sbms-config-blocker-{}-{}",
            std::process::id(),
            TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        ));
        let legacy_path = env::temp_dir().join(format!(
            "sbms-config-legacy-{}-{}.json",
            std::process::id(),
            TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        ));
        fs::write(&blocker, b"not a directory").unwrap();
        let bytes = br#"{"version":1,"target_id":"legacy"}"#;
        fs::write(&legacy_path, bytes).unwrap();
        let store = ConfigStore::with_legacy_path(blocker.join(CONFIG_FILE), legacy_path.clone());

        assert!(store.load().is_err());
        assert_eq!(fs::read(&legacy_path).unwrap(), bytes);
        assert!(!store.path().exists());

        fs::remove_file(legacy_path).unwrap();
        fs::remove_file(blocker).unwrap();
    }

    #[test]
    fn existing_malformed_v2_does_not_fall_back_to_legacy() {
        let store = test_migration_store("v2-malformed");
        let legacy_path = store.legacy_path.as_ref().unwrap();
        fs::write(
            legacy_path,
            br#"{"version":1,"target_id":"legacy","reference_source":null,"sizing":null}"#,
        )
        .unwrap();
        let current = b"{broken v2";
        fs::write(store.path(), current).unwrap();
        let outcome = store.load().unwrap();
        assert!(outcome.warning.is_some());
        assert_eq!(outcome.config, AppConfig::default());
        assert_eq!(fs::read(store.path()).unwrap(), current);
        store.reset().unwrap();
    }

    #[test]
    fn existing_future_v2_does_not_fall_back_to_legacy() {
        let store = test_migration_store("v2-future");
        fs::write(
            store.legacy_path.as_ref().unwrap(),
            br#"{"version":1,"target_id":"legacy"}"#,
        )
        .unwrap();
        fs::write(store.path(), br#"{"version":3,"groups":[]}"#).unwrap();
        let outcome = store.load().unwrap();
        assert!(
            outcome
                .warning
                .as_deref()
                .is_some_and(|warning| warning.contains("unsupported config version 3"))
        );
        assert_eq!(outcome.config, AppConfig::default());
        store.reset().unwrap();
    }

    #[test]
    fn reset_removes_current_and_legacy_configs() {
        let store = test_migration_store("reset");
        fs::write(store.path(), b"current").unwrap();
        fs::write(store.legacy_path.as_ref().unwrap(), b"legacy").unwrap();
        store.reset().unwrap();
        assert!(!store.path().exists());
        assert!(!store.legacy_path.as_ref().unwrap().exists());
    }

    #[test]
    fn group_validation_rejects_invalid_collections() {
        let store = test_store("invalid-groups");
        let mut empty = AppConfig::default();
        empty.groups.clear();
        assert!(store.save(&empty).is_err());

        let mut duplicate = AppConfig::default();
        duplicate.groups.push(GroupConfig::default());
        assert!(store.save(&duplicate).is_err());

        let missing_selection = AppConfig {
            selected_group_id: Some(7),
            ..AppConfig::default()
        };
        assert!(store.save(&missing_selection).is_err());

        let too_many = AppConfig {
            groups: (0..=MAX_OUTPUTS as u32)
                .map(|id| GroupConfig {
                    id,
                    ..GroupConfig::default()
                })
                .collect(),
            selected_group_id: Some(0),
            ..AppConfig::default()
        };
        assert!(store.save(&too_many).is_err());
    }

    #[test]
    fn exactly_sixteen_groups_round_trip_in_tab_order() {
        let store = test_store("sixteen-groups");
        let order = [12, 4, 0, 15, 7, 2, 10, 6, 1, 14, 5, 9, 3, 13, 8, 11];
        let config = AppConfig {
            version: CONFIG_VERSION,
            groups: order
                .into_iter()
                .map(|id| GroupConfig {
                    id,
                    ..GroupConfig::default()
                })
                .collect(),
            selected_group_id: Some(15),
        };
        store.save(&config).unwrap();
        let loaded = store.load().unwrap().config;
        assert_eq!(
            loaded
                .groups
                .iter()
                .map(|group| group.id)
                .collect::<Vec<_>>(),
            order
        );
        store.reset().unwrap();
    }

    #[test]
    fn duplicate_mirror_targets_are_case_insensitive() {
        let store = test_store("duplicate-target");
        let mut config = AppConfig::default();
        config.groups[0].route = GroupRouteConfig::Mirror {
            target_id: Some("DISPLAY-A".into()),
        };
        config.groups.push(GroupConfig {
            id: 1,
            route: GroupRouteConfig::Mirror {
                target_id: Some("display-a".into()),
            },
            ..GroupConfig::default()
        });
        assert!(store.save(&config).is_err());
    }

    #[test]
    fn invalid_stream_parameters_are_rejected() {
        let store = test_store("invalid-stream");
        let mut config = AppConfig::default();
        config.groups[0].route = GroupRouteConfig::StreamOnly {
            screen: StreamScreenConfig {
                width: 0,
                height: 2160,
                diagonal_inches: Some(f64::NAN),
                refresh_millihz: 0,
                aspect_ratio: AspectRatio {
                    width: 0,
                    height: 9,
                },
                rotation: Rotation::Deg0,
            },
        };
        assert!(store.save(&config).is_err());
    }

    #[test]
    fn stream_refresh_boundaries_round_trip() {
        for refresh_millihz in [MIN_REFRESH_MILLIHZ, MAX_REFRESH_MILLIHZ] {
            let store = test_store(&format!("refresh-{refresh_millihz}"));
            let config = stream_config(refresh_millihz);
            store.save(&config).unwrap();
            let outcome = store.load().unwrap();
            assert_eq!(outcome.warning, None);
            assert_eq!(outcome.config, config);
            store.reset().unwrap();
        }
    }

    #[test]
    fn sub_hertz_stream_refresh_is_rejected_on_save_and_load() {
        let store = test_store("sub-hertz-refresh");
        let config = stream_config(MIN_REFRESH_MILLIHZ - 1);
        assert!(store.save(&config).is_err());

        let bytes = serde_json::to_vec_pretty(&config).unwrap();
        fs::write(store.path(), &bytes).unwrap();
        let outcome = store.load().unwrap();
        assert_eq!(outcome.config, AppConfig::default());
        assert!(
            outcome
                .warning
                .as_deref()
                .is_some_and(|warning| warning.contains("stream refresh"))
        );
        assert_eq!(fs::read(store.path()).unwrap(), bytes);
        store.reset().unwrap();
    }

    #[test]
    fn display_override_round_trip_and_case_insensitive_lookup() {
        let store = test_override_store("round-trip");
        let mut overrides = DisplayOverrides::default();
        overrides
            .upsert(
                r"\\?\DISPLAY#ABC".into(),
                24.0,
                Some(AspectRatio {
                    width: 16,
                    height: 9,
                }),
            )
            .unwrap();
        store.save(&overrides).unwrap();
        let loaded = store.load().unwrap();
        assert_eq!(loaded.warning, None);
        assert_eq!(
            loaded
                .overrides
                .override_for(r"\\?\display#abc")
                .map(|entry| (entry.diagonal_inches, entry.aspect_ratio)),
            Some((
                24.0,
                Some(AspectRatio {
                    width: 16,
                    height: 9
                })
            ))
        );
        fs::remove_file(store.path).unwrap();
    }

    #[test]
    fn invalid_display_override_is_preserved_and_reported() {
        let store = test_override_store("invalid");
        fs::write(
            &store.path,
            br#"{"version":1,"displays":[{"display_id":"x","diagonal_inches":0}]}"#,
        )
        .unwrap();
        let before = fs::read(&store.path).unwrap();
        let outcome = store.load().unwrap();
        assert_eq!(outcome.overrides, DisplayOverrides::default());
        assert!(outcome.warning.is_some());
        assert_eq!(fs::read(&store.path).unwrap(), before);
        fs::remove_file(store.path).unwrap();
    }

    #[test]
    fn display_override_upsert_and_remove_are_deterministic() {
        let mut overrides = DisplayOverrides::default();
        overrides.upsert("B".into(), 27.0, None).unwrap();
        overrides.upsert("a".into(), 24.0, None).unwrap();
        overrides.upsert("A".into(), 25.0, None).unwrap();
        assert_eq!(overrides.displays.len(), 2);
        assert_eq!(overrides.displays[0].display_id, "A");
        assert_eq!(
            overrides
                .override_for("a")
                .map(|entry| entry.diagonal_inches),
            Some(25.0)
        );
        assert!(overrides.remove("b"));
        assert!(!overrides.remove("missing"));
    }

    #[test]
    fn legacy_display_override_without_aspect_ratio_keeps_native_behavior() {
        let store = test_override_store("legacy-aspect");
        fs::write(
            &store.path,
            br#"{"version":1,"displays":[{"display_id":"legacy","diagonal_inches":24.0}]}"#,
        )
        .unwrap();
        let outcome = store.load().unwrap();
        let display_override = outcome.overrides.override_for("legacy").unwrap();
        assert_eq!(display_override.diagonal_inches, 24.0);
        assert_eq!(display_override.aspect_ratio, None);
        fs::remove_file(store.path).unwrap();
    }

    #[test]
    fn zero_display_override_aspect_ratio_is_preserved_and_reported() {
        let store = test_override_store("zero-aspect");
        fs::write(
            &store.path,
            br#"{"version":1,"displays":[{"display_id":"x","diagonal_inches":24.0,"aspect_ratio":{"width":0,"height":9}}]}"#,
        )
        .unwrap();
        let before = fs::read(&store.path).unwrap();
        let outcome = store.load().unwrap();
        assert!(outcome.warning.is_some());
        assert_eq!(fs::read(&store.path).unwrap(), before);
        fs::remove_file(store.path).unwrap();
    }

    #[test]
    fn group_lookup_and_mutation_preserve_collection_state() {
        let mut config = AppConfig {
            version: CONFIG_VERSION,
            groups: vec![
                GroupConfig::default(),
                GroupConfig {
                    id: 3,
                    route: GroupRouteConfig::StreamOnly {
                        screen: StreamScreenConfig {
                            width: 1920,
                            height: 1080,
                            diagonal_inches: None,
                            refresh_millihz: 60_000,
                            aspect_ratio: AspectRatio {
                                width: 16,
                                height: 9,
                            },
                            rotation: Rotation::Deg0,
                        },
                    },
                    reference_source: None,
                    sizing: None,
                },
            ],
            selected_group_id: Some(3),
        };
        let other_group = config.group(3).unwrap().clone();

        config.group_mut(0).unwrap().route = GroupRouteConfig::Mirror {
            target_id: Some("replacement-target".into()),
        };

        assert_eq!(config.groups.len(), 2);
        assert_eq!(config.groups[0].id, 0);
        assert_eq!(config.groups[1], other_group);
        assert_eq!(config.selected_group_id, Some(3));
        assert!(config.group(99).is_none());
        assert!(config.group_mut(99).is_none());
    }

    #[test]
    fn failed_atomic_save_removes_temporary_file() {
        let root = env::temp_dir().join(format!(
            "sbms-config-atomic-failure-{}-{}",
            std::process::id(),
            TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        ));
        let destination = root.join(CONFIG_FILE);
        fs::create_dir_all(&destination).unwrap();
        let store = ConfigStore::new(destination);

        assert!(store.save(&AppConfig::default()).is_err());
        let temporary_prefix = format!(".{CONFIG_FILE}.");
        assert!(
            fs::read_dir(&root)
                .unwrap()
                .filter_map(Result::ok)
                .all(|entry| !entry
                    .file_name()
                    .to_string_lossy()
                    .starts_with(&temporary_prefix))
        );

        fs::remove_dir_all(root).unwrap();
    }

    fn test_profile_store(name: &str) -> (PathBuf, ConfigProfileStore) {
        let root = env::temp_dir().join(format!(
            "sbms-config-profiles-{name}-{}-{}",
            std::process::id(),
            TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        ));
        let _ = fs::remove_dir_all(&root);
        let store =
            ConfigProfileStore::new(root.join(CONFIG_PROFILES_FILE), root.join(CONFIG_FILE));
        (root, store)
    }

    fn config_with_target(target: &str) -> AppConfig {
        let mut config = AppConfig::default();
        config.group_mut(0).unwrap().route = GroupRouteConfig::Mirror {
            target_id: Some(target.into()),
        };
        config
    }

    #[test]
    fn profile_store_migrates_existing_config_without_removing_it() {
        let (root, profiles) = test_profile_store("migration");
        let legacy = ConfigStore::new(root.join(CONFIG_FILE));
        let expected = config_with_target("legacy-target");
        legacy.save(&expected).unwrap();
        let original = fs::read(legacy.path()).unwrap();

        let snapshot = profiles.load_active().unwrap();

        assert_eq!(snapshot.profile.id, "default");
        assert_eq!(snapshot.profile.revision, 1);
        assert_eq!(snapshot.config, expected);
        assert_eq!(fs::read(legacy.path()).unwrap(), original);
        assert!(profiles.path().is_file());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn launch_intro_preference_is_global_and_does_not_touch_profile_revision() {
        let (root, profiles) = test_profile_store("launch-intro-preference");
        let before = profiles.load_active().unwrap();
        assert!(!profiles.list().unwrap().launch_intro_disabled);

        profiles.set_launch_intro_disabled(true).unwrap();

        let stored = profiles.list().unwrap();
        let after = profiles.load_active().unwrap();
        assert!(stored.launch_intro_disabled);
        assert_eq!(after, before);

        profiles.set_launch_intro_disabled(false).unwrap();
        assert!(!profiles.list().unwrap().launch_intro_disabled);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn profile_store_enforces_three_profile_limit() {
        let (root, profiles) = test_profile_store("limit");
        profiles.load_active().unwrap();
        profiles
            .save_profile("second", &config_with_target("second"), false, false)
            .unwrap();
        profiles
            .save_profile("third", &config_with_target("third"), false, false)
            .unwrap();

        let error = profiles
            .save_profile("fourth", &AppConfig::default(), false, false)
            .unwrap_err();

        assert!(error.to_string().contains("3-profile limit"));
        assert_eq!(profiles.list().unwrap().profiles.len(), 3);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn profile_activation_persists_and_active_profile_cannot_be_deleted() {
        let (root, profiles) = test_profile_store("activate");
        profiles.load_active().unwrap();
        profiles
            .save_profile("gaming", &config_with_target("gaming"), false, false)
            .unwrap();

        let active = profiles.activate("gaming").unwrap();

        assert_eq!(active.profile.id, "gaming");
        assert_eq!(profiles.load_active().unwrap().config, active.config);
        assert!(profiles.delete("gaming").is_err());
        profiles.delete("default").unwrap();
        assert_eq!(profiles.list().unwrap().profiles.len(), 1);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn stale_profile_revision_cannot_overwrite_reloaded_config() {
        let (root, profiles) = test_profile_store("revision");
        let stale = profiles.load_active().unwrap();
        let updated = profiles
            .update_active(|config| {
                *config = config_with_target("new-target");
                Ok(())
            })
            .unwrap();

        let error = profiles
            .save_active_if_revision(&stale.profile, &config_with_target("stale-target"))
            .unwrap_err();

        assert!(error.to_string().contains("changed from revision"));
        assert_eq!(profiles.load_active().unwrap(), updated);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn profile_ids_are_bounded_and_case_insensitively_unique() {
        let (root, profiles) = test_profile_store("ids");
        profiles.load_active().unwrap();
        assert!(
            profiles
                .save_profile("bad name", &AppConfig::default(), false, false)
                .is_err()
        );
        assert!(
            profiles
                .save_profile("DEFAULT", &AppConfig::default(), false, false)
                .is_err()
        );
        fs::remove_dir_all(root).unwrap();
    }
}
