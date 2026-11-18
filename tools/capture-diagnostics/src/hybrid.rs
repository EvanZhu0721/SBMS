//! Opt-in experiment: expose native DXGI output ownership in this probe process.
//! This intercepts an undocumented Windows export; it is not a product default.
use std::ffi::c_void;
use std::sync::atomic::{AtomicU64, Ordering};

use minhook::MinHook;
use serde_json::{Value, json};
use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
use windows::core::{s, w};

static QUERIES: AtomicU64 = AtomicU64::new(0);

// d3dkmthk.h: D3DKMT_GPU_PREFERENCE_STATE_UNSPECIFIED (not DXGI's value 0).
const UNSPECIFIED: u32 = 3;

unsafe extern "system" fn native_preference(state: *mut u32) -> i32 {
    if state.is_null() {
        return 0xC000000Du32 as i32; // STATUS_INVALID_PARAMETER
    }
    unsafe { state.write(UNSPECIFIED) };
    QUERIES.fetch_add(1, Ordering::Relaxed);
    0 // STATUS_SUCCESS
}

/// Call once, before the first DXGI/D3D operation. The hook and module remain
/// loaded until process exit, so no device can outlive this routing policy.
pub fn install() -> crate::Result<()> {
    eprintln!("{{\"event\":\"BEGIN\",\"stage\":\"install-native-output-routing\"}}");
    unsafe {
        let module = LoadLibraryW(w!("win32u.dll"))?;
        let target = GetProcAddress(module, s!("NtGdiDdDDIGetCachedHybridQueryValue"))
            .ok_or("win32u hybrid preference export is unavailable")?
            as *mut c_void;
        MinHook::create_hook(target, native_preference as *mut c_void)
            .map_err(|error| format!("create native output routing hook: {error:?}"))?;
        MinHook::enable_hook(target)
            .map_err(|error| format!("enable native output routing hook: {error:?}"))?;
    }
    eprintln!(
        "{{\"event\":\"END\",\"stage\":\"install-native-output-routing\",\"status\":\"ok\"}}"
    );
    Ok(())
}

pub fn report(enabled: bool) -> Value {
    json!({
        "mode": if enabled { "native-output-routing" } else { "windows-default" },
        "hook_enabled": enabled,
        "hook_queries": QUERIES.load(Ordering::Relaxed),
        "forced_cached_preference_state": if enabled { Some(UNSPECIFIED) } else { None },
        "scope": "this process only; no system settings changed"
    })
}
