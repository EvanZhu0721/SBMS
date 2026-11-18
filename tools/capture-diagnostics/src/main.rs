mod capture;
mod hybrid;
mod protocol;
mod session;
mod topology;
mod wgc;

use serde_json::{Value, json};
use std::path::Path;
use windows::Win32::UI::HiDpi::{
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetThreadDpiAwarenessContext,
};

type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 2 || args.iter().any(|s| s == "--help" || s == "-h") {
        println!(
            "SBMS isolated capture diagnostics\n  inventory [--native-output-routing] --report FILE\n  probe [--native-output-routing] --config FILE --report FILE\n  session --config FILE --report-dir DIR\n  restore --snapshot FILE --report FILE\nAll rectangles use [left, top, right, bottom]. No SBMS GUI is initialized."
        );
        return;
    }
    if let Err(error) = run(&args) {
        let value = json!({"status":"error", "error":error.to_string(), "pid":std::process::id()});
        eprintln!("{value}");
        if let Ok(path) = argument(&args, "--report")
            && !Path::new(path).exists()
        {
            let _ = write_json(Path::new(path), &value);
        }
        std::process::exit(1);
    }
}

fn run(args: &[String]) -> Result<()> {
    if matches!(args[1].as_str(), "inventory" | "probe") {
        // DXGI GetDesc is DPI-virtualized otherwise; CCD/GDI and the stimulus
        // window use physical pixels. Keep all reported rectangles in that space.
        if unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) }
            .0
            .is_null()
        {
            return Err(format!(
                "set probe DPI awareness: {}",
                windows::core::Error::from_thread()
            )
            .into());
        }
    }
    let native_routing = args.iter().any(|arg| arg == "--native-output-routing");
    if native_routing {
        if !matches!(args[1].as_str(), "inventory" | "probe") {
            return Err("native output routing is only supported for inventory/probe".into());
        }
        hybrid::install()?;
    }
    match args[1].as_str() {
        "inventory" => {
            let mut inventory = capture::inventory();
            match topology::Topology::query(false).and_then(|topology| topology.displays()) {
                Ok(paths) => inventory["display_paths"] = json!(paths),
                Err(error) => inventory["display_paths_error"] = json!(error.to_string()),
            }
            inventory["routing"] = hybrid::report(native_routing);
            inventory["coordinate_space"] = json!("physical-pixels");
            write_json(Path::new(argument(args, "--report")?), &inventory)
        }
        "probe" => {
            let config = read_json(Path::new(argument(args, "--config")?))?;
            if let Some(identity) = config.get("source_identity") {
                let low: u32 = identity[0]
                    .as_u64()
                    .ok_or("invalid source_identity low")?
                    .try_into()?;
                let high: i32 = identity[1]
                    .as_i64()
                    .ok_or("invalid source_identity high")?
                    .try_into()?;
                let target: u32 = identity[2]
                    .as_u64()
                    .ok_or("invalid source_identity target")?
                    .try_into()?;
                let source = topology::Topology::query(false)?
                    .find((low, high, target))?
                    .ok_or("expected virtual source is no longer active")?;
                if source["source_device"] != config["source_device"]
                    || source["monitor_device_path"] != config["source_monitor_device_path"]
                {
                    return Err(
                        "source identity does not match the requested device and monitor path"
                            .into(),
                    );
                }
            }
            // This branch deliberately performs no GUI, D3D or WinRT initialization.
            // The worker records every initialization step itself, after process start.
            let mut value = capture::probe(&config);
            value["routing"] = hybrid::report(native_routing);
            value["coordinate_space"] = json!("physical-pixels");
            write_json(Path::new(argument(args, "--report")?), &value)
        }
        "session" => {
            let config = read_json(Path::new(argument(args, "--config")?))?;
            session::run(&config, Path::new(argument(args, "--report-dir")?))
        }
        "restore" => {
            let snapshot = read_json(Path::new(argument(args, "--snapshot")?))?;
            let result = topology::restore(&snapshot)?;
            write_json(Path::new(argument(args, "--report")?), &result)?;
            if result["verified"] != true {
                return Err("topology restore verification failed".into());
            }
            Ok(())
        }
        command => Err(format!("unknown command: {command}").into()),
    }
}

fn argument<'a>(args: &'a [String], name: &str) -> Result<&'a str> {
    args.iter()
        .position(|arg| arg == name)
        .and_then(|i| args.get(i + 1))
        .map(String::as_str)
        .ok_or_else(|| format!("missing {name}").into())
}

fn read_json(path: &Path) -> Result<Value> {
    let text = std::fs::read_to_string(path)?;
    Ok(serde_json::from_str(text.trim_start_matches('\u{feff}'))?)
}

pub(crate) fn write_json(path: &Path, value: &Value) -> Result<()> {
    if let Some(parent) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
        std::fs::create_dir_all(parent)?;
    }
    // Atomic publication avoids the polling PowerShell parent seeing a partial ready file.
    let temporary = path.with_extension("json.tmp");
    std::fs::write(&temporary, serde_json::to_vec_pretty(value)?)?;
    std::fs::rename(temporary, path)?;
    Ok(())
}
