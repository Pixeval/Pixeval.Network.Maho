use crate::marshal;
use std::ffi::c_char;

#[repr(C)]
pub struct InteropOperationResult {
    pub success: bool,
    pub error_reason: *const c_char,
}

impl<T> From<Result<T, String>> for InteropOperationResult {
    fn from(result: Result<T, String>) -> Self {
        match result {
            Ok(_) => InteropOperationResult {
                success: true,
                error_reason: std::ptr::null(),
            },
            Err(err) => InteropOperationResult {
                success: false,
                error_reason: marshal::into_raw_c_string(err),
            },
        }
    }
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
