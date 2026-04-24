use std::ffi::{c_char, c_void};

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