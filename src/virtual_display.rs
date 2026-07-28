use std::ffi::c_void;
use std::sync::{Arc, Condvar, Mutex};
use std::time::Duration;

use windows::Win32::Devices::Enumeration::Pnp::{
    HSWDEVICE, SW_DEVICE_CREATE_INFO, SWDeviceCapabilitiesDriverRequired,
    SWDeviceCapabilitiesRemovable, SWDeviceCapabilitiesSilentInstall, SwDeviceClose,
    SwDeviceCreate,
};
use windows::Win32::Security::SECURITY_DESCRIPTOR;
use windows::core::{Error, GUID, HRESULT, PCWSTR, Result, w};

const CREATE_TIMEOUT: Duration = Duration::from_secs(10);
const CONTAINER_ID: GUID = GUID::from_u128(0x71d2c4b2_0e4e_4a65_93c5_80c9c616f42c);

struct CreationState {
    result: Mutex<Option<std::result::Result<String, HRESULT>>>,
    changed: Condvar,
}

impl CreationState {
    fn new() -> Self {
        Self {
            result: Mutex::new(None),
            changed: Condvar::new(),
        }
    }
}

pub struct VirtualDisplay {
    handle: HSWDEVICE,
}

impl VirtualDisplay {
    pub fn create() -> Result<Self> {
        let hardware_ids = wide_multi_string(&["SBMS\\IndirectDisplay"]);
        let state = Arc::new(CreationState::new());

        let create_info = SW_DEVICE_CREATE_INFO {
            cbSize: size_of::<SW_DEVICE_CREATE_INFO>() as u32,
            pszInstanceId: w!("VirtualDisplay-01"),
            pszzHardwareIds: PCWSTR(hardware_ids.as_ptr()),
            pszzCompatibleIds: PCWSTR::null(),
            pContainerId: &CONTAINER_ID,
            CapabilityFlags: (SWDeviceCapabilitiesRemovable.0
                | SWDeviceCapabilitiesSilentInstall.0
                | SWDeviceCapabilitiesDriverRequired.0) as u32,
            pszDeviceDescription: w!("SBMS Virtual Display"),
            pszDeviceLocation: w!("Software device"),
            pSecurityDescriptor: std::ptr::null::<SECURITY_DESCRIPTOR>(),
        };

        // One Arc reference belongs to the callback. This keeps the context alive
        // even if our bounded wait expires before Windows invokes it.
        let callback_state = Arc::into_raw(Arc::clone(&state));

        // SAFETY: create_info remains valid for this call. callback_state is an Arc
        // raw pointer reserved for the one-shot callback.
        let create_result = unsafe {
            SwDeviceCreate(
                w!("SBMS"),
                w!("HTREE\\ROOT\\0"),
                &create_info,
                None,
                Some(device_created),
                Some(callback_state.cast::<c_void>()),
            )
        };
        let handle = match create_result {
            Ok(handle) => handle,
            Err(error) => {
                // No callback is scheduled when SwDeviceCreate itself fails.
                // SAFETY: callback_state came from Arc::into_raw above.
                unsafe { drop(Arc::from_raw(callback_state)) };
                return Err(error);
            }
        };

        let result = state.result.lock().expect("creation mutex poisoned");
        let (mut result, timeout) = state
            .changed
            .wait_timeout_while(result, CREATE_TIMEOUT, |value| value.is_none())
            .expect("creation mutex poisoned");

        if timeout.timed_out() {
            // SAFETY: handle came from a successful SwDeviceCreate call.
            unsafe { SwDeviceClose(handle) };
            return Err(Error::from_hresult(HRESULT(0x800705B4_u32 as i32)));
        }

        match result.take().expect("callback completed without a result") {
            Ok(_) => Ok(Self { handle }),
            Err(hr) => {
                // SAFETY: handle came from a successful SwDeviceCreate call.
                unsafe { SwDeviceClose(handle) };
                Err(Error::from_hresult(hr))
            }
        }
    }
}

impl Drop for VirtualDisplay {
    fn drop(&mut self) {
        // SAFETY: this type uniquely owns the HSWDEVICE returned by SwDeviceCreate.
        unsafe { SwDeviceClose(self.handle) };
    }
}

unsafe extern "system" fn device_created(
    _device: HSWDEVICE,
    create_result: HRESULT,
    context: *const c_void,
    device_instance_id: PCWSTR,
) {
    // SAFETY: create() passes one Arc strong reference exclusively to this
    // one-shot callback.
    let state = unsafe { Arc::from_raw(context as *const CreationState) };
    let outcome = if create_result.is_ok() {
        // SAFETY: Windows supplies a null-terminated instance ID.
        unsafe { device_instance_id.to_string() }.map_err(|_| HRESULT(0x80070057_u32 as i32))
    } else {
        Err(create_result)
    };

    *state.result.lock().expect("creation mutex poisoned") = Some(outcome);
    state.changed.notify_one();
}

fn wide_multi_string(values: &[&str]) -> Vec<u16> {
    let mut result = Vec::new();
    for value in values {
        result.extend(value.encode_utf16());
        result.push(0);
    }
    result.push(0);
    result
}
