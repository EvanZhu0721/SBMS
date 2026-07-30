use std::collections::VecDeque;
use std::ffi::OsString;
use std::fs::{self, File, OpenOptions};
use std::io::{self, BufWriter, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::{SystemTime, UNIX_EPOCH};

use serde::Serialize;

const DEFAULT_MAX_FILE_BYTES: u64 = 1536 * 1024;
const DEFAULT_MEMORY_RECORDS: usize = 200;
const ROLLING_FILE_COUNT: usize = 3;

static DIAGNOSTICS: OnceLock<Diagnostics> = OnceLock::new();
static NEXT_MAPPING_SESSION: AtomicU64 = AtomicU64::new(1);

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
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

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct Record {
    pub time: u64,
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
    let record = Record {
        time: unix_time_millis(),
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

fn unix_time_millis() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .try_into()
        .unwrap_or(u64::MAX)
}

struct Diagnostics {
    path: PathBuf,
    max_file_bytes: u64,
    memory_records: usize,
    state: Mutex<State>,
}

struct State {
    writer: BufWriter<File>,
    current_bytes: u64,
    recent: VecDeque<Record>,
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
        let file = OpenOptions::new().create(true).append(true).open(&path)?;
        let current_bytes = file.metadata()?.len();
        Ok(Self {
            path,
            max_file_bytes,
            memory_records,
            state: Mutex::new(State {
                writer: BufWriter::new(file),
                current_bytes,
                recent: VecDeque::with_capacity(memory_records),
            }),
        })
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

        if state.current_bytes > 0
            && state.current_bytes.saturating_add(line.len() as u64) > self.max_file_bytes
        {
            state.writer.flush()?;
            self.rotate()?;
            state.writer = BufWriter::new(
                OpenOptions::new()
                    .create(true)
                    .append(true)
                    .open(&self.path)?,
            );
            state.current_bytes = 0;
        }

        state.writer.write_all(&line)?;
        state.current_bytes = state.current_bytes.saturating_add(line.len() as u64);
        state.writer.flush()?;
        if record.level == Level::Error {
            state.writer.get_ref().sync_data()?;
        }
        Ok(())
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
    fn mapping_session_ids_are_unique() {
        assert_ne!(new_mapping_session_id(), new_mapping_session_id());
    }
}
