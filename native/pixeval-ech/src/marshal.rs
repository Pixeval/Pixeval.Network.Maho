use std::ffi::{c_char, CStr, CString};
use crate::logging;

pub fn into_raw_c_string(message: impl Into<String>) -> *const c_char {
    CString::new(message.into()).unwrap().into_raw()
}

pub(crate) fn format_ptr<T>(ptr: *const T) -> String {
    format!("{:p}", ptr)
}

pub(crate) fn format_mut_ptr<T>(ptr: *mut T) -> String {
    format!("{:p}", ptr)
}

pub(crate) fn format_c_string_arg(ptr: *const c_char) -> String {
    if ptr.is_null() {
        return String::from("null");
    }

    match unsafe { CStr::from_ptr(ptr) }.to_str() {
        Ok(value) => format!("{:?}", value),
        Err(_) => format!("{}:<invalid-utf8>", format_ptr(ptr)),
    }
}

pub(crate) fn read_c_string(ptr: *const c_char, arg_name: &str) -> Result<&str, String> {
    if ptr.is_null() {
        return Err(format!("{} pointer is null", arg_name));
    }

    unsafe { CStr::from_ptr(ptr) }
        .to_str()
        .map_err(|err| format!("{} is not valid UTF-8: {}", arg_name, err))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn free_c_string(ptr: *const c_char) {
    logging::log_ffi_call(format!("free_c_string({})", format_ptr(ptr)));
    if !ptr.is_null() {
        let _ = unsafe { CString::from_raw(ptr as *mut c_char) };
    }
}