//! Hardware GPU identities for a persisted preference and its current-session LUID.

use windows::Win32::Devices::Display::{
    DISPLAYCONFIG_ADAPTER_NAME, DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME,
    DISPLAYCONFIG_DEVICE_INFO_HEADER, DisplayConfigGetDeviceInfo,
};
use windows::Win32::Foundation::LUID;
use windows::Win32::Graphics::Dxgi::{
    CreateDXGIFactory1, DXGI_ADAPTER_DESC1, DXGI_ADAPTER_FLAG_SOFTWARE, DXGI_ERROR_NOT_FOUND,
    IDXGIAdapter1, IDXGIFactory1, IDXGIFactory4,
};

#[derive(Clone, Debug, PartialEq)]
pub struct Adapter {
    /// Device interface path, stable across boots; unlike a DXGI index or LUID.
    pub id: String,
    pub name: String,
    pub luid: LUID,
}

/// Call after capture routing initialization, before creating the virtual screen.
pub fn enumerate() -> Result<Vec<Adapter>, String> {
    let factory: IDXGIFactory1 =
        unsafe { CreateDXGIFactory1() }.map_err(|error| format!("Enumerate GPUs: {error}"))?;
    let mut adapters = Vec::new();
    for index in 0.. {
        let adapter = match unsafe { factory.EnumAdapters1(index) } {
            Ok(adapter) => adapter,
            Err(error) if error.code() == DXGI_ERROR_NOT_FOUND => break,
            Err(error) => return Err(format!("Enumerate GPUs: {error}")),
        };
        let description = unsafe { adapter.GetDesc1() }
            .map_err(|error| format!("Read GPU description: {error}"))?;
        if description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE.0 as u32 != 0 {
            continue;
        }
        let name = wide_string(&description.Description);
        let id = device_path(description.AdapterLuid)
            .map_err(|error| format!("Identify GPU {name}: {error}"))?;
        // DXGI also lists our IDD under its render GPU's name. It is a display
        // adapter, not another GPU that can render the virtual desktop.
        if id.starts_with(r"\\?\swd#sbms#") || id.starts_with(r"\\?\swd#sbmscapturediagnostics#") {
            continue;
        }
        adapters.push(Adapter {
            id,
            name,
            luid: description.AdapterLuid,
        });
    }
    adapters.sort_by(|left, right| left.name.cmp(&right.name).then(left.id.cmp(&right.id)));
    disambiguate_names(&mut adapters);
    Ok(adapters)
}

/// Resolve a persisted choice. A missing explicit GPU is never replaced.
pub fn resolve(preferred: Option<&str>) -> Result<Option<Adapter>, String> {
    match preferred {
        None => Ok(None),
        Some(id) => select(&enumerate()?, id).cloned().map(Some),
    }
}

/// All virtual screens share one render adapter. Request the mirror targets'
/// owner only when every target identifies the same selectable GPU.
pub(crate) fn automatic_for_targets(
    mut owners: impl Iterator<Item = Option<LUID>>,
    describe: impl FnOnce(LUID) -> Result<DXGI_ADAPTER_DESC1, String>,
) -> Option<(LUID, String)> {
    let owner = owners.next()??;
    if owner == LUID::default() || !owners.all(|other| other == Some(owner)) {
        return None;
    }
    let gpu = describe(owner).ok()?;
    (gpu.AdapterLuid == owner && gpu.Flags & DXGI_ADAPTER_FLAG_SOFTWARE.0 as u32 == 0)
        .then(|| (owner, wide_string(&gpu.Description)))
}

/// Describe the actual GPU from a driver receipt, without requiring it to be selectable.
pub fn name_for_luid(luid: LUID) -> Result<String, String> {
    Ok(wide_string(&description_for_luid(luid)?.Description))
}

pub(crate) fn description_for_luid(luid: LUID) -> Result<DXGI_ADAPTER_DESC1, String> {
    let factory: IDXGIFactory4 =
        unsafe { CreateDXGIFactory1() }.map_err(|error| format!("Read active GPU: {error}"))?;
    let adapter: IDXGIAdapter1 = unsafe { factory.EnumAdapterByLuid(luid) }
        .map_err(|error| format!("Read active GPU: {error}"))?;
    unsafe { adapter.GetDesc1() }.map_err(|error| format!("Read active GPU description: {error}"))
}

