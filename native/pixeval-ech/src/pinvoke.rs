use crate::client_builder::build_ech_client;
use crate::client_pool::{CLIENT_ECH_POOL, ClientPool};
use crate::logging::{
    has_logger_configuration, is_logger_initialized, log_http_request, log_managed_error,
    log_pinvoke_call, set_configured_log_level, set_configured_logger_path,
};
use crate::regex::RegexKey;
use crate::resolution::set_dns_resolution_url;
use regex::Regex;
use reqwest::Method;
use reqwest::header::{HeaderMap, HeaderName, HeaderValue};
use std::collections::HashMap;
use std::error::Error;
use std::ffi::{CStr, CString, c_char, c_void};
use std::fmt;
use std::net::IpAddr;
use std::str::FromStr;
use std::sync::OnceLock;
use tokio::runtime::{Builder, Runtime};

#[repr(C)]
pub struct NameResolution {
    pub regex: *const c_char,
    pub ips: *const *const c_char,
    pub ips_len: usize,
}

#[repr(C)]
pub struct LoggerConfigurationResult {
    pub success: bool,
    pub error_reason: *const c_char,
}

#[repr(i32)]
pub enum LoggerLevel {
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,
    Trace = 4,
}

#[repr(C)]
pub struct FFIHttpRequestMessage {
    pub request_id: u64,
    pub url: *const c_char,
    pub method: *const c_char,
    pub headers: *const *const c_char,
    pub headers_len: usize,
    pub body: *const u8,
    pub body_len: usize,
}

#[repr(C)]
pub struct FFIHttpResponseMessage {
    pub premature_death: bool,
    pub premature_death_reason: *const c_char,
    pub status_code: u16,
    pub headers: *const *const c_char,
    pub headers_len: usize,
    pub body: *const u8,
    pub body_len: usize,
}

pub type HttpCompletionCallback =
extern "C" fn(id: u64, response: FFIHttpResponseMessage, user_data: *const c_void);

pub type ClientInitializationCallback = extern "C" fn(success: bool, error_reason: *const c_char);
pub type DnsResolutionUrlConfigurationCallback =
extern "C" fn(success: bool, error_reason: *const c_char);

pub enum RequestError {
    EchFailure(String),
    RequestFailure(String),
}

fn log_ffi_call(invocation: impl AsRef<str>) {
    if let Err(err) = log_pinvoke_call(invocation.as_ref()) {
        eprintln!("Failed to log P/Invoke call: {}", err);
    }
}

fn log_ffi_error(target: &str, error: impl AsRef<str>) {
    if let Err(err) = log_managed_error(target, error.as_ref()) {
        eprintln!("Failed to log managed-facing error: {}", err);
    }
}

fn format_ptr<T>(ptr: *const T) -> String {
    format!("{:p}", ptr)
}

fn format_mut_ptr<T>(ptr: *mut T) -> String {
    format!("{:p}", ptr)
}

fn format_c_string_arg(ptr: *const c_char) -> String {
    if ptr.is_null() {
        return String::from("null");
    }

    match unsafe { CStr::from_ptr(ptr) }.to_str() {
        Ok(value) => format!("{:?}", value),
        Err(_) => format!("{}:<invalid-utf8>", format_ptr(ptr)),
    }
}

fn read_c_string(ptr: *const c_char, arg_name: &str) -> Result<&str, String> {
    if ptr.is_null() {
        return Err(format!("{} pointer is null", arg_name));
    }

    unsafe { CStr::from_ptr(ptr) }
        .to_str()
        .map_err(|err| format!("{} is not valid UTF-8: {}", arg_name, err))
}

fn logger_configuration_error(error: impl Into<String>) -> LoggerConfigurationResult {
    let error = error.into();
    if is_logger_initialized() || has_logger_configuration() {
        log_ffi_error("LoggerConfigurationResult", &error);
    } else {
        eprintln!("LoggerConfigurationResult -> managed error: {}", error);
    }
    LoggerConfigurationResult {
        success: false,
        error_reason: into_raw_c_string(error),
    }
}

fn callback_error(callback: impl FnOnce(*const c_char), target: &str, error: impl Into<String>) {
    let error = error.into();
    log_ffi_error(target, &error);
    callback(into_raw_c_string(error));
}

