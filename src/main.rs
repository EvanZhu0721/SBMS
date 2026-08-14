use std::error::Error;
use std::fmt::{Display, Formatter};
use std::fs;
use std::io;
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use sbms::config::{ConfigError, ConfigProfileStore, ConfigStore, GroupRouteConfig};
use sbms::diagnostics::{self, Level, MappingSessionId};
use sbms::display::{active_displays, unique_physical_display};
use sbms::geometry::Rotation;
use sbms::launch_broker::{self, LaunchDisposition};
use sbms::mapping::{
    MappingError, MappingEvent, MappingGroupRequest, MappingPlan, MappingRoute, MappingSession,
};
use sbms::renderer::RendererEvent;
use sbms::session_gate::VirtualMode;
use windows::Win32::UI::HiDpi::{
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetProcessDpiAwarenessContext,
};

#[derive(Debug)]
struct MapPreflightError {
    stage: &'static str,
    message: String,
}

impl MapPreflightError {
    fn new(stage: &'static str, error: impl Display) -> Self {
        Self {
            stage,
            message: error.to_string(),
        }
    }
}

impl Display for MapPreflightError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "{}: {}", self.stage, self.message)
    }
}

impl Error for MapPreflightError {}

fn main() -> Result<(), Box<dyn Error>> {
    if let Err(error) = diagnostics::init() {
        eprintln!("warning: diagnostics initialization failed: {error}");
    }
    diagnostics::log(Level::Info, "cli", "startup", None, "SBMS CLI started");
    let result = run();
    if let Err(error) = &result {
        let message = error.to_string();
        if !diagnostics::latest_error_matches(&message) {
            diagnostics::log(Level::Error, "cli", "command", None, message);
        }
    }
    result
}

fn run() -> Result<(), Box<dyn Error>> {
    unsafe { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) }?;
    let mut arguments = std::env::args().skip(1);
    match arguments.next().as_deref() {
        Some("--version") => version(arguments),
        Some("list") => list(arguments),
        Some("map") => map(arguments),
        Some("plan") => plan(arguments),
        Some("config") => config(arguments),
        Some("shutdown") => shutdown(arguments),
        Some("ui") if arguments.next().is_none() => match launch_broker::route_tray(true)? {
            LaunchDisposition::Exit => Ok(()),
            LaunchDisposition::RunHere(instance) => sbms::ui::run_host(instance, true),
        },
        _ => usage(),
    }
}

fn plan(mut arguments: impl Iterator<Item = String>) -> Result<(), Box<dyn Error>> {
    match arguments.next().as_deref() {
        Some("validate") => {
            let path = arguments
                .next()
                .ok_or("plan validate requires a JSON file")?;
            if arguments.next().is_some() {
                usage();
            }
            let plan = load_plan(&path)?;
            plan.validate()?;
            println!("valid_groups={}", plan.groups.len());
            Ok(())
        }
        Some("run") => {
            let path = arguments.next().ok_or("plan run requires a JSON file")?;
            let mut hold = None;
            while let Some(argument) = arguments.next() {
                match argument.as_str() {
                    "--hold-ms" if hold.is_none() => {
                        let milliseconds = arguments
                            .next()
                            .ok_or("--hold-ms requires a value")?
                            .parse::<u64>()?;
                        hold = Some(Duration::from_millis(milliseconds));
                    }
                    _ => usage(),
                }
            }
            let plan = load_plan(&path)?;
            let session_id = diagnostics::new_mapping_session_id();
            diagnostics::log(
                Level::Info,
                "mapping",
                "start",
                Some(session_id),
                format!("starting mapping plan with {} group(s)", plan.groups.len()),
            );
            let reporter = Arc::new(move |event| match event {
                MappingEvent::GroupReady(group) => println!(
                    "group_ready={} route={} device={} sunshine={} source={}",
                    group.id,
                    route_name(&group.route),
                    group.source_device_name,
                    group.sunshine_id.as_deref().unwrap_or("-"),
                    group.source_id
                ),
                MappingEvent::Renderer {
                    id,
                    event: RendererEvent::Fps(fps),
                } => println!("group={id} fps={fps}"),
                MappingEvent::Renderer {
                    id,
                    event: RendererEvent::Failed(error),
                } => {
                    diagnostics::log(
                        Level::Error,
                        "renderer",
                        "renderer",
                        Some(session_id),
                        format!("group={id}: {error}"),
                    );
                    eprintln!("group={id} renderer_error={error}");
                }
                MappingEvent::Renderer {
                    id,
                    event: RendererEvent::TopologyLost,
                } => {
                    diagnostics::log(
                        Level::Warn,
                        "renderer",
                        "topology-lost",
                        Some(session_id),
                        format!("group={id}: desktop duplication topology was lost"),
                    );
                    eprintln!("group={id} renderer_topology_lost");
                }
                MappingEvent::Topology { message } => {
                    diagnostics::log(
                        Level::Debug,
                        "mapping",
                        "topology",
                        Some(session_id),
                        message,
                    );
                }
            });
            let mut session = MappingSession::start_plan_with_reporter(plan, reporter)
                .map_err(|error| log_mapping_error(session_id, error))?;
            diagnostics::log(
                Level::Info,
                "mapping",
                "running",
                Some(session_id),
                format!("mapping active with {} group(s)", session.groups().len()),
            );
            println!("running_groups={}", session.groups().len());
            wait(hold)?;
            session
                .stop()
                .map_err(|error| log_mapping_error(session_id, error))?;
            diagnostics::log(
                Level::Info,
                "mapping",
                "stop",
                Some(session_id),
                "mapping stopped",
            );
            println!("stopped");
            Ok(())
        }
        _ => usage(),
    }
}

