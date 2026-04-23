use crate::client_builder::{EchFailure, build_ech_client};
use crate::pinvoke::RequestError;
use crate::regex::RegexKey;
use std::collections::HashMap;
use std::net::IpAddr;
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

pub static CLIENT_ECH_POOL: OnceLock<Result<ClientPool, EchFailure>> = OnceLock::new();

struct State {
    client: reqwest::Client,
    refreshed_at: Instant,
}

pub struct ClientPool {
    state: Mutex<State>,
    name_resolution: HashMap<RegexKey, Vec<IpAddr>>,
}

impl ClientPool {
    pub fn new(client: reqwest::Client, name_resolution: HashMap<RegexKey, Vec<IpAddr>>) -> Self {
        Self {
            state: Mutex::new(State {
                client,
                refreshed_at: Instant::now(),
            }),
            name_resolution,
        }
    }

    pub async fn obtain(&self) -> Result<reqwest::Client, RequestError> {
        {
            let state0 = self.state.lock().unwrap();
            if state0.refreshed_at.elapsed() < Duration::from_secs(1800) {
                return Ok(state0.client.clone());
            }
        }

        let result = build_ech_client(self.name_resolution.clone()).await;

        let mut state = self.state.lock().unwrap();
        if state.refreshed_at.elapsed() >= Duration::from_secs(1800) {
            state.client = result.map_err(|e| RequestError::EchFailure(e.0))?;
            state.refreshed_at = Instant::now();
        }
        Ok(state.client.clone())
    }
}