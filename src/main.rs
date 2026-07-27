mod display;
mod frame_transport;
mod mapping;
mod renderer;
mod virtual_display;

use std::error::Error;
use std::io;
use std::time::Duration;

use display::active_displays;
use mapping::{MappingRequest, MappingSession};
use virtual_display::VirtualDisplay;
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
        _ => usage(),
    }
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
            "id={}\tname={}\tdevice={}\trect={},{},{},{}\t{}{}",
            display.id,
            display.name,
            display.device_name,
            display.rect.left,
            display.rect.top,
            display.rect.right - display.rect.left,
            display.rect.bottom - display.rect.top,
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

    let display = VirtualDisplay::create()?;
    println!("created={}", display.instance_id());
    wait(hold)?;
    drop(display);
    println!("removed");
    Ok(())
}

fn map(mut arguments: impl Iterator<Item = String>) -> Result<(), Box<dyn Error>> {
    if arguments.next().as_deref() != Some("--target") {
        usage();
    }
    let target = arguments.next().ok_or("--target requires a display id")?;
    let hold = parse_hold(&mut arguments)?;
    if arguments.next().is_some() {
        usage();
    }

    let mut session = MappingSession::start(MappingRequest { target })?;
    println!("running={}", session.source_id());
    wait(hold)?;
    session.stop()?;
    println!("stopped");
    Ok(())
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
  sbms map --target <monitor-device-path> [--hold-ms <milliseconds>]"
    );
    std::process::exit(2);
}
