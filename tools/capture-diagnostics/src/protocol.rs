use crate::Result;
use serde_json::{Value, json};
use std::mem::size_of;
use std::sync::atomic::{AtomicI32, Ordering};
use windows::Win32::Foundation::{
    CloseHandle, ERROR_ALREADY_EXISTS, ERROR_FILE_NOT_FOUND, GetLastError, HANDLE, HLOCAL,
    INVALID_HANDLE_VALUE, LocalFree,
};
use windows::Win32::Security::Authorization::{
    ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
};
use windows::Win32::Security::{PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES};
use windows::Win32::System::Memory::{
    CreateFileMappingW, FILE_MAP_ALL_ACCESS, FILE_MAP_READ, MEMORY_MAPPED_VIEW_ADDRESS,
    MapViewOfFile, OpenFileMappingW, PAGE_READWRITE, UnmapViewOfFile,
};
use windows::core::{PCWSTR, w};

pub const DIAGNOSTIC_SIZE: usize = 18016;

pub fn require_no_product_session() -> Result<()> {
    for name in [w!("Global\\SBMSSession-v8"), w!("Global\\SBMSSession-v9")] {
        match unsafe { OpenFileMappingW(FILE_MAP_READ.0, false, name) } {
            Ok(handle) => {
                unsafe {
                    let _ = CloseHandle(handle);
                }
                return Err(
                    "an active SBMS mapping exists; stop it before isolated diagnostics".into(),
                );
            }
            Err(error)
                if error.code() == windows::core::HRESULT::from_win32(ERROR_FILE_NOT_FOUND.0) => {}
            Err(error) => return Err(error.into()),
        }
    }
    Ok(())
}

pub struct Mapping {
    handle: HANDLE,
    view: MEMORY_MAPPED_VIEW_ADDRESS,
    size: usize,
    // Product diagnostics reserve both protocol names for one device. Each
    // installed driver opens only its exact version and publishes into that view.
    compatibility_gate: Option<Box<Mapping>>,
}

