use std::{
    collections::HashMap,
    net::{IpAddr, SocketAddr},
    os::raw::c_char,
    sync::{OnceLock, RwLock},
};

use hyper_util::client::legacy::connect::dns::{GaiResolver, Name};
use reqwest::Url;
use reqwest::dns::{Addrs, Resolve};
use tower_service::Service;

use crate::regex::RegexKey;
pub static CONFIGURED_DNS_RESOLUTION_URL: OnceLock<Url> = OnceLock::new();

pub static DNS_RESOLUTION_GLOBAL_CALLBACK: RwLock<Option<RegisterDnsCallback>> = RwLock::new(None);

pub type RegisterDnsCallback = unsafe extern "C" fn(i64, *const c_char);

pub extern "C" fn register_dns_resolution_callback(callback: RegisterDnsCallback) -> bool {
    let result = DNS_RESOLUTION_GLOBAL_CALLBACK.write();
    match result {
        Ok(mut old) => {
            *old = Some(callback);
            true
        }
        Err(_) => false,
    }
}

pub struct SelectiveResolver {
    resolution_table: HashMap<RegexKey, Vec<IpAddr>>,
    default_resolver: GaiResolver,
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

impl SelectiveResolver {
    pub fn new(resolution_table: HashMap<RegexKey, Vec<IpAddr>>) -> Self {
        Self {
            resolution_table,
            default_resolver: GaiResolver::new(),
        }
    }

    fn find_match(&self, name: &str) -> Option<Vec<SocketAddr>> {
        self.resolution_table.iter().find_map(|(key, ips)| {
            if key.regex.is_match(name) {
                Some(
                    ips.iter()
                        .map(|ip| SocketAddr::new(ip.clone(), 0))
                        .collect(),
                )
            } else {
                None
            }
        })
    }
}

impl Resolve for SelectiveResolver {
    fn resolve(&self, name: reqwest::dns::Name) -> reqwest::dns::Resolving {
        let name_str = name.as_str();
        match self.find_match(name_str) {
            Some(ips) => {
                let iter = ips.to_vec().into_iter();
                let addrs: reqwest::dns::Addrs = Box::new(iter);
                Box::pin(std::future::ready(Ok(addrs)))
            }
            None => {
                let host = name_str.to_owned();
                let mut fallback = self.default_resolver.clone();
                Box::pin(async move {
                    let name = host.parse::<Name>()?;
                    let addrs = fallback.call(name).await?;
                    Ok(Box::new(addrs) as Addrs)
                })
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::parse_dns_resolution_url;

    #[test]
    fn accepts_parseable_url() {
        assert!(parse_dns_resolution_url("https://example.com/resolve").is_ok());
    }

    #[test]
    fn rejects_unparseable_url() {
        assert!(parse_dns_resolution_url("not a url").is_err());
    }
}
