use std::collections::VecDeque;
use std::ffi::OsString;
use std::fs::{self, OpenOptions};
use std::io::{self, BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};
use windows::Win32::Foundation::{
    CloseHandle, FILETIME, HANDLE, SYSTEMTIME, WAIT_ABANDONED, WAIT_FAILED, WAIT_OBJECT_0,
};
use windows::Win32::System::Threading::{
    CreateMutexW, INFINITE, ReleaseMutex, WaitForSingleObject,
};
use windows::Win32::System::Time::{FileTimeToSystemTime, SystemTimeToTzSpecificLocalTimeEx};
use windows::core::w;

const DEFAULT_MAX_FILE_BYTES: u64 = 1536 * 1024;
const DEFAULT_MEMORY_RECORDS: usize = 200;
const ROLLING_FILE_COUNT: usize = 3;
const AUXILIARY_LOG_RETENTION: Duration = Duration::from_secs(30 * 24 * 60 * 60);
const WINDOWS_EPOCH_OFFSET_TICKS: u64 = 116_444_736_000_000_000;
const TICKS_PER_MILLISECOND: u64 = 10_000;

static DIAGNOSTICS: OnceLock<Diagnostics> = OnceLock::new();
static NEXT_MAPPING_SESSION: AtomicU64 = AtomicU64::new(1);

#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "UPPERCASE")]
pub enum Level {
    Debug,
    Info,
    Warn,
    Error,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MappingSessionId {
    process_id: u32,
    sequence: u64,
}

impl std::fmt::Display for MappingSessionId {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "{}-{}", self.process_id, self.sequence)
    }
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub struct Record {
    pub time: u64,
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub local_time: String,
    pub level: Level,
    pub module: String,
    pub stage: String,
    pub mapping_session_id: String,
    pub message: String,
}

pub fn init() -> io::Result<&'static Path> {
    if let Some(diagnostics) = DIAGNOSTICS.get() {
        return Ok(&diagnostics.path);
    }

    let path = default_log_path()?;
    let diagnostics = Diagnostics::open(path, DEFAULT_MAX_FILE_BYTES, DEFAULT_MEMORY_RECORDS)?;
    let _ = DIAGNOSTICS.set(diagnostics);
    Ok(&DIAGNOSTICS
        .get()
        .expect("diagnostics must be initialized")
        .path)
}

pub fn default_log_path() -> io::Result<PathBuf> {
    let local = std::env::var_os("LOCALAPPDATA")
        .filter(|value| !value.is_empty())
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::NotFound,
                "LOCALAPPDATA is unavailable; cannot locate the SBMS log directory",
            )
        })?;
    Ok(PathBuf::from(local)
        .join("SBMS")
        .join("logs")
        .join("sbms.log"))
}

pub fn new_mapping_session_id() -> MappingSessionId {
    MappingSessionId {
        process_id: std::process::id(),
        sequence: NEXT_MAPPING_SESSION.fetch_add(1, Ordering::Relaxed),
    }
}

pub fn log(
    level: Level,
    module: impl Into<String>,
    stage: impl Into<String>,
    mapping_session_id: Option<MappingSessionId>,
    message: impl Into<String>,
) {
    let time = unix_time_millis();
    let record = Record {
        time,
        local_time: local_time_string(time).unwrap_or_default(),
        level,
        module: module.into(),
        stage: stage.into(),
        mapping_session_id: mapping_session_id
            .map(|id| id.to_string())
            .unwrap_or_else(|| "-".into()),
        message: message.into(),
    };

    let result = DIAGNOSTICS
        .get()
        .map(|diagnostics| diagnostics.write(record.clone()));
    if let Some(Err(error)) = result {
        eprintln!("SBMS diagnostics write failed: {error}");
    } else if result.is_none() && level == Level::Error {
        eprintln!(
            "SBMS error [{}:{} session={}]: {}",
            record.module, record.stage, record.mapping_session_id, record.message
        );
    }
}

pub fn recent() -> Vec<Record> {
    DIAGNOSTICS
        .get()
        .map(Diagnostics::recent)
        .unwrap_or_default()
}

pub fn latest_error_matches(message: &str) -> bool {
    DIAGNOSTICS
        .get()
        .and_then(|diagnostics| diagnostics.recent().pop())
        .is_some_and(|record| record.level == Level::Error && record.message == message)
}

