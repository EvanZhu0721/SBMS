use std::ffi::c_void;
use std::fmt;
use std::fs::{self, File};
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::ptr;

use serde::Deserialize;
use windows::Win32::Networking::WinHttp::{
    WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_ACCESS_TYPE_NAMED_PROXY, WINHTTP_FLAG_SECURE,
    WINHTTP_OPTION_REDIRECT_POLICY, WINHTTP_OPTION_REDIRECT_POLICY_DISALLOW_HTTPS_TO_HTTP,
    WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_QUERY_STATUS_CODE, WinHttpCloseHandle, WinHttpConnect,
    WinHttpOpen, WinHttpOpenRequest, WinHttpQueryHeaders, WinHttpReadData, WinHttpReceiveResponse,
    WinHttpSendRequest, WinHttpSetOption, WinHttpSetTimeouts,
};
use windows::core::{PCWSTR, w};

use super::diagnostics::{self, Level};
use super::update_trust::verify_installer;

const METADATA_PATH: &str = "/EvanZhu0721/SBMS/releases/latest/download/update.json";
const FALLBACK_METADATA_PATH: &str = "/downloads/EvanZhu0721/SBMS/latest/update.json";
const CURRENT_REVISION: &str = include_str!("../UPDATE_REVISION");
const MAX_METADATA_BYTES: u64 = 64 * 1024;
const MAX_INSTALLER_BYTES: u64 = 512 * 1024 * 1024;
const SKIPPED_UPDATE_FILE: &str = "skipped-update";

#[derive(Clone, Debug)]
pub(crate) struct ReadyUpdate {
    pub(crate) version: String,
    pub(crate) revision: u16,
    pub(crate) path: PathBuf,
    pub(crate) size: u64,
}

#[derive(Debug)]
pub(crate) struct UpdateError {
    pub(crate) stage: &'static str,
    message: String,
    allow_fallback: bool,
}

impl UpdateError {
    fn new(stage: &'static str, message: impl fmt::Display) -> Self {
        Self {
            stage,
            message: message.to_string(),
            allow_fallback: false,
        }
    }

    fn remote(stage: &'static str, message: impl fmt::Display) -> Self {
        Self {
            allow_fallback: true,
            ..Self::new(stage, message)
        }
    }
}

impl fmt::Display for UpdateError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{}: {}", self.stage, self.message)
    }
}

impl std::error::Error for UpdateError {}

pub(crate) fn skip_update(cache: &Path, update: &ReadyUpdate) -> Result<(), UpdateError> {
    fs::create_dir_all(cache).map_err(|error| UpdateError::new("skip-save", error))?;
    fs::write(
        cache.join(SKIPPED_UPDATE_FILE),
        format!("{}-r{}", update.version, update.revision),
    )
    .map_err(|error| UpdateError::new("skip-save", error))
}

pub(crate) fn is_update_skipped(
    cache: &Path,
    version: &str,
    revision: u16,
) -> Result<bool, UpdateError> {
    match fs::read_to_string(cache.join(SKIPPED_UPDATE_FILE)) {
        Ok(skipped) => Ok(skipped == format!("{version}-r{revision}")),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(false),
        Err(error) => Err(UpdateError::new("skip-read", error)),
    }
}

#[derive(Deserialize)]
struct Metadata {
    version: String,
    revision: u16,
    size: u64,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Source {
    Primary,
    Fallback,
}

impl Source {
    fn host(self) -> &'static str {
        match self {
            Self::Primary => "github.com",
            Self::Fallback => "blog.imzhuyi.fan",
        }
    }

    fn metadata_path(self) -> &'static str {
        match self {
            Self::Primary => METADATA_PATH,
            Self::Fallback => FALLBACK_METADATA_PATH,
        }
    }

    fn asset_path(self, version: &str, filename: &str) -> String {
        match self {
            Self::Primary => format!("/EvanZhu0721/SBMS/releases/download/v{version}/{filename}"),
            Self::Fallback => format!("/downloads/EvanZhu0721/SBMS/releases/v{version}/{filename}"),
        }
    }

    fn log_error(self, error: &UpdateError) {
        diagnostics::log(
            Level::Warn,
            "update",
            error.stage,
            None,
            format!("source={self:?} host={} error={error}", self.host()),
        );
    }
}

