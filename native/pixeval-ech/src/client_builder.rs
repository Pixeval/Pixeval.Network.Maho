use base64::prelude::BASE64_STANDARD;
use base64::Engine;
use rustls::client::{EchConfig, EchMode};
use rustls::crypto::aws_lc_rs::default_provider;
use rustls::crypto::aws_lc_rs::hpke::ALL_SUPPORTED_SUITES;
use rustls::{ClientConfig, RootCertStore};
use serde_json::Value;
use std::sync::Arc;

use crate::resolution::{current_dns_resolution_url, DelegatedResolver};

#[derive(Debug, Clone)]
pub struct EchFailure(pub(crate) String);

fn extract_ech_parameter(ech_data: &str) -> Result<String, EchFailure> {
    let ech_start = "ech=\"";
    let start_index = ech_data.find(ech_start).ok_or(EchFailure(format!(
        "Failed to find ech parameter {}",
        ech_data
    )))? + ech_start.len();
    let remainder = &ech_data[start_index..];
    let end_idx = remainder.find('"').ok_or(EchFailure(format!(
        "Ech parameter missing a closed quote: {}",
        ech_data
    )))?;
    Ok(String::from(&remainder[..end_idx]))
}

async fn fetch_cloudflare_ech_bytes() -> Result<Vec<u8>, EchFailure> {
    let dns_resolution_url = current_dns_resolution_url()
        .ok_or_else(|| EchFailure(String::from("DNS resolution URL has not been configured")))?;
    let result = reqwest::ClientBuilder::new()
        .no_proxy()
        .build()
        .unwrap()
        .get(dns_resolution_url)
        .send()
        .await;
    let https_record_code = 65;
    match result {
        Ok(response) => {
            let text = response
                .text()
                .await
                .map_err(|e| EchFailure(format!("Failed to read response text: {}", e)))?;
            let parsed: Value = serde_json::from_str(text.as_str())
                .map_err(|e| EchFailure(format!("Failed to parse JSON: {}", e)))?;
            let answer_arr = parsed["Answer"]
                .as_array()
                .ok_or(EchFailure(format!("JSON field not found: {}", text)))?;
            let ech = answer_arr
                .iter()
                .find(|item| item["type"].as_i64().unwrap() == https_record_code)
                .ok_or(EchFailure(format!(
                    "No HTTPS record found in DNS response: {}",
                    text
                )))?["data"]
                .as_str()
                .ok_or(EchFailure(format!(
                    "HTTPS record missing data field: {}",
                    text
                )))?;
            let extracted_param = extract_ech_parameter(ech)?;
            BASE64_STANDARD.decode(extracted_param).map_err(|e| {
                EchFailure(format!(
                    "Failed to decode base64 formed ECH config list: {}",
                    e
                ))
            })
        }
        Err(e) => Err(EchFailure(format!(
            "Failed to fetch ECH config list: {}",
            e
        ))),
    }
}

pub async fn build_ech_client(delegated_resolver: Arc<DelegatedResolver>) -> Result<reqwest::Client, EchFailure> {
    let ech_bytes = fetch_cloudflare_ech_bytes().await?;
    let ech_config = EchConfig::new(ech_bytes.into(), ALL_SUPPORTED_SUITES)
        .map_err(|e| EchFailure(format!("Failed to parse ECH config list: {}", e)))?;
    let mut root_store = RootCertStore::empty();
    root_store.extend(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());

    let tls_config = ClientConfig::builder_with_provider(Arc::new(default_provider()))
        .with_ech(EchMode::Enable(ech_config))
        .and_then(|config| {
            Ok(config
                .with_root_certificates(root_store)
                .with_no_client_auth())
        })
        .map_err(|e| EchFailure(format!("Failed to create tls config {}", e)))?;

    reqwest::Client::builder()
        .tls_backend_preconfigured(tls_config)
        .dns_resolver(delegated_resolver)
        .no_proxy()
        .build()
        .map_err(|e| EchFailure(format!("Failed to build reqwest client: {}", e)))
}