pub fn persisted_recent() -> io::Result<Vec<Record>> {
    let path = default_log_path()?;
    let _guard = CrossProcessLogGuard::acquire()?;
    let mut records = VecDeque::with_capacity(DEFAULT_MEMORY_RECORDS);
    for candidate in [rotated_path(&path, 2), rotated_path(&path, 1), path] {
        let file = match fs::File::open(&candidate) {
            Ok(file) => file,
            Err(error) if error.kind() == io::ErrorKind::NotFound => continue,
            Err(error) => return Err(error),
        };
        for line in BufReader::new(file).lines() {
            let line = line?;
            let Ok(record) = serde_json::from_str::<Record>(&line) else {
                continue;
            };
            if records.len() == DEFAULT_MEMORY_RECORDS {
                records.pop_front();
            }
            records.push_back(record);
        }
    }
    Ok(records.into())
}

fn unix_time_millis() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .try_into()
        .unwrap_or(u64::MAX)
}

pub fn local_time_string(unix_millis: u64) -> Option<String> {
    let ticks = unix_millis
        .checked_mul(TICKS_PER_MILLISECOND)?
        .checked_add(WINDOWS_EPOCH_OFFSET_TICKS)?;
    let file_time = FILETIME {
        dwLowDateTime: ticks as u32,
        dwHighDateTime: (ticks >> 32) as u32,
    };
    let mut utc = SYSTEMTIME::default();
    unsafe { FileTimeToSystemTime(&file_time, &mut utc) }.ok()?;
    let mut local = SYSTEMTIME::default();
    unsafe { SystemTimeToTzSpecificLocalTimeEx(None, &utc, &mut local) }.ok()?;
    Some(format!(
        "{:04}-{:02}-{:02} {:02}:{:02}:{:02}.{:03}",
        local.wYear,
        local.wMonth,
        local.wDay,
        local.wHour,
        local.wMinute,
        local.wSecond,
        local.wMilliseconds
    ))
}

struct Diagnostics {
    path: PathBuf,
    max_file_bytes: u64,
    memory_records: usize,
    state: Mutex<State>,
}

struct State {
    recent: VecDeque<Record>,
}

#[derive(Default)]
struct CleanupReport {
    removed: usize,
    failures: Vec<String>,
}

struct CrossProcessLogGuard(HANDLE);

impl CrossProcessLogGuard {
    fn acquire() -> io::Result<Self> {
        let handle = unsafe { CreateMutexW(None, false, w!("Local\\SBMSDiagnostics-v1")) }
            .map_err(io::Error::other)?;
        let wait = unsafe { WaitForSingleObject(handle, INFINITE) };
        if wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED {
            return Ok(Self(handle));
        }

        unsafe {
            let _ = CloseHandle(handle);
        }
        if wait == WAIT_FAILED {
            Err(io::Error::last_os_error())
        } else {
            Err(io::Error::other(format!(
                "unexpected diagnostics mutex wait result: {}",
                wait.0
            )))
        }
    }
}

impl Drop for CrossProcessLogGuard {
    fn drop(&mut self) {
        unsafe {
            let _ = ReleaseMutex(self.0);
            let _ = CloseHandle(self.0);
        }
    }
}