fn with_fallback<T>(
    source: Source,
    mut attempt: impl FnMut(Source) -> Result<T, UpdateError>,
) -> Result<(Source, T), UpdateError> {
    match attempt(source) {
        Ok(value) => Ok((source, value)),
        Err(error) => {
            source.log_error(&error);
            if source != Source::Primary || !error.allow_fallback {
                return Err(error);
            }
            attempt(Source::Fallback)
                .map(|value| (Source::Fallback, value))
                .inspect_err(|error| Source::Fallback.log_error(error))
        }
    }
}

/// Synchronous network and signature work; call from the update worker thread.
pub(crate) fn check_and_download(cache: &Path) -> Result<Option<ReadyUpdate>, UpdateError> {
    let revision = CURRENT_REVISION
        .trim()
        .parse::<u16>()
        .map_err(|error| UpdateError::new("current-version", error))?;
    let current = parse_version(env!("CARGO_PKG_VERSION"), revision)
        .map_err(|error| UpdateError::new("current-version", error))?;
    let (metadata_source, (mut client, update)) = with_fallback(Source::Primary, |source| {
        let client = HttpClient::new(source)?;
        let request = client.get(source.metadata_path(), "metadata-request")?;
        let mut bytes = Vec::new();
        read_body(&request, &mut bytes, MAX_METADATA_BYTES, "metadata-read")?;
        Ok((client, available_update(&bytes, current)?))
    })?;
    let Some((metadata, expected_version)) = update else {
        return Ok(None);
    };
    if is_update_skipped(cache, &metadata.version, metadata.revision)? {
        return Ok(None);
    }
    fs::create_dir_all(cache).map_err(|error| UpdateError::new("cache-create", error))?;
    let filename = format!(
        "SBMS-Setup-{}-x64-r{}.exe",
        metadata.version, metadata.revision
    );
    let ready = ReadyUpdate {
        version: metadata.version,
        revision: metadata.revision,
        path: cache.join(&filename),
        size: metadata.size,
    };
    match fs::metadata(&ready.path) {
        Ok(info) => {
            let verification = if info.len() != ready.size {
                Err(UpdateError::new(
                    "cache-verify",
                    format!("expected {} bytes, found {}", ready.size, info.len()),
                ))
            } else {
                verify_installer(&ready.path, expected_version)
                    .map_err(|error| UpdateError::new("cache-verify", error))
            };
            if verification.is_err() {
                // A later scheduled check can replace this file; do not retry this check.
                let _ = fs::remove_file(&ready.path);
            }
            verification?;
            return Ok(Some(ready));
        }
        Err(error) if error.kind() == io::ErrorKind::NotFound => {}
        Err(error) => return Err(UpdateError::new("cache-read", error)),
    }

    let partial = cache.join(format!("{filename}.part"));
    let downloaded = (|| {
        let (download_source, ()) = with_fallback(metadata_source, |source| {
            if source != metadata_source {
                client = HttpClient::new(source)?;
            }
            let asset_path = source.asset_path(&ready.version, &filename);
            let request = client.get(&asset_path, "download-request")?;
            // Recreate the partial file for each source; never append across sources.
            let mut output = File::create(&partial)
                .map_err(|error| UpdateError::new("download-create", error))?;
            let received = read_body(&request, &mut output, ready.size, "download-read")?;
            if received != ready.size {
                return Err(UpdateError::remote(
                    "download-size",
                    format!("expected {} bytes, received {received}", ready.size),
                ));
            }
            Ok(())
        })?;
        verify_installer(&partial, expected_version)
            .map_err(|error| UpdateError::new("download-verify", error))
            .inspect_err(|error| download_source.log_error(error))?;
        fs::rename(&partial, &ready.path).map_err(|error| UpdateError::new("cache-publish", error))
    })();
    if downloaded.is_err() {
        let _ = fs::remove_file(&partial);
    }
    downloaded?;
    Ok(Some(ready))
}

