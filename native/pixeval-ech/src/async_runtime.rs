use std::sync::OnceLock;
use tokio::runtime::{Builder, Runtime};

pub fn ffi_runtime() -> &'static Runtime {
    static RUNTIME: OnceLock<Runtime> = OnceLock::new();
    RUNTIME.get_or_init(|| {
        Builder::new_multi_thread()
            .enable_all()
            .build()
            .expect("failed to create Tokio runtime for FFI")
    })
}