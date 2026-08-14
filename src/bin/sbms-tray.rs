#![windows_subsystem = "windows"]

use sbms::launch_broker::{self, LaunchDisposition};

fn main() {
    if let Err(error) = run() {
        sbms::diagnostics::log(
            sbms::diagnostics::Level::Error,
            "tray",
            "startup",
            None,
            error.to_string(),
        );
        launch_broker::show_launch_error(&*error);
        std::process::exit(1);
    }
}

fn run() -> Result<(), Box<dyn std::error::Error>> {
    if let Err(error) = sbms::diagnostics::init() {
        eprintln!("warning: diagnostics initialization failed: {error}");
    }
    sbms::diagnostics::log(
        sbms::diagnostics::Level::Info,
        "tray",
        "process-start",
        None,
        "SBMS tray process started",
    );
    unsafe {
        windows::Win32::UI::HiDpi::SetProcessDpiAwarenessContext(
            windows::Win32::UI::HiDpi::DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
        )
    }?;
    let open_requested = launch_broker::tray_open_requested(std::env::args_os().skip(1))?;
    match launch_broker::route_tray(open_requested)? {
        LaunchDisposition::Exit => Ok(()),
        LaunchDisposition::RunHere(instance) => sbms::ui::run_host(instance, open_requested),
    }
}