fn select<'a>(adapters: &'a [Adapter], id: &str) -> Result<&'a Adapter, String> {
    adapters
        .iter()
        .find(|adapter| adapter.id.eq_ignore_ascii_case(id))
        .ok_or_else(|| "Selected GPU is unavailable. Choose another GPU or Automatic.".into())
}

fn device_path(luid: LUID) -> Result<String, String> {
    let mut packet = DISPLAYCONFIG_ADAPTER_NAME {
        header: DISPLAYCONFIG_DEVICE_INFO_HEADER {
            r#type: DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME,
            size: size_of::<DISPLAYCONFIG_ADAPTER_NAME>() as u32,
            adapterId: luid,
            id: 0,
        },
        ..Default::default()
    };
    let status = unsafe { DisplayConfigGetDeviceInfo(&mut packet.header) };
    if status != 0 {
        return Err(format!("DisplayConfigGetDeviceInfo returned {status}"));
    }
    let path = wide_string(&packet.adapterDevicePath);
    if path.is_empty() {
        return Err("Windows returned no persistent device identity".into());
    }
    Ok(path.to_ascii_lowercase())
}

fn wide_string(value: &[u16]) -> String {
    let end = value
        .iter()
        .position(|unit| *unit == 0)
        .unwrap_or(value.len());
    String::from_utf16_lossy(&value[..end])
}

fn disambiguate_names(adapters: &mut [Adapter]) {
    let names: Vec<_> = adapters
        .iter()
        .map(|adapter| adapter.name.clone())
        .collect();
    for (index, adapter) in adapters.iter_mut().enumerate() {
        if names
            .iter()
            .enumerate()
            .any(|(other, name)| other != index && name.eq_ignore_ascii_case(&adapter.name))
        {
            let instance = adapter.id.split('#').nth(2).unwrap_or(&adapter.id);
            adapter.name = format!("{} ({instance})", adapter.name);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn selection_survives_luid_changes_and_never_substitutes_a_missing_gpu() {
        let adapters = [Adapter {
            id: "persistent-device-path".into(),
            name: "GPU".into(),
            luid: LUID {
                LowPart: 789,
                HighPart: 0,
            },
        }];
        assert_eq!(
            select(&adapters, "PERSISTENT-DEVICE-PATH")
                .unwrap()
                .luid
                .LowPart,
            789
        );
        assert!(select(&adapters, "missing-device-path").is_err());
        assert!(select(&adapters, "").is_err());
        assert_eq!(resolve(None).unwrap(), None);
    }

    #[test]
    fn automatic_selects_the_common_mirror_owner_when_available() {
        let owner = LUID {
            LowPart: 42,
            HighPart: -1,
        };
        let other = LUID {
            HighPart: 1,
            ..owner
        };
        let gpu = DXGI_ADAPTER_DESC1 {
            AdapterLuid: owner,
            ..Default::default()
        };
        for targets in [vec![owner], vec![owner, owner]] {
            assert_eq!(
                automatic_for_targets(targets.into_iter().map(Some), |_| Ok(gpu))
                    .unwrap()
                    .0,
                owner
            );
        }
        for targets in [
            vec![],
            vec![Some(owner), Some(other)],
            vec![Some(owner), None],
            vec![Some(LUID::default())],
        ] {
            assert!(
                automatic_for_targets(targets.into_iter(), |_| panic!("no common owner")).is_none()
            );
        }
        let one_target = || std::iter::once(Some(owner));
        assert!(automatic_for_targets(one_target(), |_| Err("GPU unavailable".into())).is_none());
        let software = DXGI_ADAPTER_DESC1 {
            Flags: DXGI_ADAPTER_FLAG_SOFTWARE.0 as u32,
            ..gpu
        };
        assert!(automatic_for_targets(one_target(), |_| Ok(software)).is_none());
    }

    #[test]
    fn duplicate_models_keep_distinct_ids_and_labels() {
        let mut adapters: Vec<_> = ["slot-a", "slot-b"]
            .into_iter()
            .map(|slot| Adapter {
                id: format!(r"\\?\pci#model#{slot}#interface"),
                name: "Same GPU".into(),
                luid: LUID::default(),
            })
            .collect();
        let ids: Vec<_> = adapters.iter().map(|adapter| adapter.id.clone()).collect();
        disambiguate_names(&mut adapters);
        assert_eq!(adapters[0].name, "Same GPU (slot-a)");
        assert_eq!(adapters[1].name, "Same GPU (slot-b)");
        for id in ids {
            assert_eq!(select(&adapters, &id).unwrap().id, id);
        }
    }
}
