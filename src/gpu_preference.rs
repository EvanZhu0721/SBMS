use std::sync::OnceLock;

use windows::Win32::Foundation::HMODULE;
use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_UNKNOWN;
use windows::Win32::Graphics::Direct3D11::{
    D3D11_CREATE_DEVICE_FLAG, D3D11_FEATURE_D3D11_OPTIONS2, D3D11_FEATURE_DATA_D3D11_OPTIONS2,
    D3D11_SDK_VERSION, D3D11CreateDevice, ID3D11Device,
};
use windows::Win32::Graphics::Dxgi::{
    CreateDXGIFactory1, DXGI_ADAPTER_FLAG_SOFTWARE, DXGI_ERROR_NOT_FOUND, IDXGIAdapter1,
    IDXGIFactory1,
};

use crate::diagnostics::{self, Level};

static HIGH_PERFORMANCE: OnceLock<bool> = OnceLock::new();

pub(crate) fn prefer_high_performance() -> bool {
    *HIGH_PERFORMANCE.get_or_init(|| {
        let result = probe();
        let enabled = match &result {
            Ok(adapters) => default_for_adapters(adapters),
            Err(_) => true,
        };
        let details = match &result {
            Ok(adapters) => adapters
                .iter()
                .map(|adapter| {
                    format!(
                        "{}: software={}, unified={:?}",
                        adapter.name, adapter.software, adapter.unified
                    )
                })
                .collect::<Vec<_>>()
                .join("; "),
            Err(error) => format!("probe failed: {error}"),
        };
        diagnostics::log(
            Level::Info,
            "renderer",
            "sampling-default",
            None,
            format!("high-performance={enabled}; {details}"),
        );
        enabled
    })
}

#[derive(Debug)]
struct AdapterMemory {
    name: String,
    software: bool,
    unified: Option<bool>,
}

fn default_for_adapters(adapters: &[AdapterMemory]) -> bool {
    // Unknown adapters do not establish the presence of a discrete GPU.
    !adapters
        .iter()
        .any(|adapter| !adapter.software && adapter.unified == Some(false))
}

fn probe() -> Result<Vec<AdapterMemory>, String> {
    let factory: IDXGIFactory1 =
        unsafe { CreateDXGIFactory1() }.map_err(|error| error.to_string())?;
    let mut adapters = Vec::new();
    for index in 0.. {
        let adapter = match unsafe { factory.EnumAdapters1(index) } {
            Ok(adapter) => adapter,
            Err(error) if error.code() == DXGI_ERROR_NOT_FOUND => return Ok(adapters),
            Err(error) => return Err(error.to_string()),
        };
        let description = unsafe { adapter.GetDesc1() }.map_err(|error| error.to_string())?;
        let name_end = description
            .Description
            .iter()
            .position(|value| *value == 0)
            .unwrap_or(description.Description.len());
        let name = String::from_utf16_lossy(&description.Description[..name_end]);
        let software = description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE.0 as u32 != 0;
        let unified = if software {
            None
        } else {
            match unified_memory(&adapter) {
                Ok(unified) => Some(unified),
                Err(error) => {
                    diagnostics::log(
                        Level::Warn,
                        "renderer",
                        "sampling-default",
                        None,
                        format!("could not inspect memory architecture for {name}: {error}"),
                    );
                    None
                }
            }
        };
        adapters.push(AdapterMemory {
            name,
            software,
            unified,
        });
    }
    unreachable!()
}

fn unified_memory(adapter: &IDXGIAdapter1) -> windows::core::Result<bool> {
    let mut device: Option<ID3D11Device> = None;
    unsafe {
        D3D11CreateDevice(
            adapter,
            D3D_DRIVER_TYPE_UNKNOWN,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_FLAG(0),
            None,
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            None,
        )?;
    }
    let device = device.ok_or_else(windows::core::Error::empty)?;
    let mut options = D3D11_FEATURE_DATA_D3D11_OPTIONS2::default();
    unsafe {
        device.CheckFeatureSupport(
            D3D11_FEATURE_D3D11_OPTIONS2,
            (&mut options as *mut D3D11_FEATURE_DATA_D3D11_OPTIONS2).cast(),
            std::mem::size_of_val(&options) as u32,
        )?;
    }
    Ok(options.UnifiedMemoryArchitecture.as_bool())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn automatic_sampling_uses_memory_architecture() {
        let adapter = |software, unified| AdapterMemory {
            name: String::new(),
            software,
            unified,
        };
        assert!(default_for_adapters(&[]));
        assert!(default_for_adapters(&[adapter(false, None)]));
        assert!(default_for_adapters(&[
            adapter(false, Some(true)),
            adapter(true, Some(false)),
        ]));
        assert!(!default_for_adapters(&[
            adapter(false, Some(true)),
            adapter(false, Some(false)),
        ]));
    }

    #[test]
    #[ignore = "queries installed graphics adapters; run explicitly on a hardware validation host"]
    fn report_installed_adapter_sampling_default() {
        let adapters = probe().expect("DXGI adapter enumeration");
        for adapter in &adapters {
            println!(
                "{}: software={}, unified={:?}",
                adapter.name, adapter.software, adapter.unified
            );
        }
        println!(
            "automatic high-performance sampling={}",
            default_for_adapters(&adapters)
        );
    }
}