fn parse_version(version: &str, revision: u16) -> Result<[u16; 4], &'static str> {
    let mut parts = version.split('.');
    let mut parsed = [0, 0, 0, revision];
    for part in &mut parsed[..3] {
        let text = parts.next().ok_or("version must contain three numbers")?;
        if text.is_empty() || !text.bytes().all(|byte| byte.is_ascii_digit()) {
            return Err("version must contain only decimal numbers separated by dots");
        }
        *part = text
            .parse()
            .map_err(|_| "version component exceeds 65535")?;
    }
    if parts.next().is_some() {
        return Err("version must contain three numbers");
    }
    Ok(parsed)
}

fn available_update(
    bytes: &[u8],
    current: [u16; 4],
) -> Result<Option<(Metadata, [u16; 4])>, UpdateError> {
    let metadata: Metadata = serde_json::from_slice(bytes)
        .map_err(|error| UpdateError::remote("metadata-parse", error))?;
    let version = parse_version(&metadata.version, metadata.revision)
        .map_err(|error| UpdateError::remote("metadata-version", error))?;
    if metadata.size == 0 || metadata.size > MAX_INSTALLER_BYTES {
        return Err(UpdateError::remote(
            "metadata-size",
            format!("installer size must be between 1 and {MAX_INSTALLER_BYTES} bytes"),
        ));
    }
    Ok((version > current).then_some((metadata, version)))
}

struct HttpHandle(*mut c_void);

impl HttpHandle {
    fn new(raw: *mut c_void, stage: &'static str) -> Result<Self, UpdateError> {
        if raw.is_null() {
            Err(UpdateError::remote(
                stage,
                windows::core::Error::from_thread(),
            ))
        } else {
            Ok(Self(raw))
        }
    }
}

impl Drop for HttpHandle {
    fn drop(&mut self) {
        unsafe {
            let _ = WinHttpCloseHandle(self.0);
        }
    }
}

struct HttpClient {
    // Fields drop in declaration order: close the connection before its session.
    connection: HttpHandle,
    _session: HttpHandle,
}

fn proxy_bypass(value: &str) -> String {
    value
        .split(',')
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(|value| {
            // WinHTTP rejects unbracketed IPv6 in its bypass list.
            if value.parse::<std::net::Ipv6Addr>().is_ok() {
                format!("[{value}]")
            } else {
                value.to_owned()
            }
        })
        .collect::<Vec<_>>()
        .join(";")
}

impl HttpClient {
    fn new(source: Source) -> Result<Self, UpdateError> {
        // An explicit process proxy takes precedence over Windows' automatic settings.
        let proxy = std::env::var("HTTPS_PROXY")
            .ok()
            .filter(|value| !value.trim().is_empty())
            .map(|value| {
                value
                    .trim()
                    .strip_prefix("http://")
                    .unwrap_or(value.trim())
                    .trim_end_matches('/')
                    .encode_utf16()
                    .chain(Some(0))
                    .collect::<Vec<_>>()
            });
        let bypass = proxy_bypass(&std::env::var("NO_PROXY").unwrap_or_default())
            .encode_utf16()
            .chain(Some(0))
            .collect::<Vec<_>>();
        let session = HttpHandle::new(
            unsafe {
                WinHttpOpen(
                    w!("SBMS updater"),
                    if proxy.is_some() {
                        WINHTTP_ACCESS_TYPE_NAMED_PROXY
                    } else {
                        WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY
                    },
                    proxy
                        .as_ref()
                        .map_or(PCWSTR::null(), |value| PCWSTR(value.as_ptr())),
                    if proxy.is_some() && bypass.len() > 1 {
                        PCWSTR(bypass.as_ptr())
                    } else {
                        PCWSTR::null()
                    },
                    0,
                )
            },
            "http-session",
        )?;
        unsafe { WinHttpSetTimeouts(session.0, 10_000, 15_000, 15_000, 30_000) }
            .map_err(|error| UpdateError::remote("http-timeouts", error))?;
        let host: Vec<u16> = source.host().encode_utf16().chain(Some(0)).collect();
        let connection = HttpHandle::new(
            unsafe { WinHttpConnect(session.0, PCWSTR(host.as_ptr()), 443, 0) },
            "http-connect",
        )?;
        Ok(Self {
            connection,
            _session: session,
        })
    }