fn load_plan(path: &str) -> Result<MappingPlan, Box<dyn Error>> {
    let bytes = std::fs::read(path)?;
    Ok(serde_json::from_slice(&bytes)?)
}

fn route_name(route: &MappingRoute) -> &'static str {
    match route {
        MappingRoute::Mirror { .. } => "mirror",
        MappingRoute::StreamOnly => "stream_only",
    }
}

fn shutdown(mut arguments: impl Iterator<Item = String>) -> Result<(), Box<dyn Error>> {
    if arguments.next().is_some() {
        usage();
    }
    println!(
        "{}",
        if sbms::control::signal_shutdown()? {
            "shutdown=signaled"
        } else {
            "shutdown=not-running"
        }
    );
    Ok(())
}

fn version(mut arguments: impl Iterator<Item = String>) -> Result<(), Box<dyn Error>> {
    if arguments.next().is_some() {
        usage();
    }
    println!("sbms {}", env!("CARGO_PKG_VERSION"));
    Ok(())
}

fn list(mut arguments: impl Iterator<Item = String>) -> Result<(), Box<dyn Error>> {
    if arguments.next().is_some() {
        usage();
    }
    for display in active_displays()? {
        println!(
            "id={}\tname={}\tdevice={}\tconnector={}\tsunshine={}\trect={},{},{},{}\tnative={}x{}\trefresh={}/{}\t{}{}",
            display.id,
            display.name,
            display.device_name,
            display.connector_index,
            display.sunshine_id.as_deref().unwrap_or("-"),
            display.rect.left,
            display.rect.top,
            display.rect.right - display.rect.left,
            display.rect.bottom - display.rect.top,
            display.native_width,
            display.native_height,
            display.refresh_numerator,
            display.refresh_denominator,
            if display.primary { "primary " } else { "" },
            if display.virtual_display {
                "virtual"
            } else {
                "physical"
            }
        );
    }
    Ok(())
}

