//! Millisecond clock that works natively and in WebAssembly.

#[cfg(not(target_arch = "wasm32"))]
pub fn now_ms() -> f64 {
    use std::sync::OnceLock;
    use std::time::Instant;
    static START: OnceLock<Instant> = OnceLock::new();
    START.get_or_init(Instant::now).elapsed().as_secs_f64() * 1000.0
}

#[cfg(target_arch = "wasm32")]
#[link(wasm_import_module = "env")]
extern "C" {
    /// Imported from JS as `env.now_ms` (performance.now()).
    #[link_name = "now_ms"]
    fn js_now_ms() -> f64;
}

#[cfg(target_arch = "wasm32")]
pub fn now_ms() -> f64 {
    unsafe { js_now_ms() }
}
