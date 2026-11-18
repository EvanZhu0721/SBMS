use std::error::Error;
use std::mem::size_of;
use std::os::windows::ffi::OsStrExt;
use std::path::Path;

use windows::Win32::Foundation::{HWND, INVALID_HANDLE_VALUE};
use windows::Win32::Security::WinTrust::{
    WINTRUST_ACTION_GENERIC_VERIFY_V2, WINTRUST_DATA, WINTRUST_DATA_0, WINTRUST_FILE_INFO,
    WTD_CHOICE_FILE, WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT, WTD_STATEACTION_CLOSE,
    WTD_STATEACTION_VERIFY, WTD_UI_NONE, WTHelperGetProvCertFromChain,
    WTHelperGetProvSignerFromChain, WTHelperProvDataFromStateData, WinVerifyTrust,
};
use windows::Win32::Storage::FileSystem::{
    GetFileVersionInfoSizeW, GetFileVersionInfoW, VS_FFI_SIGNATURE, VS_FIXEDFILEINFO,
    VerQueryValueW,
};
use windows::core::{PCWSTR, w};

pub(super) fn verify_installer(
    path: &Path,
    expected_version: [u16; 4],
) -> Result<(), Box<dyn Error + Send + Sync>> {
    let executable = std::env::current_exe()?;
    let current_signer = trusted_signer(&executable).map_err(|error| {
        format!("Automatic updates require a trusted signature on the running executable: {error}")
    })?;
    let installer_signer = trusted_signer(path)
        .map_err(|error| format!("Installer signature verification failed: {error}"))?;
    if installer_signer != current_signer {
        return Err("Installer signer does not match the running executable".into());
    }

    let version = file_version(path)?;
    if version != expected_version {
        return Err(format!(
            "Installer version {}.{}.{}.{} does not match expected {}.{}.{}.{}",
            version[0],
            version[1],
            version[2],
            version[3],
            expected_version[0],
            expected_version[1],
            expected_version[2],
            expected_version[3],
        )
        .into());
    }
    Ok(())
}

struct TrustState(WINTRUST_DATA);

impl Drop for TrustState {
    fn drop(&mut self) {
        self.0.dwStateAction = WTD_STATEACTION_CLOSE;
        let mut action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
        unsafe {
            WinVerifyTrust(
                HWND(INVALID_HANDLE_VALUE.0),
                &mut action,
                (&mut self.0 as *mut WINTRUST_DATA).cast(),
            );
        }
    }
}

fn trusted_signer(path: &Path) -> Result<Vec<u8>, Box<dyn Error + Send + Sync>> {
    let wide_path: Vec<u16> = path.as_os_str().encode_wide().chain(Some(0)).collect();
    let mut file = WINTRUST_FILE_INFO {
        cbStruct: size_of::<WINTRUST_FILE_INFO>() as u32,
        pcwszFilePath: PCWSTR(wide_path.as_ptr()),
        ..Default::default()
    };
    // Keep the file and its path alive until the verification state is closed.
    let mut state = TrustState(WINTRUST_DATA {
        cbStruct: size_of::<WINTRUST_DATA>() as u32,
        dwUIChoice: WTD_UI_NONE,
        dwUnionChoice: WTD_CHOICE_FILE,
        Anonymous: WINTRUST_DATA_0 { pFile: &mut file },
        dwStateAction: WTD_STATEACTION_VERIFY,
        dwProvFlags: WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT,
        ..Default::default()
    });
    let mut action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    let status = unsafe {
        WinVerifyTrust(
            HWND(INVALID_HANDLE_VALUE.0),
            &mut action,
            (&mut state.0 as *mut WINTRUST_DATA).cast(),
        )
    };
    // WinVerifyTrust returns LONG, not HRESULT: only zero means success.
    if status != 0 {
        return Err(format!("Authenticode verification failed (0x{:08X})", status as u32).into());
    }

    unsafe {
        let provider = WTHelperProvDataFromStateData(state.0.hWVTStateData);
        if provider.is_null() {
            return Err("Verified signature provider data is unavailable".into());
        }
        let signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if signer.is_null() {
            return Err("Verified signature signer is unavailable".into());
        }
        let certificate = WTHelperGetProvCertFromChain(signer, 0)
            .as_ref()
            .and_then(|certificate| certificate.pCert.as_ref())
            .ok_or("Verified signer certificate is unavailable")?;
        if certificate.pbCertEncoded.is_null() || certificate.cbCertEncoded == 0 {
            return Err("Verified signer certificate is empty".into());
        }
        // Copy the actual verified leaf certificate before closing WinTrust state.
        Ok(std::slice::from_raw_parts(
            certificate.pbCertEncoded,
            certificate.cbCertEncoded as usize,
        )
        .to_vec())
    }
}

fn file_version(path: &Path) -> Result<[u16; 4], Box<dyn Error + Send + Sync>> {
    let wide_path: Vec<u16> = path.as_os_str().encode_wide().chain(Some(0)).collect();
    let size = unsafe { GetFileVersionInfoSizeW(PCWSTR(wide_path.as_ptr()), None) };
    if size == 0 {
        return Err(format!(
            "Cannot read installer version: {}",
            std::io::Error::last_os_error()
        )
        .into());
    }
    let mut data = vec![0u8; size as usize];
    unsafe {
        GetFileVersionInfoW(
            PCWSTR(wide_path.as_ptr()),
            None,
            size,
            data.as_mut_ptr().cast(),
        )
        .map_err(|error| format!("Cannot read installer version: {error}"))?;
        let mut fixed_info = std::ptr::null_mut();
        let mut fixed_info_len = 0;
        if !VerQueryValueW(
            data.as_ptr().cast(),
            w!("\\"),
            &mut fixed_info,
            &mut fixed_info_len,
        )
        .as_bool()
            || fixed_info.is_null()
            || fixed_info_len < size_of::<VS_FIXEDFILEINFO>() as u32
        {
            return Err("Installer fixed file version is unavailable".into());
        }
        let version = fixed_info.cast::<VS_FIXEDFILEINFO>().read_unaligned();
        if version.dwSignature != VS_FFI_SIGNATURE as u32 {
            return Err("Installer fixed file version is invalid".into());
        }
        Ok([
            (version.dwFileVersionMS >> 16) as u16,
            version.dwFileVersionMS as u16,
            (version.dwFileVersionLS >> 16) as u16,
            version.dwFileVersionLS as u16,
        ])
    }
}