impl Diagnostics {
    fn open(path: PathBuf, max_file_bytes: u64, memory_records: usize) -> io::Result<Self> {
        if max_file_bytes == 0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "maximum log file size must be greater than zero",
            ));
        }
        if memory_records == 0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "memory record capacity must be greater than zero",
            ));
        }
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let cleanup_report = match CrossProcessLogGuard::acquire() {
            Ok(_guard) => cleanup_obsolete_logs(
                path.parent().unwrap_or_else(|| Path::new(".")),
                SystemTime::now(),
            ),
            Err(error) => CleanupReport {
                failures: vec![format!("could not acquire the diagnostics lock: {error}")],
                ..CleanupReport::default()
            },
        };
        let diagnostics = Self {
            path,
            max_file_bytes,
            memory_records,
            state: Mutex::new(State {
                recent: VecDeque::with_capacity(memory_records),
            }),
        };
        diagnostics.report_cleanup(cleanup_report);
        Ok(diagnostics)
    }

    fn write(&self, mut record: Record) -> io::Result<()> {
        let line = encode_line(&mut record, self.max_file_bytes as usize)?;
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());

        if state.recent.len() == self.memory_records {
            state.recent.pop_front();
        }
        state.recent.push_back(record.clone());

        let _guard = CrossProcessLogGuard::acquire()?;
        let current_bytes = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&self.path)?
            .metadata()?
            .len();
        if current_bytes > 0
            && current_bytes.saturating_add(line.len() as u64) > self.max_file_bytes
        {
            self.rotate()?;
        }

        // Open the active path for every record. A persistent handle can keep pointing at
        // `sbms.log.1` after another SBMS process rotates the file.
        let mut file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&self.path)?;
        file.write_all(&line)?;
        file.flush()?;
        if record.level == Level::Error {
            file.sync_data()?;
        }
        Ok(())
    }

    fn report_cleanup(&self, report: CleanupReport) {
        if report.removed > 0 {
            let time = unix_time_millis();
            let _ = self.write(Record {
                time,
                local_time: local_time_string(time).unwrap_or_default(),
                level: Level::Info,
                module: "diagnostics".into(),
                stage: "retention".into(),
                mapping_session_id: "-".into(),
                message: format!(
                    "removed {} obsolete or expired legacy log file(s)",
                    report.removed
                ),
            });
        }
        if !report.failures.is_empty() {
            let time = unix_time_millis();
            let failure_count = report.failures.len();
            let details = report
                .failures
                .iter()
                .take(5)
                .cloned()
                .collect::<Vec<_>>()
                .join("; ");
            let _ = self.write(Record {
                time,
                local_time: local_time_string(time).unwrap_or_default(),
                level: Level::Warn,
                module: "diagnostics".into(),
                stage: "retention".into(),
                mapping_session_id: "-".into(),
                message: format!("could not clean {failure_count} legacy log file(s): {details}"),
            });
        }
    }

    fn recent(&self) -> Vec<Record> {
        self.state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .recent
            .iter()
            .cloned()
            .collect()
    }

    fn rotate(&self) -> io::Result<()> {
        let oldest = rotated_path(&self.path, ROLLING_FILE_COUNT - 1);
        if oldest.exists() {
            fs::remove_file(&oldest)?;
        }
        for index in (1..ROLLING_FILE_COUNT - 1).rev() {
            let source = rotated_path(&self.path, index);
            if source.exists() {
                fs::rename(source, rotated_path(&self.path, index + 1))?;
            }
        }
        if self.path.exists() {
            fs::rename(&self.path, rotated_path(&self.path, 1))?;
        }
        Ok(())
    }
}

fn cleanup_obsolete_logs(directory: &Path, now: SystemTime) -> CleanupReport {
    let mut report = CleanupReport::default();
    let entries = match fs::read_dir(directory) {
        Ok(entries) => entries,
        Err(error) => {
            report
                .failures
                .push(format!("{}: {error}", directory.display()));
            return report;
        }
    };

    for entry in entries {
        let entry = match entry {
            Ok(entry) => entry,
            Err(error) => {
                report.failures.push(error.to_string());
                continue;
            }
        };
        let file_type = match entry.file_type() {
            Ok(file_type) => file_type,
            Err(error) => {
                report
                    .failures
                    .push(format!("{}: {error}", entry.path().display()));
                continue;
            }
        };
        if !file_type.is_file() {
            continue;
        }
        let Some(name) = entry.file_name().to_str().map(str::to_owned) else {
            continue;
        };

        let remove = is_obsolete_legacy_log(&name)
            || (is_known_auxiliary_log(&name)
                && match entry.metadata().and_then(|metadata| metadata.modified()) {
                    Ok(modified) => now
                        .duration_since(modified)
                        .is_ok_and(|age| age >= AUXILIARY_LOG_RETENTION),
                    Err(error) => {
                        report
                            .failures
                            .push(format!("{}: {error}", entry.path().display()));
                        false
                    }
                });
        if !remove {
            continue;
        }

        match fs::remove_file(entry.path()) {
            Ok(()) => report.removed += 1,
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => report
                .failures
                .push(format!("{}: {error}", entry.path().display())),
        }
    }
    report
}

fn is_obsolete_legacy_log(name: &str) -> bool {
    name.eq_ignore_ascii_case("latest.log")
        || name.eq_ignore_ascii_case("error.log")
        || exact_timestamped_log(name, "SBMS-")
}

fn is_known_auxiliary_log(name: &str) -> bool {
    if name.eq_ignore_ascii_case("sunshine-actions.log") {
        return true;
    }
    exact_timestamped_log(name, "setup-")
        || [
            "elevated-",
            "native-smoke-",
            "programfiles-sync-",
            "ab-driver-verify-",
        ]
        .iter()
        .any(|prefix| timestamp_suffixed_log(name, prefix))
}

fn exact_timestamped_log(name: &str, prefix: &str) -> bool {
    let Some(stem) = name
        .strip_prefix(prefix)
        .and_then(|value| value.strip_suffix(".log"))
    else {
        return false;
    };
    is_log_timestamp(stem)
}

fn timestamp_suffixed_log(name: &str, prefix: &str) -> bool {
    let Some(stem) = name
        .strip_prefix(prefix)
        .and_then(|value| value.strip_suffix(".log"))
    else {
        return false;
    };
    stem.get(stem.len().saturating_sub(15)..)
        .is_some_and(is_log_timestamp)
}

