use crate::marshal;
use crate::native_client::NativeClient;
use crate::pinvoke::InteropOperationResult;
use anyhow::anyhow;
use reqwest::dns::{Addrs, Resolve};
use std::ffi::{c_char, CStr, CString};
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::{Arc, Mutex};
use std::{
    collections::HashMap,
    net::{IpAddr, SocketAddr}
};
use tokio::sync::oneshot::{self, Sender};

pub type ManagedDnsResolutionCallback = unsafe extern "C" fn(i64, *const c_char);

#[unsafe(no_mangle)]
pub unsafe extern "C" fn complete_resolution(
    native_client: *mut NativeClient,
    request_id: i64,
    ip_str: *const *const c_char,
    ip_len: usize,
) -> InteropOperationResult {
    let client_res = unsafe { native_client.as_ref() }.ok_or_else(|| "native_client is null".to_string());
    if client_res.is_err() {
        return InteropOperationResult::from(client_res);
    }
    let client = client_res.unwrap();
    match client.resolver.pending.lock().unwrap().remove(&request_id)
    {
        Some(sender) => {
            let parsed_addrs_res = parse_managed_ip_addr(ip_str, ip_len);
            if parsed_addrs_res.is_err() {
                return InteropOperationResult::from(parsed_addrs_res);
            }
            let parsed_addrs = parsed_addrs_res.unwrap()
                .into_iter()
                .map(|addr| SocketAddr::new(addr, 0))
                .collect::<Vec<_>>();
            sender.send(Ok(parsed_addrs))
                .map_err(|e| format!("Failed to complete the resolved IP addresses: {:?}", e))
                .map_or_else(
                    |e| InteropOperationResult { success: false, error_reason: marshal::into_raw_c_string(e) },
                    |_| InteropOperationResult { success: true, error_reason: std::ptr::null() })
        }
        None => InteropOperationResult {
            success: false,
            error_reason: marshal::into_raw_c_string(format!(
                "No pending DNS resolution found for request ID: {}",
                request_id
            ))
        }
    }
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn complete_resolution_failure(
    native_client: *mut NativeClient,
    request_id: i64,
    err_reason: *const c_char) -> InteropOperationResult
{
    let client_res = unsafe { native_client.as_ref() }.ok_or_else(|| "native_client is null".to_string());
    if client_res.is_err() {
        return InteropOperationResult::from(client_res);
    }
    let client = client_res.unwrap();
    match client.resolver.pending.lock().unwrap().remove(&request_id)
    {
        Some(sender) => {
            let _ = sender.send(Err(String::from(marshal::read_c_string(err_reason, "err_reason").unwrap())));
            InteropOperationResult { success: true, error_reason: std::ptr::null() }
        }
        None => InteropOperationResult {
            success: false,
            error_reason: marshal::into_raw_c_string(format!(
                "No pending DNS resolution found for request ID: {}",
                request_id
            ))
        }
    }
}

fn parse_managed_ip_addr(
    ip_str: *const *const c_char,
    ip_len: usize,
) -> Result<Vec<IpAddr>, String> {
    let mut vec: Vec<IpAddr> = Vec::new();
    for i in 0..ip_len {
        let c_str = unsafe { CStr::from_ptr(*ip_str.add(i)) };
        vec.push(
            c_str
                .to_str()
                .unwrap()
                .parse::<IpAddr>()
                .map_err(|e| format!("Failed to parse IP address: {}", e))?,
        );
    }
    Ok(vec)
}

pub struct DelegatedResolver {
    pub callback: Arc<Mutex<ManagedDnsResolutionCallback>>,
    pub pending: Arc<Mutex<HashMap<i64, Box<Sender<Result<Vec<SocketAddr>, String>>>>>>,
    pub request_token: Arc<AtomicI64>,
    pub dns_url: String,
}

impl Resolve for DelegatedResolver {
    fn resolve(&self, name: reqwest::dns::Name) -> reqwest::dns::Resolving {
        let callback_owned = self.callback.clone();
        let request_token_owned = self.request_token.clone();
        let pending_owned = self.pending.clone();
        // noinspection ALL
        let async_resolve = async move ||  {
            let native_name = CString::new(name.as_str())?;
            let token = (*request_token_owned).fetch_add(1, Ordering::Relaxed);
            let (sender, receiver) = oneshot::channel();

            pending_owned.lock().unwrap().insert(token, Box::new(sender));

            unsafe { (*callback_owned.lock().unwrap())(token, native_name.as_ptr()) };

            let addrs = receiver.await.unwrap().map_err(|e| {
                anyhow!(
                    "Failed to receive resolved IP addresses: {}",
                    e
                )
            })?;

            Ok(Box::new(addrs.into_iter()) as Addrs)
        };

        Box::pin(async move { async_resolve().await })
    }
}