#[unsafe(no_mangle)]
pub extern "C" fn configure_logger_path(path: *const c_char) -> LoggerConfigurationResult {
    let invocation = format!("configure_logger_path({})", format_c_string_arg(path));
    if path.is_null() {
        return logger_configuration_error("Log file path pointer is null");
    }

    match read_c_string(path, "Log file path") {
        Ok(path) => match set_configured_logger_path(path) {
            Ok(_) => {
                log_ffi_call(invocation);
                LoggerConfigurationResult {
                    success: true,
                    error_reason: std::ptr::null(),
                }
            }
            Err(err) => logger_configuration_error(err),
        },
        Err(err) => logger_configuration_error(err),
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn configure_logger_level(level: i32) -> LoggerConfigurationResult {
    let invocation = format!("configure_logger_level({})", level);
    let level = match level {
        x if x == LoggerLevel::Error as i32 => log::LevelFilter::Error,
        x if x == LoggerLevel::Warn as i32 => log::LevelFilter::Warn,
        x if x == LoggerLevel::Info as i32 => log::LevelFilter::Info,
        x if x == LoggerLevel::Debug as i32 => log::LevelFilter::Debug,
        x if x == LoggerLevel::Trace as i32 => log::LevelFilter::Trace,
        _ => {
            return logger_configuration_error(format!("Invalid logger level value: {}", level));
        }
    };

    match set_configured_log_level(level) {
        Ok(_) => {
            log_ffi_call(invocation);
            LoggerConfigurationResult {
                success: true,
                error_reason: std::ptr::null(),
            }
        }
        Err(err) => logger_configuration_error(err),
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn configure_dns_resolution_url(
    url: *const c_char,
    callback: DnsResolutionUrlConfigurationCallback,
) {
    log_ffi_call(format!(
        "configure_dns_resolution_url({}, {:p})",
        format_c_string_arg(url),
        callback as *const ()
    ));
    if url.is_null() {
        callback_error(
            |error| callback(false, error),
            "configure_dns_resolution_url callback",
            "DNS resolution URL pointer is null",
        );
        return;
    }

    match read_c_string(url, "DNS resolution URL") {
        Ok(url) => match set_dns_resolution_url(url) {
            Ok(_) => callback(true, std::ptr::null()),
            Err(err) => callback_error(
                |error| callback(false, error),
                "configure_dns_resolution_url callback",
                err,
            ),
        },
        Err(err) => callback_error(
            |error| callback(false, error),
            "configure_dns_resolution_url callback",
            err,
        ),
    }
}

fn into_raw_c_string(message: impl Into<String>) -> *const c_char {
    CString::new(message.into()).unwrap().into_raw()
}

fn ffi_runtime() -> &'static Runtime {
    static RUNTIME: OnceLock<Runtime> = OnceLock::new();
    RUNTIME.get_or_init(|| {
        Builder::new_multi_thread()
            .enable_all()
            .build()
            .expect("failed to create Tokio runtime for FFI")
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn init_client(
    name_resolution: *const NameResolution,
    name_resolution_len: usize,
    dns_resolution_url: *const c_char,
    callback: ClientInitializationCallback,
) {
    log_ffi_call(format!(
        "init_client({}, {}, {}, {:p})",
        format_ptr(name_resolution),
        name_resolution_len,
        format_c_string_arg(dns_resolution_url),
        callback as *const ()
    ));
    if dns_resolution_url.is_null() {
        callback_error(
            |error| callback(false, error),
            "init_client callback",
            "DNS resolution URL pointer is null",
        );
        return;
    }

    let dns_resolution_url = match read_c_string(dns_resolution_url, "DNS resolution URL") {
        Ok(url) => url,
        Err(err) => {
            callback_error(|error| callback(false, error), "init_client callback", err);
            return;
        }
    };

    if let Err(err) = set_dns_resolution_url(dns_resolution_url) {
        callback_error(|error| callback(false, error), "init_client callback", err);
        return;
    }

    if CLIENT_ECH_POOL.get().is_some() {
        callback(true, std::ptr::null());
        return;
    }

    let name_resolution_map = match build_resolution_map(name_resolution, name_resolution_len) {
        Ok(map) => map,
        Err(err) => {
            callback_error(|error| callback(false, error), "init_client callback", err);
            return;
        }
    };

    let pool_resolution_map = name_resolution_map.clone();
    ffi_runtime().spawn(async move {
        match build_ech_client(name_resolution_map).await {
            Ok(client) => {
                let _ = CLIENT_ECH_POOL.set(Ok(ClientPool::new(client, pool_resolution_map)));
                callback(true, std::ptr::null());
            }
            Err(err) => {
                callback_error(
                    |error| callback(false, error),
                    "init_client callback",
                    err.0,
                );
            }
        }
    });
}

fn build_resolution_map(
    name_resolution: *const NameResolution,
    name_resolution_len: usize,
) -> Result<HashMap<RegexKey, Vec<IpAddr>>, String> {
    if name_resolution_len > 0 && name_resolution.is_null() {
        return Err("name_resolution pointer is null".to_string());
    }

    let mut resolution_table = HashMap::new();
    for i in 0..name_resolution_len {
        let resolution = unsafe { &*name_resolution.add(i) };
        if resolution.regex.is_null() {
            return Err(format!("regex at index {} is null", i));
        }

        let regex_str = unsafe { CStr::from_ptr(resolution.regex) }
            .to_str()
            .map_err(|err| format!("regex at index {} is not valid UTF-8: {}", i, err))?;
        let regex = Regex::new(regex_str)
            .map_err(|err| format!("invalid regex '{}': {}", regex_str, err))?;
        let mut ips = Vec::new();
        for j in 0..resolution.ips_len {
            if resolution.ips.is_null() {
                return Err(format!("ips pointer at index {} is null", i));
            }

            let ip_ptr = unsafe { *resolution.ips.add(j) };
            if ip_ptr.is_null() {
                return Err(format!(
                    "ip at index {} for regex '{}' is null",
                    j, regex_str
                ));
            }

            let ip_str = unsafe { CStr::from_ptr(ip_ptr) }.to_str().map_err(|err| {
                format!(
                    "ip at index {} for regex '{}' is not valid UTF-8: {}",
                    j, regex_str, err
                )
            })?;
            ips.push(
                ip_str
                    .parse::<IpAddr>()
                    .map_err(|_| format!("Failed to parse the ip address: {}", ip_str))?,
            );
        }
        resolution_table.insert(
            RegexKey {
                pattern: String::from(regex_str),
                regex: regex,
            },
            ips,
        );
    }
    Ok(resolution_table)
}

impl fmt::Display for RequestError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            RequestError::EchFailure(ech_failure) => write!(f, "ECH failure: {}", ech_failure),
            RequestError::RequestFailure(string) => write!(f, "Request error: {}", string),
        }
    }
}

fn ffi_error_response(message: impl Into<String>) -> FFIHttpResponseMessage {
    let message = message.into();
    log_ffi_error("FFIHttpResponseMessage", &message);
    FFIHttpResponseMessage {
        premature_death: true,
        premature_death_reason: into_raw_c_string(message),
        status_code: 0,
        headers: std::ptr::null_mut(),
        headers_len: 0,
        body: std::ptr::null_mut(),
        body_len: 0,
    }
}

fn parse_header_line(header_line: &str) -> Result<(HeaderName, HeaderValue), String> {
    let (header_name, header_value) = header_line
        .split_once(':')
        .ok_or_else(|| format!("Invalid header format: {}", header_line))?;
    let header_name = HeaderName::from_str(header_name.trim())
        .map_err(|err| format!("Invalid header name '{}': {}", header_name.trim(), err))?;
    let header_value = HeaderValue::from_str(header_value.trim()).map_err(|err| {
        format!(
            "Invalid value for header '{}': {}",
            header_name.as_str(),
            err
        )
    })?;
    Ok((header_name, header_value))
}

unsafe fn parse_request_headers(
    headers: *const *const c_char,
    headers_len: usize,
) -> Result<HeaderMap, String> {
    let mut header_map = HeaderMap::new();

    for i in 0..headers_len {
        let header_ptr = unsafe { *headers.add(i) };
        if header_ptr.is_null() {
            return Err(format!("Header at index {} is null", i));
        }

        let header_line = unsafe { CStr::from_ptr(header_ptr) }
            .to_str()
            .map_err(|err| format!("Header at index {} is not valid UTF-8: {}", i, err))?;
        let (header_name, header_value) = parse_header_line(header_line)?;
        header_map.append(header_name, header_value);
    }

    Ok(header_map)
}

// it is obligatory to ensure the CLIENT_ECH_POOL is initialized before this call
async fn send_request_kernel(
    request_id: u64,
    url: String,
    method: Method,
    headers: HeaderMap,
    body: Vec<u8>,
) -> Result<reqwest::Response, RequestError> {
    if let Err(err) = log_http_request(request_id, &method, &url, &headers, &body) {
        eprintln!("Failed to log outbound request {}: {}", request_id, err);
    }

    let client_pool = CLIENT_ECH_POOL
        .get()
        .unwrap()
        .as_ref()
        .map_err(|e| RequestError::EchFailure(e.clone().0))?;
    client_pool
        .obtain()
        .await?
        .request(method, url)
        .headers(headers)
        .body(body)
        .send()
        .await
        .map_err(|e| RequestError::RequestFailure(format_reqwest_error(&e)))
}

fn format_reqwest_error(err: &reqwest::Error) -> String {
    let mut s = format!("{}", err);

    let mut current: &(dyn Error + 'static) = err;
    while let Some(src) = current.source() {
        let _ = write!(s, "\n\nCaused by: {}", src);
        current = src;
    }

    s
}

async fn send_request_wrapped(
    request_id: u64,
    url: String,
    method: Method,
    headers: HeaderMap,
    body: Vec<u8>,
) -> FFIHttpResponseMessage {
    let response = send_request_kernel(request_id, url, method, headers, body).await;
    match response {
        Ok(res) => {
            let status_code = res.status().as_u16();
            let response_headers = res
                .headers()
                .iter()
                .map(|(k, v)| {
                    CString::new(format!("{}: {}", k.as_str(), v.to_str().unwrap())).unwrap()
                })
                .collect::<Vec<CString>>();
            let body_bytes = match res.bytes().await {
                Ok(bytes) => bytes.to_vec(),
                Err(err) => {
                    return ffi_error_response(format!("Failed to read response body: {}", err));
                }
            };
            let mut headers = response_headers
                .into_iter()
                .map(CString::into_raw)
                .collect::<Vec<*mut c_char>>();
            let headers_len = headers.len();
            headers.push(std::ptr::null_mut());
            let headers = Box::into_raw(headers.into_boxed_slice()) as *const *const c_char;
            let body_len = body_bytes.len();
            let body = Box::into_raw(body_bytes.into_boxed_slice()) as *mut u8;
            FFIHttpResponseMessage {
                premature_death: false,
                premature_death_reason: std::ptr::null_mut(),
                status_code,
                headers,
                headers_len,
                body,
                body_len,
            }
        }
        Err(err) => ffi_error_response(err.to_string()),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn free_response(response: FFIHttpResponseMessage) {
    log_ffi_call(format!(
        "free_response(FFIHttpResponseMessage {{ premature_death: {}, premature_death_reason: {}, status_code: {}, headers: {}, headers_len: {}, body: {}, body_len: {} }})",
        response.premature_death,
        format_ptr(response.premature_death_reason),
        response.status_code,
        format_ptr(response.headers),
        response.headers_len,
        format_ptr(response.body),
        response.body_len
    ));
    if !response.premature_death_reason.is_null() {
        let _ = unsafe { CString::from_raw(response.premature_death_reason as *mut c_char) };
    }

    if !response.headers.is_null() {
        for i in 0..response.headers_len {
            let _ = unsafe { CString::from_raw(*response.headers.add(i) as *mut c_char) };
        }
        let headers_slice = std::ptr::slice_from_raw_parts_mut(
            response.headers as *mut *mut c_char,
            response.headers_len + 1,
        );
        let _ = unsafe { Box::from_raw(headers_slice) };
    }

    if !response.body.is_null() && response.body_len > 0 {
        let body_slice =
            std::ptr::slice_from_raw_parts_mut(response.body as *mut u8, response.body_len);
        let _ = unsafe { Box::from_raw(body_slice) };
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn free_c_string(ptr: *const c_char) {
    log_ffi_call(format!("free_c_string({})", format_ptr(ptr)));
    if !ptr.is_null() {
        let _ = unsafe { CString::from_raw(ptr as *mut c_char) };
    }
}

#[unsafe(no_mangle)]
unsafe extern "C" fn send_request(
    request_message: FFIHttpRequestMessage,
    callback: HttpCompletionCallback,
    user_data: *mut c_void,
) {
    log_ffi_call(format!(
        "send_request(FFIHttpRequestMessage {{ request_id: {}, url: {}, method: {}, headers: {}, headers_len: {}, body: {}, body_len: {} }}, {:p}, {})",
        request_message.request_id,
        format_c_string_arg(request_message.url),
        format_c_string_arg(request_message.method),
        format_ptr(request_message.headers),
        request_message.headers_len,
        format_ptr(request_message.body),
        request_message.body_len,
        callback as *const (),
        format_mut_ptr(user_data)
    ));
    let url = match read_c_string(request_message.url, "request_message.url") {
        Ok(url) => String::from(url),
        Err(err) => {
            let response = ffi_error_response(err);
            callback(request_message.request_id, response, user_data);
            return;
        }
    };
    let header_map = match unsafe {
        parse_request_headers(request_message.headers, request_message.headers_len)
    } {
        Ok(header_map) => header_map,
        Err(err) => {
            callback(
                request_message.request_id,
                ffi_error_response(format!("Invalid HTTP headers: {}", err)),
                user_data,
            );
            return;
        }
    };
    let method =
        match read_c_string(request_message.method, "request_message.method").and_then(|method| {
            Method::from_str(method).map_err(|_| String::from("Invalid HTTP method"))
        }) {
            Ok(method) => method,
            Err(err) => {
                let response = ffi_error_response(err);
                callback(request_message.request_id, response, user_data);
                return;
            }
        };
    let request_id = request_message.request_id;
    let body = unsafe {
        std::slice::from_raw_parts(request_message.body, request_message.body_len).to_vec()
    };
    let user_data_safe = user_data as usize;
    ffi_runtime().spawn(async move {
        let resp = send_request_wrapped(request_id, url, method, header_map, body).await;
        callback(request_id, resp, user_data_safe as *mut c_void)
    });
}