impl Mapping {
    pub fn create(name: &str, data: &[u8]) -> Result<Self> {
        let mut sd = PSECURITY_DESCRIPTOR::default();
        unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                w!("D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)(A;;GA;;;OW)"),
                SDDL_REVISION_1,
                &mut sd,
                None,
            )?;
        }
        let attrs = SECURITY_ATTRIBUTES {
            nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: sd.0,
            bInheritHandle: false.into(),
        };
        let name: Vec<_> = name.encode_utf16().chain(Some(0)).collect();
        let handle = unsafe {
            CreateFileMappingW(
                INVALID_HANDLE_VALUE,
                Some(&attrs),
                PAGE_READWRITE,
                0,
                data.len() as u32,
                PCWSTR(name.as_ptr()),
            )
        };
        let already_exists = unsafe { GetLastError() } == ERROR_ALREADY_EXISTS;
        unsafe {
            LocalFree(Some(HLOCAL(sd.0)));
        }
        let handle = handle?;
        if already_exists {
            unsafe {
                let _ = CloseHandle(handle);
            }
            return Err(
                "session mapping already exists; stop the active SBMS/diagnostic session first"
                    .into(),
            );
        }
        let view = unsafe { MapViewOfFile(handle, FILE_MAP_ALL_ACCESS, 0, 0, data.len()) };
        if view.Value.is_null() {
            unsafe {
                let _ = CloseHandle(handle);
            }
            return Err(windows::core::Error::from_thread().into());
        }
        unsafe {
            std::ptr::copy_nonoverlapping(data.as_ptr(), view.Value.cast::<u8>(), data.len());
        }
        Ok(Self {
            handle,
            view,
            size: data.len(),
            compatibility_gate: None,
        })
    }

    pub fn gate(
        diagnostic: bool,
        width: u32,
        height: u32,
        numerator: u32,
        denominator: u32,
    ) -> Result<Self> {
        let legacy = gate_data(8, width, height, numerator, denominator);
        if diagnostic {
            return Self::create("Global\\SBMSCaptureSession-v8", &legacy);
        }
        let mut gate = Self::create(
            "Global\\SBMSSession-v9",
            &gate_data(9, width, height, numerator, denominator),
        )?;
        // A collision on either version aborts creation and drops our other
        // reservation before any software device is created.
        gate.compatibility_gate = Some(Box::new(Self::create("Global\\SBMSSession-v8", &legacy)?));
        Ok(gate)
    }

    pub fn diagnostic(config: &Value) -> Result<Self> {
        let mut data = vec![0u8; DIAGNOSTIC_SIZE];
        for (offset, value) in [
            (0, 0x53434431),
            (4, 1),
            (8, DIAGNOSTIC_SIZE as u32),
            (12, 256),
        ] {
            put32(&mut data, offset, value);
        }
        let session_id = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)?
            .as_nanos() as u64
            ^ u64::from(std::process::id());
        data[16..24].copy_from_slice(&session_id.max(1).to_le_bytes());
        if let Some(adapter) = config.get("render_adapter").filter(|v| !v.is_null()) {
            let low = adapter["low"]
                .as_u64()
                .ok_or("render_adapter.low is required")?;
            let high = adapter["high"]
                .as_i64()
                .ok_or("render_adapter.high is required")?;
            put32(&mut data, 24, 1);
            put32(&mut data, 28, low.try_into()?);
            put32(&mut data, 32, i32::try_from(high)? as u32);
        }
        Self::create("Global\\SBMSCaptureDiagnostics-v1", &data)
    }

    fn stable(&self, sequence_offset: usize) -> Option<Vec<u8>> {
        // Driver writes use InterlockedExchange; read the same aligned sequence atomically.
        let sequence = unsafe {
            &*self
                .view
                .Value
                .cast::<u8>()
                .add(sequence_offset)
                .cast::<AtomicI32>()
        };
        let before = sequence.load(Ordering::Acquire);
        if before & 1 != 0 {
            return None;
        }
        let mut data = vec![0u8; self.size];
        for (index, byte) in data.iter_mut().enumerate() {
            *byte = unsafe { std::ptr::read_volatile(self.view.Value.cast::<u8>().add(index)) };
        }
        std::sync::atomic::fence(Ordering::AcqRel);
        (sequence.load(Ordering::Acquire) == before).then_some(data)
    }

    pub fn identity(&self) -> Option<(u32, i32, u32)> {
        self.stable(336)
            .filter(|data| u32_at(data, 336) != 0)
            .map(|data| (u32_at(&data, 340), i32_at(&data, 344), u32_at(&data, 348)))
            .or_else(|| {
                self.compatibility_gate
                    .as_ref()
                    .and_then(|gate| gate.identity())
            })
    }

    pub fn observations(&self, last_serial: &mut u64) -> Value {
        let Some(data) = self.stable(36) else {
            return json!({"snapshot":"write_in_progress"});
        };
        let written = u64_at(&data, 40);
        let mut events = Vec::new();
        for serial in ((*last_serial + 1).max(written.saturating_sub(255)))..=written {
            if serial == 0 {
                continue;
            }
            let offset = 1632 + ((serial - 1) % 256) as usize * 64;
            if u64_at(&data, offset) != serial {
                continue;
            }
            events.push(json!({"serial":serial,"tick":u64_at(&data,offset+8),"device_generation":u64_at(&data,offset+16),"generation":u64_at(&data,offset+24),"connector":u32_at(&data,offset+32),"kind":u32_at(&data,offset+36),"result":i32_at(&data,offset+40),"stage":u32_at(&data,offset+44),"adapter_luid":{"low":u32_at(&data,offset+48),"high":i32_at(&data,offset+52)},"detail":u64_at(&data,offset+56)}));
        }
        let lost_since_read = written.saturating_sub(*last_serial).saturating_sub(256);
        *last_serial = written;
        let monitors: Vec<_> = (0..16).filter_map(|i| {
            let o = 96+i*96;
            if u64_at(&data,o)==0 {return None;}
            Some(json!({"connector":i,"generation":u64_at(&data,o),"assign_tick":u64_at(&data,o+8),"first_frame_tick":u64_at(&data,o+16),"last_event_tick":u64_at(&data,o+24),"active":u32_at(&data,o+32),"state":u32_at(&data,o+36),"assigned_adapter_luid":{"low":u32_at(&data,o+40),"high":i32_at(&data,o+44)},"actual_adapter_luid":{"low":u32_at(&data,o+48),"high":i32_at(&data,o+52)},"create_device_result":i32_at(&data,o+56),"set_device_result":i32_at(&data,o+60),"last_error":i32_at(&data,o+64),"last_stage":u32_at(&data,o+68),"assign_count":u32_at(&data,o+72),"unassign_count":u32_at(&data,o+76),"stop_count":u32_at(&data,o+80),"first_frame_count":u32_at(&data,o+84),"flags":u32_at(&data,o+88),"driver_build":u32_at(&data,o+92)}))
        }).collect();
        json!({"session_id":u64_at(&data,16),"driver_build":u32_at(&data,188),"sequence":i32_at(&data,36),"written_events":written,"overwritten_events":u64_at(&data,48),"lost_since_read":lost_since_read,"device_generation":u64_at(&data,64),"driver_pid":u32_at(&data,72),"device_status":i32_at(&data,76),"last_event_tick":u64_at(&data,80),"device_attached":u32_at(&data,88),"dropped_events":u32_at(&data,92),"monitors":monitors,"events":events})
    }
}

impl Drop for Mapping {
    fn drop(&mut self) {
        unsafe {
            let _ = UnmapViewOfFile(self.view);
            let _ = CloseHandle(self.handle);
        }
    }
}