fn map(mut arguments: impl Iterator<Item = String>) -> Result<(), Box<dyn Error>> {
    let mut target = None;
    let mut hold = None;
    while let Some(argument) = arguments.next() {
        match argument.as_str() {
            "--target" if target.is_none() => {
                target = Some(arguments.next().ok_or("--target requires a display id")?);
            }
            "--hold-ms" if hold.is_none() => {
                let milliseconds = arguments
                    .next()
                    .ok_or("--hold-ms requires a value")?
                    .parse::<u64>()?;
                hold = Some(Duration::from_millis(milliseconds));
            }
            _ => usage(),
        }
    }
    let session_id = diagnostics::new_mapping_session_id();
    diagnostics::log(
        Level::Info,
        "mapping",
        "start",
        Some(session_id),
        "starting mapping",
    );
    let plan =
        configured_map_plan(target).map_err(|error| log_map_preflight_error(session_id, error))?;
    let reporter = Arc::new(move |event| match event {
        MappingEvent::Renderer {
            id: 0,
            event: RendererEvent::Fps(fps),
        } => println!("fps={fps}"),
        MappingEvent::Renderer {
            id: 0,
            event: RendererEvent::Failed(error),
        } => {
            diagnostics::log(
                Level::Error,
                "renderer",
                "renderer",
                Some(session_id),
                &error,
            );
            eprintln!("renderer_error={error}");
        }
        MappingEvent::Renderer {
            id: 0,
            event: RendererEvent::TopologyLost,
        } => {
            diagnostics::log(
                Level::Warn,
                "renderer",
                "topology-lost",
                Some(session_id),
                "desktop duplication topology was lost",
            );
            eprintln!("renderer_topology_lost");
        }
        _ => {}
    });
    let mut session = MappingSession::start_plan_with_reporter(plan, reporter)
        .map_err(|error| log_mapping_error(session_id, error))?;
    let source_id = session
        .groups()
        .next()
        .expect("a running mapping session has at least one group")
        .source_id
        .clone();
    diagnostics::log(
        Level::Info,
        "mapping",
        "running",
        Some(session_id),
        format!("mapping active on {source_id}"),
    );
    println!("running={source_id}");
    wait(hold)?;
    session
        .stop()
        .map_err(|error| log_mapping_error(session_id, error))?;
    diagnostics::log(
        Level::Info,
        "mapping",
        "stop",
        Some(session_id),
        "mapping stopped",
    );
    println!("stopped");
    Ok(())
}

fn configured_map_plan(target: Option<String>) -> Result<MappingPlan, MapPreflightError> {
    let snapshot = ConfigProfileStore::default_store()
        .map_err(|error| MapPreflightError::new("config", error))?
        .load_active()
        .map_err(|error| MapPreflightError::new("config", error))?;
    let target = match target {
        Some(target) => target,
        None => {
            let output_one = snapshot.config.group(0).ok_or_else(|| {
                MapPreflightError::new(
                    "config",
                    "Output 1 is not present in the saved configuration",
                )
            })?;
            match &output_one.route {
                GroupRouteConfig::Mirror {
                    target_id: Some(target),
                } => target.clone(),
                GroupRouteConfig::Mirror { target_id: None } => {
                    return Err(MapPreflightError::new(
                        "config",
                        "Output 1 has no saved target display",
                    ));
                }
                GroupRouteConfig::StreamOnly { .. } => {
                    return Err(MapPreflightError::new(
                        "config",
                        "Output 1 is stream-only; use `sbms plan run` or provide --target",
                    ));
                }
            }
        }
    };
    let sizing = snapshot.config.groups.iter().find_map(|group| {
        matches!(
            &group.route,
            GroupRouteConfig::Mirror {
                target_id: Some(configured_target)
            } if configured_target.eq_ignore_ascii_case(&target)
        )
        .then_some(group.sizing)
        .flatten()
    });
    let mode = match sizing {
        Some(sizing) => {
            let result = sizing
                .calculate()
                .map_err(|error| MapPreflightError::new("geometry", error))?;
            let refresh_millihz = match result.preferred_refresh_millihz {
                Some(refresh) => refresh,
                None => target_refresh_millihz(&target)?,
            };
            VirtualMode::from_millihz(
                result.virtual_mode.width,
                result.virtual_mode.height,
                refresh_millihz,
            )
            .map_err(|error| MapPreflightError::new("mode", error))?
        }
        None => VirtualMode::default(),
    };
    MappingPlan::new(vec![MappingGroupRequest {
        id: 0,
        mode,
        rotation: Rotation::Deg0,
        route: MappingRoute::Mirror { target },
    }])
    .map_err(|error| MapPreflightError::new(error.stage(), error.message()))
}

