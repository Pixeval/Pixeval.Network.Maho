use crate::async_runtime::ffi_runtime;
use crate::logging::{ManagedLogger, ManagedLoggingCallback};
use crate::pinvoke::{FFIHttpRequestMessage, FFIHttpResponseMessage};
use crate::resolution::{DelegatedResolver, ManagedDnsResolutionCallback};
use crate::util::format_error;
use crate::{client_builder, marshal};
use reqwest::header::{HeaderMap, HeaderName, HeaderValue};
use reqwest::Method;
use std::collections::HashMap;
use std::ffi::{c_char, c_void, CStr, CString};
use std::fmt;
use std::str::FromStr;
use std::sync::atomic::AtomicI64;
use std::sync::{Arc, Mutex};

pub struct NativeClient {
    pub client: reqwest::Client,
    pub resolver: Arc<DelegatedResolver>,
    pub logger: Arc<ManagedLogger>
}

pub enum RequestError {
    EchFailure(String),
    RequestFailure(String),
}

impl fmt::Display for RequestError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            RequestError::EchFailure(ech_failure) => write!(f, "ECH failure: {}", ech_failure),
            RequestError::RequestFailure(string) => write!(f, "Request error: {}", string),
        }
    }
}

pub type ClientCreationCallback = extern "C" fn(success: bool, client_handle: *const NativeClient, error_reason: *const c_char);
pub type HttpCompletionCallback = extern "C" fn(id: u64, response: FFIHttpResponseMessage, user_data: *const c_void);

#[unsafe(no_mangle)]
pub unsafe extern "C" fn begin_create_client(
    dns_server: *const c_char,
    dns_callback: ManagedDnsResolutionCallback,
    logger_callback: ManagedLoggingCallback,
    client_creation_callback: ClientCreationCallback,
) {
    if dns_server.is_null() {
        let err_str = CString::from_str("The dns server is null.").unwrap();
        client_creation_callback(false, std::ptr::null(), err_str.as_ptr());
        return;
    }
    let dns_server_n = unsafe{ CStr::from_ptr(dns_server) }.to_str().map_err(|e| format!("Invalid DNS server string: {}", e))
        .unwrap_or_else(|e| {
            let err_str = CString::new(e).unwrap();
            client_creation_callback(false, std::ptr::null(), err_str.as_ptr());
            "" // Return an empty string to satisfy the type, though it won't be used.
        });
    let resolver = DelegatedResolver {
        callback: Arc::new(Mutex::new(dns_callback)),
        pending: Arc::new(Mutex::new(HashMap::new())),
        request_token: Arc::new(AtomicI64::new(0)),
        dns_url: String::from(dns_server_n)
    };
    let resolver_arc = Arc::new(resolver);

    let logger = ManagedLogger {
        callback: Arc::new(Mutex::new(logger_callback)),
    };
    let logger_arc = Arc::new(logger);

    ffi_runtime().spawn(async move {
        let client_res = client_builder::build_ech_client(resolver_arc.clone()).await;
        match client_res {
            Ok(client) => {
                let heap_allocated_native_client = Box::new(NativeClient { client, resolver: resolver_arc, logger: logger_arc });
                client_creation_callback(true, Box::into_raw(heap_allocated_native_client), std::ptr::null());
            }
            Err(e) => {
                let err_str = CString::new(e.0.as_str()).unwrap();
                client_creation_callback(false, std::ptr::null(), err_str.as_ptr());
            }
        };
    });
}

#[unsafe(no_mangle)]
pub extern "C" fn free_client(client_handle: *const NativeClient) {
    if client_handle.is_null() {
        return;
    }

    drop(unsafe { Box::from_raw(client_handle as *mut NativeClient) });
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn free_response(client_handle: *const NativeClient, response: FFIHttpResponseMessage) {
    if let Some(client) = unsafe { client_handle.as_ref() } {
        client.logger.log_ffi_call(format!(
            "free_response(FFIHttpResponseMessage {{ premature_death: {}, premature_death_reason: {}, status_code: {}, headers: {}, headers_len: {}, body: {}, body_len: {} }})",
            response.premature_death,
            marshal::format_ptr(response.premature_death_reason),
            response.status_code,
            marshal::format_ptr(response.headers),
            response.headers_len,
            marshal::format_ptr(response.body),
            response.body_len
        ));
    }

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
unsafe extern "C" fn send_request(
    native_client: *mut NativeClient,
    request_message: FFIHttpRequestMessage,
    callback: HttpCompletionCallback,
    user_data: *mut c_void,
) {
    if let Some(client) = unsafe { native_client.as_ref() } {
        client.logger.log_ffi_call(format!(
            "send_request(FFIHttpRequestMessage {{ request_id: {}, url: {}, method: {}, headers: {}, headers_len: {}, body: {}, body_len: {} }}, {:p}, {})",
            request_message.request_id,
            marshal::format_c_string_arg(request_message.url),
            marshal::format_c_string_arg(request_message.method),
            marshal::format_ptr(request_message.headers),
            request_message.headers_len,
            marshal::format_ptr(request_message.body),
            request_message.body_len,
            callback as *const (),
            marshal::format_mut_ptr(user_data)
        ));
    }

    let url = match marshal::read_c_string(request_message.url, "request_message.url") {
        Ok(url) => String::from(url),
        Err(err) => {
            let response = ffi_error_response(err);
            callback(request_message.request_id, response, user_data);
            return;
        }
    };
    let header_map = match unsafe { parse_request_headers(request_message.headers, request_message.headers_len) } {
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
        match marshal::read_c_string(request_message.method, "request_message.method").and_then(|method| {
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
    let body = unsafe { std::slice::from_raw_parts(request_message.body, request_message.body_len).to_vec() };
    let user_data_safe = user_data as usize;
    match unsafe { native_client.as_ref() } {
        None => {
            callback(request_message.request_id, ffi_error_response("Invalid client handle pointer"), user_data);
        }
        Some(inner) => {
            ffi_runtime().spawn(async move {
                let resp = inner.send_request_wrapped(request_id, url, method, header_map, body).await;
                callback(request_id, resp, user_data_safe as *mut c_void)
            });
        }
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

impl NativeClient {
    async fn send_request_kernel(
        &self,
        request_id: u64,
        url: String,
        method: Method,
        headers: HeaderMap,
        body: Vec<u8>,
    ) -> Result<reqwest::Response, RequestError> {
        if let Err(err) = self.logger.log_http_request(request_id, &method, &url, &headers, &body) {
            eprintln!("Failed to log outbound request {}: {}", request_id, err);
        }

        self.client
            .request(method, url)
            .headers(headers)
            .body(body)
            .send()
            .await
            .map_err(|e| RequestError::RequestFailure(format_error(&e)))
    }

    pub async fn send_request_wrapped(
        &self,
        request_id: u64,
        url: String,
        method: Method,
        headers: HeaderMap,
        body: Vec<u8>,
    ) -> FFIHttpResponseMessage {
        let response = self.send_request_kernel(request_id, url, method, headers, body).await;
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
}

fn ffi_error_response(message: impl Into<String>) -> FFIHttpResponseMessage {
    let message = message.into();
    FFIHttpResponseMessage {
        premature_death: true,
        premature_death_reason: marshal::into_raw_c_string(message),
        status_code: 0,
        headers: std::ptr::null_mut(),
        headers_len: 0,
        body: std::ptr::null_mut(),
        body_len: 0,
    }
}
