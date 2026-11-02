use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

use crate::diagnostics::{self, Level, Record};

use super::QuickAccess;

pub(super) fn populate_diagnostics(ui: &QuickAccess) {
    let (records, read_error) = match diagnostics::persisted_recent() {
        Ok(records) => (records, None),
        Err(error) => (diagnostics::recent(), Some(error)),
    };
    ui.set_diagnostic_summary(format_last_error(&records).into());
    ui.set_diagnostic_text(format_recent_diagnostics(&records).into());
    let path = diagnostics::default_log_path()
        .map(|path| path.display().to_string())
        .unwrap_or_else(|error| format!("Log path unavailable: {error}"));
    ui.set_diagnostic_path(path.into());
    ui.set_diagnostic_action_error(
        read_error
            .map(|error| format!("Couldn’t read the log file; showing this process only: {error}"))
            .unwrap_or_default()
            .into(),
    );
}

pub(super) fn format_recent_diagnostics(records: &[Record]) -> String {
    format_recent_diagnostics_at(records)
}

fn format_recent_diagnostics_at(records: &[Record]) -> String {
    let mut selected = records.iter().collect::<Vec<_>>();
    selected.sort_by_key(|record| std::cmp::Reverse(record.time));
    selected.truncate(40);
    if selected.is_empty() {
        return "No diagnostic records are available.".into();
    }

    selected
        .into_iter()
        .map(|record| {
            let local_time = if record.local_time.is_empty() {
                diagnostics::local_time_string(record.time)
                    .unwrap_or_else(|| record.time.to_string())
            } else {
                record.local_time.clone()
            };
            let session = if record.mapping_session_id == "-" {
                String::new()
            } else {
                format!(" · {}", record.mapping_session_id)
            };
            let message = record.message.replace(['\r', '\n'], " ");
            format!(
                "[{local_time}] {} · {}/{}{}\n{}",
                diagnostic_level(record),
                record.module,
                record.stage,
                session,
                message
            )
        })
        .collect::<Vec<_>>()
        .join("\n\n")
}

fn format_last_error(records: &[Record]) -> String {
    records
        .iter()
        .filter(|record| record.level == Level::Error)
        .max_by_key(|record| record.time)
        .map(|record| {
            format!(
                "{} / {}: {}",
                record.module,
                record.stage,
                record.message.replace(['\r', '\n'], " ")
            )
        })
        .unwrap_or_default()
}

fn diagnostic_level(record: &Record) -> &'static str {
    match record.level {
        Level::Debug => "DEBUG",
        Level::Info => "INFO",
        Level::Warn => "WARN",
        Level::Error => "ERROR",
    }
}

pub(super) fn open_log_folder() -> Result<(), String> {
    let path = diagnostics::default_log_path()
        .map_err(|error| format!("Couldn’t locate the log folder: {error}"))?;
    let folder = ensure_log_folder(&path)?;
    Command::new("explorer.exe")
        .arg(folder)
        .spawn()
        .map(|_| ())
        .map_err(|error| format!("Couldn’t open the log folder: {error}"))
}

fn ensure_log_folder(log_path: &Path) -> Result<PathBuf, String> {
    let folder = log_path
        .parent()
        .filter(|folder| !folder.as_os_str().is_empty())
        .ok_or_else(|| "The diagnostic log path has no parent folder".to_string())?;
    fs::create_dir_all(folder).map_err(|error| {
        format!(
            "Couldn’t create the log folder at {}: {error}",
            folder.display()
        )
    })?;
    Ok(folder.to_path_buf())
}

#[cfg(test)]
mod tests {

    use super::*;

    fn record(time: u64, level: Level, session: &str, message: &str) -> Record {
        Record {
            time,
            local_time: String::new(),
            level,
            module: "mapping".into(),
            stage: "test".into(),
            mapping_session_id: session.into(),
            message: message.into(),
        }
    }

    #[test]
    fn last_error_selects_latest_error_and_ignores_healthy_records() {
        assert_eq!(
            format_last_error(&[record(1_000, Level::Info, "1-1", "healthy")]),
            ""
        );

        let records = [
            record(1_000, Level::Error, "1-1", "first"),
            record(2_000, Level::Info, "1-2", "healthy"),
            record(3_000, Level::Error, "1-3", "latest"),
        ];

        assert_eq!(format_last_error(&records), "mapping / test: latest");
    }
}