fn gate_data(version: u32, width: u32, height: u32, numerator: u32, denominator: u32) -> Vec<u8> {
    let mut data = vec![0u8; if version == 9 { 856 } else { 592 }];
    for (offset, value) in [
        (0, if version == 9 { 0x53424739 } else { 0x53424738 }),
        (4, version),
        (8, 1),
        (20, width),
        (24, height),
        (28, numerator),
        (32, denominator),
    ] {
        put32(&mut data, offset, value);
    }
    data
}

fn put32(data: &mut [u8], offset: usize, value: u32) {
    data[offset..offset + 4].copy_from_slice(&value.to_le_bytes());
}
fn u32_at(data: &[u8], offset: usize) -> u32 {
    u32::from_le_bytes(data[offset..offset + 4].try_into().unwrap())
}
fn i32_at(data: &[u8], offset: usize) -> i32 {
    u32_at(data, offset) as i32
}
fn u64_at(data: &[u8], offset: usize) -> u64 {
    u64::from_le_bytes(data[offset..offset + 8].try_into().unwrap())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn product_gate_accepts_receipts_from_old_or_new_driver() {
        let mut current = gate_data(9, 1314, 2336, 200, 1);
        let mut legacy = gate_data(8, 1314, 2336, 200, 1);
        assert_eq!(current.len(), 856);
        assert_eq!(legacy.len(), 592);
        assert_eq!((u32_at(&current, 0), u32_at(&current, 4)), (0x53424739, 9));
        assert_eq!((u32_at(&legacy, 0), u32_at(&legacy, 4)), (0x53424738, 8));
        assert_eq!(&current[8..592], &legacy[8..]);
        assert!(current[592..].iter().all(|byte| *byte == 0));

        for version in [8, 9] {
            let receipt = if version == 9 {
                &mut current
            } else {
                &mut legacy
            };
            put32(receipt, 336, 2);
            put32(receipt, 340, version);
            put32(receipt, 348, 256);
            let prefix = format!(
                "Local\\SBMS-gate-compat-test-{}-{version}",
                std::process::id()
            );
            let mut mapping = Mapping::create(&format!("{prefix}-v9"), &current).unwrap();
            mapping.compatibility_gate = Some(Box::new(
                Mapping::create(&format!("{prefix}-v8"), &legacy).unwrap(),
            ));
            assert_eq!(mapping.identity(), Some((version, 0, 256)));
            drop(mapping);
            // Both named reservations are released with the owning session.
            assert!(Mapping::create(&format!("{prefix}-v9"), &current).is_ok());
            assert!(Mapping::create(&format!("{prefix}-v8"), &legacy).is_ok());
            put32(
                if version == 9 {
                    &mut current
                } else {
                    &mut legacy
                },
                336,
                0,
            );
        }
    }

    #[test]
    fn gate_requires_complete_atomic_publication() {
        let mut data = vec![0u8; 592];
        put32(&mut data, 336, 1);
        put32(&mut data, 340, 0xaabbccdd);
        put32(&mut data, 344, (-2i32) as u32);
        put32(&mut data, 348, 256);
        let mapping = Mapping::create(
            &format!("Local\\SBMS-gate-receipt-test-{}", std::process::id()),
            &data,
        )
        .unwrap();
        assert_eq!(mapping.identity(), None);
        let seq = unsafe { &*mapping.view.Value.cast::<u8>().add(336).cast::<AtomicI32>() };
        seq.store(2, Ordering::Release);
        assert_eq!(mapping.identity(), Some((0xaabbccdd, -2, 256)));
        assert!(
            Mapping::create(
                &format!("Local\\SBMS-gate-receipt-test-{}", std::process::id()),
                &data
            )
            .is_err()
        );
    }

    #[test]
    fn driver_ring_reports_wraparound_loss_and_never_replays_consumed_events() {
        let mut data = vec![0u8; DIAGNOSTIC_SIZE];
        put32(&mut data, 36, 2);
        put32(&mut data, 188, 0x20261105);
        data[40..48].copy_from_slice(&260u64.to_le_bytes());
        for serial in 5u64..=260 {
            let o = 1632 + ((serial - 1) % 256) as usize * 64;
            data[o..o + 8].copy_from_slice(&serial.to_le_bytes());
            put32(&mut data, o + 40, 0x887a0026);
        }
        let mapping = Mapping::create(
            &format!("Local\\SBMS-driver-ring-test-{}", std::process::id()),
            &data,
        )
        .unwrap();
        let mut serial = 0;
        let first = mapping.observations(&mut serial);
        assert_eq!(first["driver_build"], 0x20261105u32);
        assert_eq!(first["lost_since_read"], 4);
        assert_eq!(first["events"].as_array().unwrap().len(), 256);
        assert_eq!(first["events"][0]["serial"], 5);
        assert_eq!(first["events"][255]["serial"], 260);
        assert_eq!(first["events"][0]["result"], 0x887a0026u32 as i32);
        assert_eq!(serial, 260);
        assert!(
            mapping.observations(&mut serial)["events"]
                .as_array()
                .unwrap()
                .is_empty()
        );
    }
}
