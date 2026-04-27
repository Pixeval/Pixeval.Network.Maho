use crate::marshal;
use log::{warn};
use reqwest::Method;
use reqwest::header::HeaderMap;
use std::ffi::c_char;
use std::sync::{Arc, Mutex};

#[repr(i32)]
pub enum LoggerLevel {
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,
    Trace = 4,
}

pub type ManagedLoggingCallback = unsafe extern "C" fn(LoggerLevel, *const c_char);

pub struct ManagedLogger {
    pub callback: Arc<Mutex<ManagedLoggingCallback>>
}

impl ManagedLogger {
    pub fn log_http_request(
        &self,
        request_id: u64,
        method: &Method,
        url: &str,
        headers: &HeaderMap,
        body: &[u8],
    ) -> Result<(), String> {
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

        let logging_content = format!(
            "Outgoing HTTP request\nrequest_id: {}\nmethod: {}\nurl: {}\nheaders:\n{}\nbody_len: {}\nbody_utf8:\n{}",
            request_id,
            method,
            url,
            formatted_headers,
            body.len(),
            body_text
        );
        let callback = self.callback.lock().unwrap();
        unsafe { (*callback)(LoggerLevel::Info, marshal::into_raw_c_string(logging_content)); }
        Ok(())
    }

    pub fn log_pinvoke_call(&self, invocation: &str) -> Result<(), String> {
        let callback = self.callback.lock().unwrap();
        unsafe { (*callback)(LoggerLevel::Info, marshal::into_raw_c_string(format!("{}", invocation))) }
        Ok(())
    }

    pub fn log_managed_error(&self, target: &str, error: &str) -> Result<(), String> {
        warn!("{} -> managed error: {}", target, error);
        Ok(())
    }

    pub fn log_ffi_error(&self, target: &str, error: impl AsRef<str>) {
        if let Err(err) = self.log_managed_error(target, error.as_ref()) {
            eprintln!("Failed to log managed-facing error: {}", err);
        }
    }

    pub fn log_ffi_call(&self, invocation: impl AsRef<str>) {
        if let Err(err) = self.log_pinvoke_call(invocation.as_ref()) {
            eprintln!("Failed to log P/Invoke call: {}", err);
        }
    }
}