fn target_refresh_millihz(target_id: &str) -> Result<u32, MapPreflightError> {
    let displays = active_displays().map_err(|error| MapPreflightError::new("target", error))?;
    let target = unique_physical_display(&displays, target_id)
        .map_err(|error| MapPreflightError::new("target", error))?;
    if target.refresh_numerator == 0 || target.refresh_denominator == 0 {
        return Err(MapPreflightError::new(
            "mode",
            "target display reported an invalid refresh rate",
        ));
    }
    u64::from(target.refresh_numerator)
        .checked_mul(1_000)
        .and_then(|value| value.checked_div(u64::from(target.refresh_denominator)))
        .and_then(|value| u32::try_from(value).ok())
        .filter(|value| *value > 0)
        .ok_or_else(|| {
            MapPreflightError::new("mode", "target refresh rate is outside the supported range")
        })
}

fn log_map_preflight_error(
    session_id: MappingSessionId,
    error: MapPreflightError,
) -> MapPreflightError {
    diagnostics::log(
        Level::Error,
        "mapping",
        error.stage,
        Some(session_id),
        error.to_string(),
    );
    error
}

fn log_mapping_error(session_id: MappingSessionId, error: MappingError) -> MappingError {
    diagnostics::log(
        Level::Error,
        "mapping",
        error.stage(),
        Some(session_id),
        error.to_string(),
    );
    error
}

fn config(mut arguments: impl Iterator<Item = String>) -> Result<(), Box<dyn Error>> {
    let store = ConfigProfileStore::default_store()?;
    match arguments.next().as_deref() {
        Some("path") if arguments.next().is_none() => {
            println!("{}", store.path().display());
        }
        Some("list") if arguments.next().is_none() => {
            let profiles = store.list()?;
            let summaries = profiles
                .profiles
                .iter()
                .map(|profile| {
                    serde_json::json!({
                        "id": profile.id,
                        "revision": profile.revision,
                        "active": profile.id.eq_ignore_ascii_case(&profiles.active_profile),
                    })
                })
                .collect::<Vec<_>>();
            println!("{}", serde_json::to_string_pretty(&summaries)?);
        }
        Some("show") => {
            let profile = arguments.next();
            if arguments.next().is_some() {
                usage();
            }
            let snapshot = match profile {
                Some(profile) => store.load_profile(&profile)?,
                None => store.load_active()?,
            };
            println!("{}", serde_json::to_string_pretty(&snapshot.config)?);
        }
        Some("save") => {
            let profile = arguments
                .next()
                .ok_or("config save requires a profile id")?;
            if arguments.next().is_some() {
                usage();
            }
            let snapshot = store.save_active_as(&profile)?;
            println!(
                "saved={} revision={}",
                snapshot.profile.id, snapshot.profile.revision
            );
        }
        Some("import") => {
            let profile = arguments
                .next()
                .ok_or("config import requires a profile id")?;
            let path = arguments
                .next()
                .ok_or("config import requires a JSON file path")?;
            let mut replace = false;
            let mut activate = false;
            for argument in arguments {
                match argument.as_str() {
                    "--replace" => replace = true,
                    "--activate" => activate = true,
                    _ => usage(),
                }
            }
            let imported = ConfigStore::new(path.into()).load_strict()?;
            let (snapshot, is_active) =
                store.save_profile(&profile, &imported, replace, activate)?;
            let reload = is_active.then(signal_config_reload).transpose()?;
            println!(
                "imported={} revision={}{}",
                snapshot.profile.id,
                snapshot.profile.revision,
                reload
                    .map(|signaled| format!(" reload={}", reload_status(signaled)))
                    .unwrap_or_default()
            );
        }
        Some("export") => {
            let profile = arguments
                .next()
                .ok_or("config export requires a profile id")?;
            let path = arguments
                .next()
                .ok_or("config export requires a JSON file path")?;
            let force = match arguments.next().as_deref() {
                None => false,
                Some("--force") if arguments.next().is_none() => true,
                _ => usage(),
            };
            let snapshot = store.load_profile(&profile)?;
            let destination = Path::new(&path);
            if destination.exists() {
                if same_existing_path(destination, store.path())? {
                    return Err("refusing to export over the config profile store".into());
                }
                if !force {
                    return Err(format!(
                        "refusing to overwrite {path}; pass --force to replace it"
                    )
                    .into());
                }
            }
            let export_store = ConfigStore::new(path.clone().into());
            if force {
                export_store.save(&snapshot.config)?;
            } else {
                export_store.save_new(&snapshot.config)?;
            }
            println!("exported={} path={path}", snapshot.profile.id);
        }
        Some("activate") => {
            let profile = arguments
                .next()
                .ok_or("config activate requires a profile id")?;
            if arguments.next().is_some() {
                usage();
            }
            let snapshot = store.activate(&profile)?;
            let signaled = signal_config_reload()?;
            println!(
                "active={} revision={} reload={}",
                snapshot.profile.id,
                snapshot.profile.revision,
                reload_status(signaled)
            );
        }
        Some("delete") => {
            let profile = arguments
                .next()
                .ok_or("config delete requires a profile id")?;
            if arguments.next().is_some() {
                usage();
            }
            store.delete(&profile)?;
            println!("deleted={profile}");
        }
        Some("reload") if arguments.next().is_none() => {
            println!("reload={}", reload_status(signal_config_reload()?));
        }
        Some("set-target") => {
            let target = arguments
                .next()
                .ok_or("config set-target requires a display id")?;
            if arguments.next().is_some() {
                usage();
            }
            validate_physical_target(&target)?;
            let snapshot = store.update_active(|config| {
                let output_one = config.group_mut(0).ok_or_else(|| {
                    ConfigError::new("Output 1 is not present in the saved configuration")
                })?;
                output_one.route = GroupRouteConfig::Mirror {
                    target_id: Some(target),
                };
                Ok(())
            })?;
            println!(
                "saved={} revision={} reload={}",
                snapshot.profile.id,
                snapshot.profile.revision,
                reload_status(signal_config_reload()?)
            );
        }
        Some("clear-target") if arguments.next().is_none() => {
            let snapshot = store.update_active(|config| {
                let output_one = config.group_mut(0).ok_or_else(|| {
                    ConfigError::new("Output 1 is not present in the saved configuration")
                })?;
                output_one.route = GroupRouteConfig::Mirror { target_id: None };
                Ok(())
            })?;
            println!(
                "saved={} revision={} reload={}",
                snapshot.profile.id,
                snapshot.profile.revision,
                reload_status(signal_config_reload()?)
            );
        }
        Some("reset") if arguments.next().is_none() => {
            let snapshot = store.reset_active()?;
            println!(
                "reset={} revision={} reload={}",
                snapshot.profile.id,
                snapshot.profile.revision,
                reload_status(signal_config_reload()?)
            );
        }
        _ => usage(),
    }
    Ok(())
}

