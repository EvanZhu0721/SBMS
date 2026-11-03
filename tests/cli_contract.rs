use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Output};
use std::sync::atomic::{AtomicU64, Ordering};

use serde_json::{Value, json};

static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

struct TestRoot(PathBuf);

impl TestRoot {
    fn new(name: &str) -> Self {
        let path = std::env::temp_dir().join(format!(
            "sbms-cli-contract-{}-{}-{name}",
            std::process::id(),
            TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir_all(&path).unwrap();
        Self(path)
    }

    fn path(&self) -> &Path {
        &self.0
    }

    fn config_path(&self) -> PathBuf {
        self.0.join("SBMS").join("config-v2.json")
    }

    fn profiles_path(&self) -> PathBuf {
        self.0.join("SBMS").join("config-profiles-v1.json")
    }
}

impl Drop for TestRoot {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn run(root: &TestRoot, arguments: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_sbms"))
        .args(arguments)
        .env("LOCALAPPDATA", root.path())
        .output()
        .unwrap()
}

fn text(bytes: &[u8]) -> String {
    String::from_utf8_lossy(bytes).replace("\r\n", "\n")
}

fn usage() -> &'static str {
    "usage:
  sbms --version
  sbms list
  sbms map [--target <monitor-device-path>] [--hold-ms <milliseconds>]
  sbms plan validate <plan.json>
  sbms plan run <plan.json> [--hold-ms <milliseconds>]
  sbms config path|list|show [<profile>]
  sbms config save <profile>
  sbms config import <profile> <file.json> [--replace] [--activate]
  sbms config export <profile> <file.json> [--force]
  sbms config activate|delete <profile>
  sbms config reload
  sbms config set-target <monitor-device-path>|clear-target|reset
  sbms config reset --all --backup
  sbms shutdown
  sbms ui
"
}

#[test]
fn cli_version_and_usage_have_stable_output_and_exit_codes() {
    let root = TestRoot::new("version");
    let output = run(&root, &["--version"]);

    assert!(output.status.success());
    assert_eq!(
        text(&output.stdout),
        format!("sbms {}\n", env!("CARGO_PKG_VERSION"))
    );
    assert_eq!(text(&output.stderr), "");

    for arguments in [&[][..], &["unknown"], &["--version", "extra"]] {
        let root = TestRoot::new("usage");
        let output = run(&root, arguments);

        assert_eq!(output.status.code(), Some(2));
        assert_eq!(text(&output.stdout), "");
        assert_eq!(text(&output.stderr), usage());
    }
}

#[test]
fn config_path_and_missing_config_show_initializes_default_profile() {
    let root = TestRoot::new("config-defaults");
    let path_output = run(&root, &["config", "path"]);

    assert!(path_output.status.success());
    assert_eq!(
        text(&path_output.stdout).trim_end(),
        root.profiles_path().display().to_string()
    );

    let show_output = run(&root, &["config", "show"]);
    assert!(show_output.status.success());
    let shown: Value = serde_json::from_slice(&show_output.stdout).unwrap();
    assert_eq!(shown["version"], 2);
    assert_eq!(shown["groups"].as_array().unwrap().len(), 1);
    assert_eq!(shown["groups"][0]["id"], 0);
    assert_eq!(shown["selected_group_id"], 0);
    assert!(root.profiles_path().exists());
    assert!(!root.config_path().exists());
}

#[test]
fn clear_target_changes_only_the_first_output_and_preserves_invalid_input() {
    let root = TestRoot::new("clear-target");
    let config_path = root.config_path();
    fs::create_dir_all(config_path.parent().unwrap()).unwrap();
    let original = json!({
        "version": 2,
        "groups": [
            {
                "id": 3,
                "route": {
                    "kind": "mirror",
                    "target_id": "stable-display-id"
                }
            },
            {
                "id": 5,
                "route": {
                    "kind": "stream_only",
                    "screen": {
                        "width": 1920,
                        "height": 1080,
                        "refresh_millihz": 60000,
                        "aspect_ratio": {"width": 16, "height": 9},
                        "rotation": "deg0"
                    }
                }
            },
            {
                "id": 7,
                "route": {
                    "kind": "mirror"
                }
            }
        ],
        "selected_group_id": 3
    });
    fs::write(&config_path, serde_json::to_vec_pretty(&original).unwrap()).unwrap();

    let output = run(&root, &["config", "clear-target"]);

    assert!(output.status.success(), "{}", text(&output.stderr));
    assert!(text(&output.stdout).starts_with("saved=default revision=2 reload="));
    let profiles: Value = serde_json::from_slice(&fs::read(root.profiles_path()).unwrap()).unwrap();
    let saved = &profiles["profiles"][0]["config"];
    assert_eq!(saved["groups"][0]["id"], 3);
    assert_eq!(saved["groups"][0]["route"], json!({"kind": "mirror"}));
    assert_eq!(saved["groups"][1], original["groups"][1]);
    assert_eq!(saved["groups"][2], original["groups"][2]);
    assert_eq!(saved["selected_group_id"], 3);

    let root = TestRoot::new("bad-config");
    let config_path = root.config_path();
    fs::create_dir_all(config_path.parent().unwrap()).unwrap();
    let original = b"{not json";
    fs::write(&config_path, original).unwrap();

    let output = run(&root, &["config", "clear-target"]);

    assert_eq!(output.status.code(), Some(1));
    assert!(text(&output.stderr).contains("cannot initialize config profiles"));
    assert_eq!(fs::read(config_path).unwrap(), original);

    let reset = run(&root, &["config", "reset"]);
    assert!(reset.status.success(), "{}", text(&reset.stderr));
    assert!(text(&reset.stdout).starts_with("reset=default revision=2 reload="));
    assert_eq!(fs::read(root.config_path()).unwrap(), original);
    let shown = run(&root, &["config", "show"]);
    let config: Value = serde_json::from_slice(&shown.stdout).unwrap();
    assert_eq!(config["version"], 2);
    assert_eq!(config["groups"].as_array().unwrap().len(), 1);
}

#[test]
fn config_profiles_import_activate_export_and_enforce_limit() {
    let root = TestRoot::new("profiles");
    let imported_path = root.path().join("Gaming 配置.json");
    let imported = json!({
        "version": 2,
        "groups": [{
            "id": 0,
            "route": {"kind": "mirror", "target_id": "gaming-display"}
        }],
        "selected_group_id": 0
    });
    fs::write(
        &imported_path,
        serde_json::to_vec_pretty(&imported).unwrap(),
    )
    .unwrap();

    let imported_output = run(
        &root,
        &[
            "config",
            "import",
            "gaming",
            imported_path.to_str().unwrap(),
            "--activate",
        ],
    );
    assert!(
        imported_output.status.success(),
        "{}",
        text(&imported_output.stderr)
    );
    assert!(text(&imported_output.stdout).contains("imported=gaming"));

    let shown = run(&root, &["config", "show"]);
    assert_eq!(
        serde_json::from_slice::<Value>(&shown.stdout).unwrap(),
        imported
    );

    let exported_path = root.path().join("exported.json");
    let exported = run(
        &root,
        &[
            "config",
            "export",
            "gaming",
            exported_path.to_str().unwrap(),
        ],
    );
    assert!(exported.status.success(), "{}", text(&exported.stderr));
    assert_eq!(
        serde_json::from_slice::<Value>(&fs::read(exported_path).unwrap()).unwrap(),
        imported
    );

    assert!(run(&root, &["config", "save", "third"]).status.success());
    let fourth = run(&root, &["config", "save", "fourth"]);
    assert_eq!(fourth.status.code(), Some(1));
    assert!(text(&fourth.stderr).contains("3-profile limit"));

    let listed = run(&root, &["config", "list"]);
    let profiles: Value = serde_json::from_slice(&listed.stdout).unwrap();
    assert_eq!(profiles.as_array().unwrap().len(), 3);
    assert!(
        profiles
            .as_array()
            .unwrap()
            .iter()
            .any(|profile| { profile["id"] == "gaming" && profile["active"] == true })
    );
}

#[test]
fn config_export_never_overwrites_store_or_existing_destination_without_force() {
    let root = TestRoot::new("safe-export");
    let initialized = run(&root, &["config", "show"]);
    assert!(initialized.status.success());
    let store_bytes = fs::read(root.profiles_path()).unwrap();

    let self_export = run(
        &root,
        &[
            "config",
            "export",
            "default",
            root.profiles_path().to_str().unwrap(),
            "--force",
        ],
    );
    assert_eq!(self_export.status.code(), Some(1));
    assert!(text(&self_export.stderr).contains("config profile store"));
    assert_eq!(fs::read(root.profiles_path()).unwrap(), store_bytes);

    let destination = root.path().join("existing.json");
    fs::write(&destination, b"keep-me").unwrap();
    let refused = run(
        &root,
        &["config", "export", "default", destination.to_str().unwrap()],
    );
    assert_eq!(refused.status.code(), Some(1));
    assert_eq!(fs::read(destination).unwrap(), b"keep-me");
}

fn recovery_backups(root: &TestRoot) -> Vec<PathBuf> {
    let profiles = root.profiles_path();
    let directory = profiles.parent().unwrap();
    let mut paths: Vec<_> = fs::read_dir(directory)
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .filter(|path| {
            path.file_name()
                .and_then(|name| name.to_str())
                .is_some_and(|name| name.contains(".recovery-") && name.ends_with(".bak"))
        })
        .collect();
    paths.sort();
    paths
}

fn write_profile_neighbors(root: &TestRoot) -> (PathBuf, Vec<u8>, PathBuf, Vec<u8>) {
    let profiles = root.profiles_path();
    let directory = profiles.parent().unwrap();
    fs::create_dir_all(directory).unwrap();
    let legacy = root.config_path();
    let legacy_bytes = b"legacy-source".to_vec();
    fs::write(&legacy, &legacy_bytes).unwrap();
    let overrides = directory.join("display-overrides-v1.json");
    let override_bytes = br#"{"version":1,"displays":[]}"#.to_vec();
    fs::write(&overrides, &override_bytes).unwrap();
    let credentials = directory.join("credentials.json");
    fs::write(&credentials, b"stream-secret").unwrap();
    let log = directory.join("sbms.log");
    fs::write(&log, b"log-bytes").unwrap();
    (legacy, legacy_bytes, overrides, override_bytes)
}

#[test]
fn config_reset_rejects_incomplete_recovery_arguments_without_writing() {
    let root = TestRoot::new("reset-args");
    let profiles = root.profiles_path();
    fs::create_dir_all(profiles.parent().unwrap()).unwrap();
    let original = b"{not json".to_vec();
    fs::write(&profiles, &original).unwrap();
    let (legacy, legacy_bytes, overrides, override_bytes) = write_profile_neighbors(&root);

    for arguments in [
        &["config", "reset", "--all"][..],
        &["config", "reset", "--backup"][..],
        &["config", "reset", "--all", "--backup", "extra"][..],
        &["config", "reset", "--backup", "--all", "--force"][..],
        &["config", "reset", "--all", "--all"][..],
        &["config", "reset", "--backup", "--backup"][..],
    ] {
        let output = run(&root, arguments);
        assert_eq!(
            output.status.code(),
            Some(2),
            "{arguments:?}\n{}",
            text(&output.stderr)
        );
        assert_eq!(text(&output.stdout), "");
        assert_eq!(text(&output.stderr), usage());
        assert_eq!(fs::read(&profiles).unwrap(), original, "{arguments:?}");
        assert_eq!(fs::read(&legacy).unwrap(), legacy_bytes, "{arguments:?}");
        assert_eq!(
            fs::read(&overrides).unwrap(),
            override_bytes,
            "{arguments:?}"
        );
        assert_eq!(
            fs::read(profiles.parent().unwrap().join("credentials.json")).unwrap(),
            b"stream-secret"
        );
        assert_eq!(
            fs::read(profiles.parent().unwrap().join("sbms.log")).unwrap(),
            b"log-bytes"
        );
        assert!(recovery_backups(&root).is_empty(), "{arguments:?}");
    }
}

#[test]
fn config_reset_all_backup_restores_damaged_bytes_and_can_save_again() {
    let root = TestRoot::new("recover-store");
    let profiles = root.profiles_path();
    fs::create_dir_all(profiles.parent().unwrap()).unwrap();
    let original = b"\xff\xfe{not json".to_vec();
    fs::write(&profiles, &original).unwrap();
    let (legacy, legacy_bytes, overrides, override_bytes) = write_profile_neighbors(&root);

    let recovered = run(&root, &["config", "reset", "--backup", "--all"]);
    assert!(recovered.status.success(), "{}", text(&recovered.stderr));
    let stdout = text(&recovered.stdout);
    assert!(stdout.contains("recovered=default"), "{stdout}");
    assert!(stdout.contains("revision=1"), "{stdout}");
    assert!(stdout.contains("backup="), "{stdout}");
    assert!(stdout.contains("reload="), "{stdout}");
    assert!(!stdout.contains("reload=failed"), "{stdout}");
    let backups = recovery_backups(&root);
    assert_eq!(backups.len(), 1);
    assert_eq!(fs::read(&backups[0]).unwrap(), original);
    let backup_name = backups[0]
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap();
    assert!(stdout.contains(backup_name), "{stdout}");
    assert_eq!(fs::read(&legacy).unwrap(), legacy_bytes);
    assert_eq!(fs::read(&overrides).unwrap(), override_bytes);
    assert_eq!(
        fs::read(profiles.parent().unwrap().join("credentials.json")).unwrap(),
        b"stream-secret"
    );
    assert_eq!(
        fs::read(profiles.parent().unwrap().join("sbms.log")).unwrap(),
        b"log-bytes"
    );

    let rebuilt = fs::read(&profiles).unwrap();
    let again = run(&root, &["config", "reset", "--all", "--backup"]);
    assert!(again.status.success(), "{}", text(&again.stderr));
    let backups = recovery_backups(&root);
    assert_eq!(backups.len(), 2);
    let backup_bytes: Vec<Vec<u8>> = backups.iter().map(|path| fs::read(path).unwrap()).collect();
    assert!(backup_bytes.iter().any(|bytes| bytes == &original));
    assert!(backup_bytes.iter().any(|bytes| bytes == &rebuilt));

    let shown = run(&root, &["config", "show"]);
    assert!(shown.status.success(), "{}", text(&shown.stderr));
    let config: Value = serde_json::from_slice(&shown.stdout).unwrap();
    assert_eq!(config["version"], 2);
    assert_eq!(config["groups"][0]["id"], 0);

    let saved = run(&root, &["config", "save", "edited"]);
    assert!(saved.status.success(), "{}", text(&saved.stderr));
    let listed = run(&root, &["config", "list"]);
    assert!(listed.status.success(), "{}", text(&listed.stderr));
    let profiles_json: Value = serde_json::from_slice(&listed.stdout).unwrap();
    assert_eq!(profiles_json.as_array().unwrap().len(), 2);
    assert_eq!(fs::read(&legacy).unwrap(), legacy_bytes);
    assert_eq!(fs::read(&overrides).unwrap(), override_bytes);
}

#[test]
fn config_reset_all_backup_refuses_a_future_schema_without_writing() {
    let root = TestRoot::new("future-store");
    let profiles = root.profiles_path();
    fs::create_dir_all(profiles.parent().unwrap()).unwrap();
    let original = br#"{"version":99,"active_profile":"default","profiles":[]}"#.to_vec();
    fs::write(&profiles, &original).unwrap();
    let (legacy, legacy_bytes, overrides, override_bytes) = write_profile_neighbors(&root);

    let output = run(&root, &["config", "reset", "--all", "--backup"]);
    assert_eq!(output.status.code(), Some(1), "{}", text(&output.stdout));
    let stderr = text(&output.stderr);
    assert!(stderr.contains("newer than this SBMS"), "{stderr}");
    assert!(stderr.contains("left unchanged"), "{stderr}");
    assert!(
        !stderr.contains("configuration recovery was committed"),
        "{stderr}"
    );
    assert!(
        !stderr.contains("sbms config reset --all --backup"),
        "{stderr}"
    );
    assert_eq!(text(&output.stdout), "");
    assert_eq!(fs::read(&profiles).unwrap(), original);
    assert!(recovery_backups(&root).is_empty());
    assert_eq!(fs::read(&legacy).unwrap(), legacy_bytes);
    assert_eq!(fs::read(&overrides).unwrap(), override_bytes);
}

#[test]
fn config_reset_changes_only_the_active_profile() {
    let root = TestRoot::new("reset-active");
    let office_path = root.path().join("office.json");
    let gaming_path = root.path().join("gaming.json");
    let office = json!({
        "version": 2,
        "groups": [{
            "id": 0,
            "route": {"kind": "mirror", "target_id": "office-display"}
        }],
        "selected_group_id": 0
    });
    let gaming = json!({
        "version": 2,
        "groups": [{
            "id": 0,
            "route": {"kind": "mirror", "target_id": "gaming-display"}
        }],
        "selected_group_id": 0
    });
    fs::write(&office_path, serde_json::to_vec_pretty(&office).unwrap()).unwrap();
    fs::write(&gaming_path, serde_json::to_vec_pretty(&gaming).unwrap()).unwrap();
    assert!(run(&root, &["config", "show"]).status.success());
    let replace_default = run(
        &root,
        &[
            "config",
            "import",
            "default",
            office_path.to_str().unwrap(),
            "--replace",
        ],
    );
    assert!(
        replace_default.status.success(),
        "{}",
        text(&replace_default.stderr)
    );
    let activate_gaming = run(
        &root,
        &[
            "config",
            "import",
            "gaming",
            gaming_path.to_str().unwrap(),
            "--activate",
        ],
    );
    assert!(
        activate_gaming.status.success(),
        "{}",
        text(&activate_gaming.stderr)
    );

    let reset = run(&root, &["config", "reset"]);
    assert!(reset.status.success(), "{}", text(&reset.stderr));
    assert!(text(&reset.stdout).starts_with("reset=gaming revision=2 reload="));
    assert!(recovery_backups(&root).is_empty());

    let active = run(&root, &["config", "show"]);
    let active_config: Value = serde_json::from_slice(&active.stdout).unwrap();
    assert_eq!(
        active_config["groups"][0]["route"],
        json!({"kind": "mirror"})
    );
    let preserved = run(&root, &["config", "show", "default"]);
    assert_eq!(
        serde_json::from_slice::<Value>(&preserved.stdout).unwrap(),
        office
    );
    let listed = serde_json::from_slice::<Value>(&run(&root, &["config", "list"]).stdout).unwrap();
    assert_eq!(listed.as_array().unwrap().len(), 2);
    assert!(
        listed
            .as_array()
            .unwrap()
            .iter()
            .any(|profile| { profile["id"] == "gaming" && profile["active"] == true })
    );
}

#[test]
fn plan_validation_reports_valid_and_empty_plans() {
    let root = TestRoot::new("plan");
    let valid_path = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("examples")
        .join("portrait-stream.json");
    let valid = run(&root, &["plan", "validate", valid_path.to_str().unwrap()]);

    assert!(valid.status.success(), "{}", text(&valid.stderr));
    assert_eq!(text(&valid.stdout), "valid_groups=1\n");

    let empty_path = root.path().join("empty-plan.json");
    fs::write(&empty_path, br#"{"groups":[]}"#).unwrap();
    let empty = run(&root, &["plan", "validate", empty_path.to_str().unwrap()]);

    assert_eq!(empty.status.code(), Some(1));
    assert!(text(&empty.stderr).contains("at least one mapping group is required"));
}
