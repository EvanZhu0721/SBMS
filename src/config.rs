use std::env;
use std::error::Error;
use std::ffi::OsStr;
use std::fmt::{Display, Formatter};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use serde::{Deserialize, Serialize};
use windows::Win32::Storage::FileSystem::{
    MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW, REPLACEFILE_WRITE_THROUGH,
    ReplaceFileW,
};
use windows::core::PCWSTR;

use crate::geometry::SizingRequest;

const CONFIG_VERSION: u32 = 1;
const CONFIG_DIRECTORY: &str = "SBMS";
const CONFIG_FILE: &str = "config-v1.json";
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
    #[serde(default)]
    pub target_id: Option<String>,
    #[serde(default)]
    pub reference_source: Option<ReferenceSource>,
    #[serde(default)]
    pub sizing: Option<SizingRequest>,
}

#[derive(Clone, Debug)]
pub struct ConfigStore {
    path: PathBuf,
}

#[derive(Clone, Debug, PartialEq)]
pub struct LoadOutcome {
    pub config: AppConfig,
    pub warning: Option<String>,
}

#[derive(Debug)]
pub struct ConfigError(String);

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
            target_id: None,
            reference_source: None,
            sizing: None,
        }
    }
}

impl ConfigStore {
    pub fn default_path() -> Result<PathBuf, ConfigError> {
        let local_app_data = env::var_os("LOCALAPPDATA")
            .filter(|value| !value.is_empty())
            .ok_or_else(|| ConfigError("LOCALAPPDATA is not available".into()))?;
        Ok(PathBuf::from(local_app_data)
            .join(CONFIG_DIRECTORY)
            .join(CONFIG_FILE))
    }

    pub fn default_store() -> Result<Self, ConfigError> {
        Ok(Self::new(Self::default_path()?))
    }

    pub fn new(path: PathBuf) -> Self {
        Self { path }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn load(&self) -> Result<LoadOutcome, ConfigError> {
        let mut file = match File::open(&self.path) {
            Ok(file) => file,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(LoadOutcome {
                    config: AppConfig::default(),
                    warning: None,
                });
            }
            Err(error) => {
                return Err(ConfigError(format!(
                    "could not open {}: {error}",
                    self.path.display()
                )));
            }
        };
        let mut bytes = Vec::new();
        file.read_to_end(&mut bytes).map_err(|error| {
            ConfigError(format!("could not read {}: {error}", self.path.display()))
        })?;
        let config = match serde_json::from_slice::<AppConfig>(&bytes) {
            Ok(config) => config,
            Err(error) => {
                return Ok(LoadOutcome {
                    config: AppConfig::default(),
                    warning: Some(format!(
                        "{} is invalid and was left unchanged: {error}",
                        self.path.display()
                    )),
                });
            }
        };
        if config.version != CONFIG_VERSION {
            return Ok(LoadOutcome {
                config: AppConfig::default(),
                warning: Some(format!(
                    "{} uses unsupported config version {} and was left unchanged",
                    self.path.display(),
                    config.version
                )),
            });
        }
        if config
            .target_id
            .as_ref()
            .is_some_and(|target| target.trim().is_empty())
        {
            return Ok(LoadOutcome {
                config: AppConfig::default(),
                warning: Some(format!(
                    "{} contains an empty target id and was left unchanged",
                    self.path.display()
                )),
            });
        }
        if matches!(
            config.reference_source.as_ref(),
            Some(ReferenceSource::Display(id)) if id.trim().is_empty()
        ) {
            return Ok(LoadOutcome {
                config: AppConfig::default(),
                warning: Some(format!(
                    "{} contains an empty reference display id and was left unchanged",
                    self.path.display()
                )),
            });
        }
        if let Some(sizing) = config.sizing
            && let Err(error) = sizing.calculate()
        {
            return Ok(LoadOutcome {
                config: AppConfig::default(),
                warning: Some(format!(
                    "{} contains invalid sizing parameters and was left unchanged: {error}",
                    self.path.display()
                )),
            });
        }
        Ok(LoadOutcome {
            config,
            warning: None,
        })
    }

    pub fn save(&self, config: &AppConfig) -> Result<(), ConfigError> {
        if config.version != CONFIG_VERSION {
            return Err(ConfigError(format!(
                "refusing to save unsupported config version {}",
                config.version
            )));
        }
        if let Some(target) = &config.target_id
            && target.trim().is_empty()
        {
            return Err(ConfigError("target id cannot be empty".into()));
        }
        if matches!(
            config.reference_source.as_ref(),
            Some(ReferenceSource::Display(id)) if id.trim().is_empty()
        ) {
            return Err(ConfigError("reference display id cannot be empty".into()));
        }
        if let Some(sizing) = config.sizing {
            sizing
                .calculate()
                .map_err(|error| ConfigError(format!("invalid sizing parameters: {error}")))?;
        }

        let parent = self
            .path
            .parent()
            .ok_or_else(|| ConfigError("config path has no parent directory".into()))?;
        fs::create_dir_all(parent).map_err(|error| {
            ConfigError(format!("could not create {}: {error}", parent.display()))
        })?;
        let (temporary_path, mut temporary) = create_temporary(parent)?;
        let result = (|| {
            let bytes = serde_json::to_vec_pretty(config)
                .map_err(|error| ConfigError(format!("could not serialize config: {error}")))?;
            temporary
                .write_all(&bytes)
                .and_then(|_| temporary.write_all(b"\n"))
                .and_then(|_| temporary.sync_all())
                .map_err(|error| {
                    ConfigError(format!(
                        "could not write temporary config {}: {error}",
                        temporary_path.display()
                    ))
                })?;
            drop(temporary);
            atomic_replace(&temporary_path, &self.path)
        })();
        if result.is_err() {
            let _ = fs::remove_file(&temporary_path);
        }
        result
    }

    pub fn reset(&self) -> Result<(), ConfigError> {
        match fs::remove_file(&self.path) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(error) => Err(ConfigError(format!(
                "could not remove {}: {error}",
                self.path.display()
            ))),
        }
    }
}

fn create_temporary(parent: &Path) -> Result<(PathBuf, File), ConfigError> {
    for _ in 0..32 {
        let sequence = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let path = parent.join(format!(
            ".config-v1.json.{}.{}.tmp",
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

    fn test_store(name: &str) -> ConfigStore {
        let path = env::temp_dir().join(format!(
            "sbms-config-test-{}-{}-{}.json",
            std::process::id(),
            TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed),
            name
        ));
        ConfigStore::new(path)
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
            target_id: Some("stable-display-id".into()),
            reference_source: Some(ReferenceSource::Display("reference-display-id".into())),
            ..AppConfig::default()
        };
        store.save(&config).unwrap();
        assert_eq!(store.load().unwrap().config, config);

        config.target_id = Some("replacement-id".into());
        store.save(&config).unwrap();
        assert_eq!(store.load().unwrap().config, config);
        store.reset().unwrap();
    }

    #[test]
    fn empty_reference_display_id_is_rejected() {
        let store = test_store("empty-reference");
        let config = AppConfig {
            reference_source: Some(ReferenceSource::Display(" ".into())),
            ..AppConfig::default()
        };
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
        fs::write(
            store.path(),
            br#"{"version":2,"target_id":null,"sizing":null}"#,
        )
        .unwrap();
        let outcome = store.load().unwrap();
        assert_eq!(outcome.config, AppConfig::default());
        assert!(
            outcome
                .warning
                .unwrap()
                .contains("unsupported config version")
        );
        store.reset().unwrap();
    }
}