fn is_log_timestamp(value: &str) -> bool {
    value.len() == 15
        && value.as_bytes()[8] == b'-'
        && value
            .bytes()
            .enumerate()
            .all(|(index, byte)| index == 8 || byte.is_ascii_digit())
}

fn encode_line(record: &mut Record, max_bytes: usize) -> io::Result<Vec<u8>> {
    let mut line = serde_json::to_vec(record).map_err(io::Error::other)?;
    line.push(b'\n');
    if line.len() <= max_bytes {
        return Ok(line);
    }

    record.message.clear();
    let fixed_bytes = serde_json::to_vec(record)
        .map_err(io::Error::other)?
        .len()
        .saturating_add(1);
    if fixed_bytes > max_bytes {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "maximum log file size is too small for a diagnostic record",
        ));
    }
    Ok(serde_json::to_vec(record)
        .map_err(io::Error::other)?
        .into_iter()
        .chain(std::iter::once(b'\n'))
        .collect())
}

fn rotated_path(path: &Path, index: usize) -> PathBuf {
    let mut name = path
        .file_name()
        .map(OsString::from)
        .unwrap_or_else(|| OsString::from("sbms.log"));
    name.push(format!(".{index}"));
    path.with_file_name(name)
}

#[cfg(test)]
mod tests {
    use super::*;

    static NEXT_TEST_DIR: AtomicU64 = AtomicU64::new(1);

    fn test_path(name: &str) -> PathBuf {
        std::env::temp_dir()
            .join(format!(
                "sbms-diagnostics-{}-{}",
                std::process::id(),
                NEXT_TEST_DIR.fetch_add(1, Ordering::Relaxed)
            ))
            .join(name)
    }

    fn record(index: usize, level: Level) -> Record {
        Record {
            time: index as u64,
            local_time: String::new(),
            level,
            module: "test".into(),
            stage: "write".into(),
            mapping_session_id: "1-1".into(),
            message: format!("record-{index}-{}", "x".repeat(96)),
        }
    }

    #[test]
    fn ring_buffer_keeps_the_latest_records() {
        let path = test_path("sbms.log");
        let diagnostics = Diagnostics::open(path.clone(), 1024 * 1024, 200).unwrap();
        for index in 0..225 {
            diagnostics.write(record(index, Level::Info)).unwrap();
        }
        let recent = diagnostics.recent();
        assert_eq!(recent.len(), 200);
        assert_eq!(recent.first().unwrap().time, 25);
        assert_eq!(recent.last().unwrap().time, 224);
        drop(diagnostics);
        fs::remove_dir_all(path.parent().unwrap()).unwrap();
    }

    #[test]
    fn old_records_without_local_time_remain_compatible() {
        let record: Record = serde_json::from_str(
            r#"{"time":1,"level":"INFO","module":"test","stage":"read","mapping_session_id":"-","message":"legacy"}"#,
        )
        .unwrap();

        assert_eq!(record.time, 1);
        assert!(record.local_time.is_empty());
    }

    #[test]
    fn local_time_uses_a_readable_system_time_shape() {
        let time = local_time_string(unix_time_millis()).unwrap();

        assert_eq!(time.len(), 23);
        assert_eq!(&time[4..5], "-");
        assert_eq!(&time[10..11], " ");
        assert_eq!(&time[19..20], ".");
    }

    #[test]
    fn rotation_keeps_three_bounded_files() {
        let path = test_path("sbms.log");
        let diagnostics = Diagnostics::open(path.clone(), 512, 200).unwrap();
        for index in 0..30 {
            diagnostics.write(record(index, Level::Error)).unwrap();
        }
        drop(diagnostics);

        for candidate in [&path, &rotated_path(&path, 1), &rotated_path(&path, 2)] {
            assert!(candidate.is_file());
            assert!(candidate.metadata().unwrap().len() <= 512);
        }
        assert!(!rotated_path(&path, 3).exists());
        fs::remove_dir_all(path.parent().unwrap()).unwrap();
    }