    fn get(&self, path: &str, stage: &'static str) -> Result<HttpHandle, UpdateError> {
        let path: Vec<u16> = path.encode_utf16().chain(Some(0)).collect();
        let request = HttpHandle::new(
            unsafe {
                WinHttpOpenRequest(
                    self.connection.0,
                    w!("GET"),
                    PCWSTR(path.as_ptr()),
                    PCWSTR::null(),
                    PCWSTR::null(),
                    ptr::null(),
                    WINHTTP_FLAG_SECURE,
                )
            },
            stage,
        )?;
        // GitHub redirects release assets to HTTPS storage; never allow an HTTP hop.
        unsafe {
            WinHttpSetOption(
                Some(request.0.cast_const()),
                WINHTTP_OPTION_REDIRECT_POLICY,
                Some(&WINHTTP_OPTION_REDIRECT_POLICY_DISALLOW_HTTPS_TO_HTTP.to_ne_bytes()),
            )
        }
        .map_err(|error| UpdateError::remote(stage, format!("redirect policy: {error}")))?;
        unsafe { WinHttpSendRequest(request.0, None, None, 0, 0, 0) }
            .map_err(|error| UpdateError::remote(stage, format!("send: {error}")))?;
        unsafe { WinHttpReceiveResponse(request.0, ptr::null_mut()) }
            .map_err(|error| UpdateError::remote(stage, format!("receive: {error}")))?;
        let mut status = 0_u32;
        let mut status_bytes = size_of::<u32>() as u32;
        unsafe {
            WinHttpQueryHeaders(
                request.0,
                WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                PCWSTR::null(),
                Some((&mut status as *mut u32).cast()),
                &mut status_bytes,
                ptr::null_mut(),
            )
        }
        .map_err(|error| UpdateError::remote(stage, format!("status: {error}")))?;
        if status != 200 {
            return Err(UpdateError::remote(stage, format!("HTTP {status}")));
        }
        Ok(request)
    }
}

