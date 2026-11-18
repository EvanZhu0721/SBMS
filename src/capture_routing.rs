//! Preserve native DXGI output ownership through an undocumented Windows export.
//! SBMS_NATIVE_OUTPUT_ROUTING=0 disables the compatibility hook for troubleshooting.

use std::error::Error;
use std::ffi::c_void;

use minhook::MinHook;
use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
use windows::core::{s, w};

use crate::diagnostics::{self, Level};

// d3dkmthk.h: D3DKMT_GPU_PREFERENCE_STATE_UNSPECIFIED (not DXGI's value 0).
const UNSPECIFIED: u32 = 3;

unsafe extern "system" fn native_preference(state: *mut u32) -> i32 {
    if state.is_null() {
        return 0xC000000Du32 as i32; // STATUS_INVALID_PARAMETER
    }
    unsafe { state.write(UNSPECIFIED) };
    0 // STATUS_SUCCESS
}

/// Call once per process, after DPI setup and before the first DXGI/D3D operation.
/// The hook and module stay loaded until process exit, so every device uses the
/// same routing policy for its entire lifetime.
pub fn initialize_from_env() -> Result<(), Box<dyn Error>> {
    if std::env::var("SBMS_NATIVE_OUTPUT_ROUTING").as_deref() == Ok("0") {
        diagnostics::log(
            Level::Warn,
            "capture",
            "native-output-routing",
            None,
            "native output routing disabled by SBMS_NATIVE_OUTPUT_ROUTING=0",
        );
        return Ok(());
    }

    unsafe {
        let module = LoadLibraryW(w!("win32u.dll"))
            .map_err(|error| format!("load native output routing module win32u.dll: {error}"))?;
        let target = GetProcAddress(module, s!("NtGdiDdDDIGetCachedHybridQueryValue"))
            .ok_or("win32u hybrid preference export is unavailable")?
            as *mut c_void;
        MinHook::create_hook(target, native_preference as *mut c_void)
            .map_err(|error| format!("create native output routing hook: {error:?}"))?;
        MinHook::enable_hook(target)
            .map_err(|error| format!("enable native output routing hook: {error:?}"))?;
    }
    diagnostics::log(
        Level::Info,
        "capture",
        "native-output-routing",
        None,
        format!(
            "native output routing enabled for process {}",
            std::process::id()
        ),
    );
    Ok(())
}
