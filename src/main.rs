use std::error::Error;
use std::io;
use std::time::Duration;

use sbms::config::ConfigStore;
use sbms::display::active_displays;
use sbms::frame_transport::{FrameTransport, VirtualMode};
use sbms::mapping::{MappingRequest, MappingSession};
use sbms::virtual_display::VirtualDisplay;
use windows::Win32::UI::HiDpi::{
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetProcessDpiAwarenessContext,
};

fn main() -> Result<(), Box<dyn Error>> {
    unsafe { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) }?;
    let mut arguments = std::env::args().skip(1);
    match arguments.next().as_deref() {
        Some("--version") => version(arguments),
        Some("list") => list(arguments),
        Some("create") => create(arguments),
        Some("map") => map(arguments),
        Some("config") => config(arguments),
        Some("shutdown") => shutdown(arguments),
        Some("ui") => sbms::ui::run_open(),
        _ => usage(),
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
            "id={}\tname={}\tdevice={}\trect={},{},{},{}\trefresh={}/{}\t{}{}",
            display.id,
            display.name,
            display.device_name,
            display.rect.left,
            display.rect.top,
            display.rect.right - display.rect.left,
            display.rect.bottom - display.rect.top,
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

fn create(mut arguments: impl Iterator<Item = String>) -> Result<(), Box<dyn Error>> {
    let hold = parse_hold(&mut arguments)?;
    if arguments.next().is_some() {
        usage();
    }

    let transport = FrameTransport::create(VirtualMode::default())?;
    let display = VirtualDisplay::create()?;
    println!("created={}", display.instance_id());
    wait(hold)?;
    drop(display);
    drop(transport);
    println!("removed");
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
            outcome
                .config
                .target_id
                .ok_or("no --target was provided and no target_id is saved in config")?
        }
    };

    let request = MappingRequest::configured(target)?;
    let mut session = MappingSession::start(request)?;
    println!("running={}", session.source_id());
    wait(hold)?;
    session.stop()?;
    println!("stopped");
    Ok(())
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
            config.target_id = Some(target);
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
            config.target_id = None;
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

fn parse_hold(
    arguments: &mut impl Iterator<Item = String>,
) -> Result<Option<Duration>, Box<dyn Error>> {
    let hold = match arguments.next().as_deref() {
        None => None,
        Some("--hold-ms") => {
            let milliseconds = arguments
                .next()
                .ok_or("--hold-ms requires a value")?
                .parse::<u64>()?;
            Some(Duration::from_millis(milliseconds))
        }
        Some(_) => usage(),
    };
    Ok(hold)
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
  sbms create [--hold-ms <milliseconds>]
  sbms map [--target <monitor-device-path>] [--hold-ms <milliseconds>]
  sbms config path|show|set-target <monitor-device-path>|clear-target|reset
  sbms shutdown
  sbms ui"
    );
    std::process::exit(2);
}