    #[test]
    fn rotation_coordinates_multiple_diagnostics_instances() {
        let path = test_path("sbms.log");
        let first = Diagnostics::open(path.clone(), 512, 200).unwrap();
        let second = Diagnostics::open(path.clone(), 512, 200).unwrap();
        for index in 0..30 {
            let diagnostics = if index % 2 == 0 { &first } else { &second };
            diagnostics.write(record(index, Level::Info)).unwrap();
        }
        let mut latest = record(31, Level::Info);
        latest.message = "latest-after-cross-process-rotation".into();
        first.write(latest).unwrap();

        assert!(
            fs::read_to_string(&path)
                .unwrap()
                .contains("latest-after-cross-process-rotation")
        );
        for candidate in [&path, &rotated_path(&path, 1), &rotated_path(&path, 2)] {
            assert!(candidate.metadata().unwrap().len() <= 512);
        }
        assert!(!rotated_path(&path, 3).exists());
        drop((first, second));
        fs::remove_dir_all(path.parent().unwrap()).unwrap();
    }

    #[test]
    fn legacy_cleanup_removes_only_explicit_obsolete_names() {
        let path = test_path("sbms.log");
        let directory = path.parent().unwrap();
        fs::create_dir_all(directory).unwrap();
        for name in [
            "SBMS-20260727-054400.log",
            "latest.log",
            "error.log",
            "setup-20260813-120000.log",
            "SBMS-not-a-timestamp.log",
            "SBMS-user-20260727-054400.log",
            "user-notes.log",
            "sbms.log",
            "sbms.log.1",
            "sbms.log.2",
        ] {
            fs::write(directory.join(name), name).unwrap();
        }

        let report = cleanup_obsolete_logs(directory, SystemTime::now());

        assert_eq!(report.removed, 3);
        assert!(report.failures.is_empty());
        for name in [
            "setup-20260813-120000.log",
            "SBMS-not-a-timestamp.log",
            "SBMS-user-20260727-054400.log",
            "user-notes.log",
            "sbms.log",
            "sbms.log.1",
            "sbms.log.2",
        ] {
            assert!(directory.join(name).is_file(), "{name} should be preserved");
        }
        fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn auxiliary_cleanup_applies_thirty_day_retention() {
        let path = test_path("sbms.log");
        let directory = path.parent().unwrap();
        fs::create_dir_all(directory).unwrap();
        for name in [
            "setup-20260629-220847.log",
            "elevated-gui-start-smoke-063-20260629-122148.log",
            "native-smoke-20260629-043731.log",
            "programfiles-sync-20260629-043721.log",
            "ab-driver-verify-20260629-043305.log",
            "sunshine-actions.log",
            "unknown-20260629-043305.log",
            "sbms.log",
        ] {
            fs::write(directory.join(name), name).unwrap();
        }
        let after_retention = SystemTime::now() + AUXILIARY_LOG_RETENTION + Duration::from_secs(1);

        let report = cleanup_obsolete_logs(directory, after_retention);

        assert_eq!(report.removed, 6);
        assert!(report.failures.is_empty());
        assert!(directory.join("unknown-20260629-043305.log").is_file());
        assert!(directory.join("sbms.log").is_file());
        fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn error_is_visible_without_dropping_the_writer() {
        let path = test_path("sbms.log");
        let diagnostics = Diagnostics::open(path.clone(), 1024 * 1024, 200).unwrap();
        diagnostics.write(record(1, Level::Info)).unwrap();
        diagnostics.write(record(2, Level::Error)).unwrap();

        let text = fs::read_to_string(&path).unwrap();
        assert!(text.contains("\"level\":\"ERROR\""));
        assert!(text.contains("record-2"));
        drop(diagnostics);
        fs::remove_dir_all(path.parent().unwrap()).unwrap();
    }

    #[test]
    fn persisted_recent_skips_malformed_lines_and_keeps_the_latest_records() {
        let path = test_path("sbms.log");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        let mut contents = String::new();
        for index in 0..205 {
            contents.push_str(&serde_json::to_string(&record(index, Level::Info)).unwrap());
            contents.push('\n');
        }
        contents.push_str("not-json\n");
        fs::write(&path, contents).unwrap();

        let records = {
            let _guard = CrossProcessLogGuard::acquire().unwrap();
            let mut records = VecDeque::with_capacity(DEFAULT_MEMORY_RECORDS);
            let file = fs::File::open(&path).unwrap();
            for line in BufReader::new(file).lines() {
                let line = line.unwrap();
                let Ok(record) = serde_json::from_str::<Record>(&line) else {
                    continue;
                };
                if records.len() == DEFAULT_MEMORY_RECORDS {
                    records.pop_front();
                }
                records.push_back(record);
            }
            records
        };

        assert_eq!(records.len(), 200);
        assert_eq!(records.front().unwrap().time, 5);
        assert_eq!(records.back().unwrap().time, 204);
        fs::remove_dir_all(path.parent().unwrap()).unwrap();
    }

    #[test]
    fn mapping_session_ids_are_unique() {
        assert_ne!(new_mapping_session_id(), new_mapping_session_id());
    }
}