fn read_body(
    request: &HttpHandle,
    output: &mut impl Write,
    limit: u64,
    stage: &'static str,
) -> Result<u64, UpdateError> {
    let mut buffer = [0_u8; 64 * 1024];
    let mut received = 0_u64;
    loop {
        let mut count = 0;
        unsafe {
            WinHttpReadData(
                request.0,
                buffer.as_mut_ptr().cast(),
                buffer.len() as u32,
                &mut count,
            )
        }
        .map_err(|error| UpdateError::remote(stage, format!("read: {error}")))?;
        if count == 0 {
            return Ok(received);
        }
        received += u64::from(count);
        if received > limit {
            return Err(UpdateError::remote(
                stage,
                format!("response exceeds {limit} bytes"),
            ));
        }
        output
            .write_all(&buffer[..count as usize])
            .map_err(|error| UpdateError::new(stage, format!("write: {error}")))?;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn skipped_update_persists_only_for_the_exact_version_and_revision() {
        let cache = std::env::temp_dir().join(format!(
            "sbms-skipped-update-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos(),
        ));
        let update = ReadyUpdate {
            version: "2.3.0".into(),
            revision: 7,
            path: cache.join("installer.exe"),
            size: 1024,
        };
        assert!(!is_update_skipped(&cache, &update.version, update.revision).unwrap());
        skip_update(&cache, &update).unwrap();
        drop(update);
        assert!(is_update_skipped(&cache, "2.3.0", 7).unwrap());
        assert!(!is_update_skipped(&cache, "2.3.0", 8).unwrap());
        assert!(!is_update_skipped(&cache, "2.3.1", 7).unwrap());
        fs::remove_file(cache.join(SKIPPED_UPDATE_FILE)).unwrap();
        fs::remove_dir(cache).unwrap();
    }

    #[test]
    fn invalid_metadata_uses_fallback_for_metadata_and_download() {
        let mut metadata_sources = Vec::new();
        let (source, update) = with_fallback(Source::Primary, |source| {
            metadata_sources.push(source);
            let bytes: &[u8] = match source {
                Source::Primary => b"invalid JSON",
                Source::Fallback => br#"{"version":"2.3.0","revision":2,"size":1024}"#,
            };
            available_update(bytes, [2, 2, 0, 1])
        })
        .unwrap();
        assert_eq!(metadata_sources, [Source::Primary, Source::Fallback]);
        assert_eq!(source, Source::Fallback);
        let (metadata, version) = update.unwrap();
        assert_eq!(version, [2, 3, 0, 2]);
        assert_eq!(metadata.size, 1024);

        let mut download_sources = Vec::new();
        let result = with_fallback(source, |source| {
            download_sources.push(source);
            Err::<(), _>(UpdateError::remote("download-request", "unavailable"))
        });
        assert!(result.is_err());
        assert_eq!(download_sources, [Source::Fallback]);
    }

    #[test]
    fn download_failover_keeps_the_release_and_stops_after_fallback() {
        let (metadata, _) = available_update(
            br#"{"version":"2.3.0","revision":7,"size":1024}"#,
            [2, 2, 0, 1],
        )
        .unwrap()
        .unwrap();
        let filename = format!(
            "SBMS-Setup-{}-x64-r{}.exe",
            metadata.version, metadata.revision
        );
        let mut attempts = Vec::new();
        let result = with_fallback(Source::Primary, |source| {
            attempts.push((
                source.asset_path(&metadata.version, &filename),
                metadata.size,
            ));
            Err::<(), _>(UpdateError::remote("download-read", "connection lost"))
        });
        assert!(result.is_err());
        assert_eq!(
            attempts,
            [
                (
                    "/EvanZhu0721/SBMS/releases/download/v2.3.0/SBMS-Setup-2.3.0-x64-r7.exe"
                        .to_owned(),
                    1024,
                ),
                (
                    "/downloads/EvanZhu0721/SBMS/releases/v2.3.0/SBMS-Setup-2.3.0-x64-r7.exe"
                        .to_owned(),
                    1024,
                ),
            ]
        );
    }

    #[test]
    fn local_or_verification_errors_and_no_update_do_not_change_source() {
        for stage in ["download-create", "download-write", "download-verify"] {
            let mut sources = Vec::new();
            let result = with_fallback(Source::Primary, |source| {
                sources.push(source);
                Err::<(), _>(UpdateError::new(stage, "failed"))
            });
            assert!(result.is_err());
            assert_eq!(sources, [Source::Primary], "{stage}");
        }

        let mut sources = Vec::new();
        let (_, update) = with_fallback(Source::Primary, |source| {
            sources.push(source);
            available_update(
                br#"{"version":"2.2.0","revision":1,"size":1024}"#,
                [2, 2, 0, 1],
            )
        })
        .unwrap();
        assert!(update.is_none());
        assert_eq!(sources, [Source::Primary]);
    }

    #[test]
    fn proxy_bypass_preserves_ipv6_loopback_in_winhttp_format() {
        assert_eq!(
            proxy_bypass("127.0.0.1, localhost, ::1,"),
            "127.0.0.1;localhost;[::1]"
        );
    }

    #[test]
    fn version_rejects_non_release_strings_and_overflow() {
        assert_eq!(parse_version("65535.0.1", 2), Ok([65535, 0, 1, 2]));
        for invalid in [
            "2.2",
            "2.2.0.1",
            "2..0",
            "v2.2.0",
            "2.2.0-beta",
            "2.2.0/../x",
            "+2.2.0",
            "2.2.65536",
        ] {
            assert!(parse_version(invalid, 1).is_err(), "{invalid}");
        }
    }

    #[test]
    fn update_order_uses_revision_only_after_the_release_version() {
        for (version, revision, newer) in [
            ("2.2.0", 1, false),
            ("2.2.0", 2, true),
            ("2.1.9", 65535, false),
            ("2.2.1", 0, true),
        ] {
            let bytes = serde_json::to_vec(&serde_json::json!({
                "version": version, "revision": revision, "size": 1024,
            }))
            .unwrap();
            assert_eq!(
                available_update(&bytes, [2, 2, 0, 1]).unwrap().is_some(),
                newer
            );
        }
    }

    #[test]
    fn metadata_rejects_unbounded_sizes_and_invalid_revision() {
        for size in [0, MAX_INSTALLER_BYTES + 1] {
            let bytes = serde_json::to_vec(&serde_json::json!({
                "version": "2.3.0", "revision": 1, "size": size,
            }))
            .unwrap();
            assert!(available_update(&bytes, [2, 2, 0, 1]).is_err());
        }
        assert!(
            available_update(
                br#"{"version":"2.3.0","revision":65536,"size":1024}"#,
                [2, 2, 0, 1],
            )
            .is_err()
        );
    }
}
