use crate::util::format_error;
use anyhow::anyhow;
use reqwest::dns::{Addrs, Resolve};
use reqwest::Url;
use std::error::Error;
use std::ffi::{c_void, CStr, CString};
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::{Arc, LazyLock, Mutex};
use std::{
    collections::HashMap,
    net::{IpAddr, SocketAddr},
    os::raw::c_char,
    sync::{OnceLock, RwLock},
};
use tokio::sync::oneshot;

static REQUEST_TOKEN_ID: AtomicI64 = AtomicI64::new(0);

static CONFIGURED_DNS_RESOLUTION_URL: OnceLock<Url> = OnceLock::new();

pub static DNS_RESOLUTION_GLOBAL_CALLBACK: RwLock<Option<ManagedDnsResolutionCallback>> = RwLock::new(None);

pub static DNS_RESOLUTION_CHANNEL_CACHE: LazyLock<Mutex<HashMap<i64, Box<oneshot::Sender<Vec<SocketAddr>>>>>> = LazyLock::new(|| Mutex::new(HashMap::new()));

pub type ManagedDnsResolutionCallback = unsafe extern "C" fn(i64, *const c_char);

pub extern "C" fn register_dns_resolution_callback(ctx: usize, callback: ManagedDnsResolutionCallback) -> bool {
    let result = DNS_RESOLUTION_GLOBAL_CALLBACK.write();
    match result {
        Ok(mut old) => {
            *old = Some(callback);
            true
        }
        Err(_) => false
    }
}

pub extern "C" fn complete_resolution(request_id: i64, ip_str: *const *const c_char, ip_len: usize) -> Result<(), String> {
     match DNS_RESOLUTION_CHANNEL_CACHE.lock().unwrap().remove(&request_id) {
         Some(sender) => {
             let parsed_addrs = parse_managed_ip_addr(ip_str, ip_len)?
                 .into_iter()
                 .map(|addr| SocketAddr::new(addr, 0))
                 .collect::<Vec<_>>();
             Ok(sender.send(parsed_addrs).map_err(|e| format!("Failed to complete the resolved IP addresses: {:?}", e))?)
         }
         None => Err(format!("No pending DNS resolution found for request ID: {}", request_id)),
     }
}

fn parse_managed_ip_addr(ip_str: *const *const c_char, ip_len: usize) -> Result<Vec<IpAddr>, String> {
    let mut vec: Vec<IpAddr> = Vec::new();
    for i in 0..ip_len {
        let c_str = unsafe { CStr::from_ptr(*ip_str.add(i)) };
        vec.push(c_str.to_str().unwrap().parse::<IpAddr>().map_err(|e| format!("Failed to parse IP address: {}", e))?);
    }
    Ok(vec)
}

pub fn current_dns_resolution_url() -> Option<Url> {
    CONFIGURED_DNS_RESOLUTION_URL.get().cloned()
}

pub fn set_dns_resolution_url(full_url: &str) -> Result<(), String> {
    let url = parse_dns_resolution_url(full_url)?;

    if let Some(existing) = CONFIGURED_DNS_RESOLUTION_URL.get() {
        if existing == &url {
            return Ok(());
        }

        return Err(String::from(
            "DNS resolution URL has already been set and cannot be changed",
        ));
    }

    CONFIGURED_DNS_RESOLUTION_URL.set(url).map_err(|_| {
        String::from("DNS resolution URL has already been set and cannot be changed")
    })?;

    Ok(())
}

fn parse_dns_resolution_url(full_url: &str) -> Result<Url, String> {
    Url::parse(full_url).map_err(|err| format!("Invalid DNS resolution URL: {}", err))
}

pub type ManagedClientHandle = usize;

pub struct DelegatedResolver {
    pub managed_ctx: ManagedClientHandle,
    pub callback: ManagedDnsResolutionCallback,
    pub pending: Arc<Mutex<HashMap<i64, oneshot::Sender<Vec<SocketAddr>>>>>,
    pub request_token: AtomicI64,
    pub dns_url: String
}

impl Resolve for DelegatedResolver {
    fn resolve(&self, name: reqwest::dns::Name) -> reqwest::dns::Resolving {
        // noinspection ALL
        async fn async_resolve(name: reqwest::dns::Name) -> Result<Addrs, Box<dyn Error + Sync + Send>> {
            let callback = {
                let guard = DNS_RESOLUTION_GLOBAL_CALLBACK.read().unwrap();
                (*guard).ok_or_else(|| "No DNS resolution callback has been registered")?
            };

            let native_name = CString::new(name.as_str())?;
            let token = REQUEST_TOKEN_ID.fetch_add(1, Ordering::Relaxed);
            let (sender, receiver) = oneshot::channel();

            DNS_RESOLUTION_CHANNEL_CACHE
                .lock()
                .unwrap()
                .insert(token, Box::new(sender));

            unsafe { callback(token, native_name.as_ptr()) };

            let addrs = receiver
                .await
                .map_err(|e| anyhow!("Failed to receive resolved IP addresses: {}", format_error(&e)))?;

            Ok(Box::new(addrs.into_iter()) as Addrs)
        }

        Box::pin(async move {
            async_resolve(name).await
        })
    }
}