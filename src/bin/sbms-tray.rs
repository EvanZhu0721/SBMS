#![windows_subsystem = "windows"]

fn main() -> Result<(), Box<dyn std::error::Error>> {
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
    sbms::ui::run()
}