fn signal_config_reload() -> Result<bool, Box<dyn Error>> {
    sbms::control::signal_config_reload().map_err(Into::into)
}

fn reload_status(signaled: bool) -> &'static str {
    if signaled { "signaled" } else { "not-running" }
}

fn same_existing_path(left: &Path, right: &Path) -> io::Result<bool> {
    let left = fs::canonicalize(left)?;
    let right = fs::canonicalize(right)?;
    Ok(left
        .to_string_lossy()
        .eq_ignore_ascii_case(&right.to_string_lossy()))
}

fn validate_physical_target(target_id: &str) -> Result<(), Box<dyn Error>> {
    let matches: Vec<_> = active_displays()?
        .into_iter()
        .filter(|display| display.id.eq_ignore_ascii_case(target_id))
        .collect();
    match matches.as_slice() {
        [target] if !target.virtual_display => Ok(()),
        [_] => Err("the saved target cannot be the SBMS virtual display".into()),
        [] => Err(format!("active physical display id not found: {target_id}").into()),
        _ => Err(format!("display id is ambiguous: {target_id}").into()),
    }
}

fn wait(hold: Option<Duration>) -> io::Result<()> {
    if let Some(duration) = hold {
        std::thread::sleep(duration);
    } else {
        println!("Press Enter to stop.");
        let mut line = String::new();
        io::stdin().read_line(&mut line)?;
    }
    Ok(())
}

fn usage() -> ! {
    eprintln!(
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
  sbms shutdown
  sbms ui"
    );
    std::process::exit(2);
}
