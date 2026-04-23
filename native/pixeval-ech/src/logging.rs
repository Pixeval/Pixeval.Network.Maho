use log::{LevelFilter, info, warn};
use reqwest::Method;
use reqwest::header::HeaderMap;
use simplelog::{ConfigBuilder, WriteLogger};
use std::fs::{File, OpenOptions, create_dir_all};
use std::path::{Path, PathBuf};
use std::sync::OnceLock;

pub static CONFIGURED_LOG_FULL_PATH: OnceLock<PathBuf> = OnceLock::new();
pub static CONFIGURED_LOG_LEVEL: OnceLock<LevelFilter> = OnceLock::new();
static LOGGER_INIT: OnceLock<Result<(), String>> = OnceLock::new();

const DEFAULT_LOG_FILE_NAME: &str = "http-requests.log";

fn log_file_path() -> PathBuf {
    if CONFIGURED_LOG_FULL_PATH.get().is_some() {
        return CONFIGURED_LOG_FULL_PATH.get().unwrap().clone();
    }
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("logs")
        .join(DEFAULT_LOG_FILE_NAME)
}

fn create_file_with_parents(path: impl AsRef<Path>) -> Result<File, std::io::Error> {
    let path = path.as_ref();

    if let Some(parent) = path.parent() {
        create_dir_all(parent)?;
    }

    File::create(path)
}

fn log_level() -> LevelFilter {
    CONFIGURED_LOG_LEVEL
        .get()
        .copied()
        .unwrap_or(LevelFilter::Info)
}

pub fn set_configured_logger_path(full_path: &str) -> Result<(), String> {
    let path = Path::new(full_path);
    if !path.exists() {
        create_file_with_parents(path)
            .map_err(|e| format!("Failed to create logger directory: {}", e))?;
    }
    CONFIGURED_LOG_FULL_PATH
        .set(path.to_path_buf())
        .map_err(|_| String::from("Logger path has already been set and cannot be changed"))?;
    Ok(())
}

pub fn set_configured_log_level(level: LevelFilter) -> Result<(), String> {
    if let Some(existing) = CONFIGURED_LOG_LEVEL.get() {
        if *existing == level {
            return Ok(());
        }

        return Err(String::from(
            "Logger level has already been set and cannot be changed",
        ));
    }

    CONFIGURED_LOG_LEVEL
        .set(level)
        .map_err(|_| String::from("Logger level has already been set and cannot be changed"))?;

    Ok(())
}

pub(crate) fn init_logger() -> Result<(), String> {
    LOGGER_INIT
        .get_or_init(logger_initializer)
        .as_ref()
        .map(|_| ())
        .map_err(|err| err.clone())
}

pub fn is_logger_initialized() -> bool {
    LOGGER_INIT.get().is_some()
}

pub fn has_logger_configuration() -> bool {
    CONFIGURED_LOG_FULL_PATH.get().is_some() || CONFIGURED_LOG_LEVEL.get().is_some()
}

fn logger_initializer() -> Result<(), String> {
    let log_path = log_file_path();
    if let Some(parent) = log_path.parent() {
        create_dir_all(parent).map_err(|err| {
            format!(
                "failed to create log directory {}: {}",
                parent.display(),
                err
            )
        })?;
    }

    let log_file = OpenOptions::new()
        .create(true)
        .append(true)
        .open(&log_path)
        .map_err(|err| format!("failed to open log file {}: {}", log_path.display(), err))?;

    let config = ConfigBuilder::new().set_time_format_rfc3339().build();

    WriteLogger::init(log_level(), config, log_file)
        .map_err(|err| format!("failed to initialize logger: {}", err))
}

pub fn log_http_request(
    request_id: u64,
    method: &Method,
    url: &str,
    headers: &HeaderMap,
    body: &[u8],
) -> Result<(), String> {
    init_logger()?;

    let formatted_headers = if headers.is_empty() {
        String::from("<none>")
    } else {
        headers
            .iter()
            .map(|(name, value)| {
                let value = value.to_str().unwrap_or("<non-utf8 header value>");
                format!("{}: {}", name.as_str(), value)
            })
            .collect::<Vec<String>>()
            .join("\n")
    };

    let body_text = if body.is_empty() {
        String::from("<empty>")
    } else {
        match std::str::from_utf8(body) {
            Ok(text) => text.to_owned(),
            Err(err) => format!(
                "<invalid utf-8: {}; lossy={}>",
                err,
                String::from_utf8_lossy(body)
            ),
        }
    };

    info!(
        "Outgoing HTTP request\nrequest_id: {}\nmethod: {}\nurl: {}\nheaders:\n{}\nbody_len: {}\nbody_utf8:\n{}",
        request_id,
        method,
        url,
        formatted_headers,
        body.len(),
        body_text
    );

    Ok(())
}

pub fn log_pinvoke_call(invocation: &str) -> Result<(), String> {
    init_logger()?;
    info!("{}", invocation);
    Ok(())
}

pub fn log_managed_error(target: &str, error: &str) -> Result<(), String> {
    init_logger()?;
    warn!("{} -> managed error: {}", target, error);
    Ok(())
}
