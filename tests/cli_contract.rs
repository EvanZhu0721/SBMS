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
  sbms config path|show|set-target <monitor-device-path>|clear-target|reset
  sbms shutdown
  sbms ui
"
}

#[test]
fn version_is_machine_readable() {
    let root = TestRoot::new("version");
    let output = run(&root, &["--version"]);

    assert!(output.status.success());
    assert_eq!(
        text(&output.stdout),
        format!("sbms {}\n", env!("CARGO_PKG_VERSION"))
    );
    assert_eq!(text(&output.stderr), "");
}

#[test]
fn invalid_invocations_keep_the_usage_contract() {
    for arguments in [&[][..], &["unknown"], &["--version", "extra"]] {
        let root = TestRoot::new("usage");
        let output = run(&root, arguments);

        assert_eq!(output.status.code(), Some(2));
        assert_eq!(text(&output.stdout), "");
        assert_eq!(text(&output.stderr), usage());
    }
}

#[test]
fn config_path_and_missing_config_show_defaults_without_persisting() {
    let root = TestRoot::new("config-defaults");
    let path_output = run(&root, &["config", "path"]);

    assert!(path_output.status.success());
    assert_eq!(
        text(&path_output.stdout).trim_end(),
        root.config_path().display().to_string()
    );

    let show_output = run(&root, &["config", "show"]);
    assert!(show_output.status.success());
    let shown: Value = serde_json::from_slice(&show_output.stdout).unwrap();
    assert_eq!(shown["version"], 2);
    assert_eq!(shown["groups"].as_array().unwrap().len(), 1);
    assert_eq!(shown["groups"][0]["id"], 0);
    assert_eq!(shown["selected_group_id"], 0);
    assert!(!root.config_path().exists());
}

#[test]
fn clear_target_only_changes_output_one() {
    let root = TestRoot::new("clear-target");
    let config_path = root.config_path();
    fs::create_dir_all(config_path.parent().unwrap()).unwrap();
    let original = json!({
        "version": 2,
        "groups": [
            {
                "id": 3,
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
                "id": 0,
                "route": {
                    "kind": "mirror",
                    "target_id": "stable-display-id"
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
    assert_eq!(
        text(&output.stdout),
        format!("saved={}\n", config_path.display())
    );
    let saved: Value = serde_json::from_slice(&fs::read(&config_path).unwrap()).unwrap();
    assert_eq!(saved["groups"][0], original["groups"][0]);
    assert_eq!(saved["groups"][0]["id"], 3);
    assert_eq!(saved["groups"][1]["id"], 0);
    assert_eq!(saved["groups"][1]["route"], json!({"kind": "mirror"}));
    assert_eq!(saved["groups"][2], original["groups"][2]);
    assert_eq!(saved["selected_group_id"], 3);
}

#[test]
fn clear_target_preserves_malformed_config_bytes() {
    let root = TestRoot::new("bad-config");
    let config_path = root.config_path();
    fs::create_dir_all(config_path.parent().unwrap()).unwrap();
    let original = b"{not json";
    fs::write(&config_path, original).unwrap();

    let output = run(&root, &["config", "clear-target"]);

    assert_eq!(output.status.code(), Some(1));
    assert!(text(&output.stderr).contains("run `sbms config reset`"));
    assert_eq!(fs::read(config_path).unwrap(), original);
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
