use std::error::Error;
use std::io;
use std::sync::Arc;
use std::time::Duration;

use sbms::config::{ConfigStore, GroupRouteConfig};
use sbms::diagnostics::{self, Level, MappingSessionId};
use sbms::display::active_displays;
use sbms::mapping::{
    MappingError, MappingEvent, MappingPlan, MappingRequest, MappingRoute, MappingSession,
};
use sbms::renderer::RendererEvent;
use windows::Win32::UI::HiDpi::{
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetProcessDpiAwarenessContext,
};

fn main() -> Result<(), Box<dyn Error>> {
    if let Err(error) = diagnostics::init() {
        eprintln!("warning: diagnostics initialization failed: {error}");
    }
    diagnostics::log(Level::Info, "cli", "startup", None, "SBMS CLI started");
    let result = run();
    if let Err(error) = &result {
        diagnostics::log(Level::Error, "cli", "command", None, error.to_string());
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
        Some("ui") => sbms::ui::run_open(),
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
    let target = match target {
        Some(target) => target,
        None => {
            let outcome = ConfigStore::default_store()?.load()?;
            if let Some(warning) = outcome.warning {
                eprintln!("warning: {warning}");
            }
            let output_one = outcome
                .config
                .group(0)
                .ok_or("Output 1 is not present in the saved configuration")?;
            match &output_one.route {
                GroupRouteConfig::Mirror {
                    target_id: Some(target),
                } => target.clone(),
                GroupRouteConfig::Mirror { target_id: None } => {
                    return Err("Output 1 has no saved target display".into());
                }
                GroupRouteConfig::StreamOnly { .. } => {
                    return Err(
                        "Output 1 is stream-only; use `sbms plan run` or provide --target".into(),
                    );
                }
            }
        }
    };

    let session_id = diagnostics::new_mapping_session_id();
    diagnostics::log(
        Level::Info,
        "mapping",
        "start",
        Some(session_id),
        "starting mapping",
    );
    let request =
        MappingRequest::configured(target).map_err(|error| log_mapping_error(session_id, error))?;
    let reporter = Arc::new(move |event| match event {
        RendererEvent::Fps(fps) => println!("fps={fps}"),
        RendererEvent::Failed(error) => {
            diagnostics::log(
                Level::Error,
                "renderer",
                "renderer",
                Some(session_id),
                &error,
            );
            eprintln!("renderer_error={error}");
        }
    });
    let mut session = MappingSession::start_with_reporter(request, reporter)
        .map_err(|error| log_mapping_error(session_id, error))?;
    diagnostics::log(
        Level::Info,
        "mapping",
        "running",
        Some(session_id),
        format!("mapping active on {}", session.source_id()),
    );
    println!("running={}", session.source_id());
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
    let store = ConfigStore::default_store()?;
    match arguments.next().as_deref() {
        Some("path") if arguments.next().is_none() => {
            println!("{}", store.path().display());
        }
        Some("show") if arguments.next().is_none() => {
            let outcome = store.load()?;
            if let Some(warning) = outcome.warning {
                eprintln!("warning: {warning}");
            }
            println!("{}", serde_json::to_string_pretty(&outcome.config)?);
        }
        Some("set-target") => {
            let target = arguments
                .next()
                .ok_or("config set-target requires a display id")?;
            if arguments.next().is_some() {
                usage();
            }
            validate_physical_target(&target)?;
            let outcome = store.load()?;
            if let Some(warning) = outcome.warning {
                return Err(format!(
                    "{warning}; run `sbms config reset` before replacing a bad config"
                )
                .into());
            }
            let mut config = outcome.config;
            let output_one = config
                .group_mut(0)
                .ok_or("Output 1 is not present in the saved configuration")?;
            output_one.route = GroupRouteConfig::Mirror {
                target_id: Some(target),
            };
            store.save(&config)?;
            println!("saved={}", store.path().display());
        }
        Some("clear-target") if arguments.next().is_none() => {
            let outcome = store.load()?;
            if let Some(warning) = outcome.warning {
                return Err(format!(
                    "{warning}; run `sbms config reset` before replacing a bad config"
                )
                .into());
            }
            let mut config = outcome.config;
            let output_one = config
                .group_mut(0)
                .ok_or("Output 1 is not present in the saved configuration")?;
            output_one.route = GroupRouteConfig::Mirror { target_id: None };
            store.save(&config)?;
            println!("saved={}", store.path().display());
        }
        Some("reset") if arguments.next().is_none() => {
            store.reset()?;
            println!("reset={}", store.path().display());
        }
        _ => usage(),
    }
    Ok(())
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
  sbms config path|show|set-target <monitor-device-path>|clear-target|reset
  sbms shutdown
  sbms ui"
    );
    std::process::exit(2);
}
