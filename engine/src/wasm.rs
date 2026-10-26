//! Raw WebAssembly exports (no wasm-bindgen). JS writes a UTF-8 JSON request into memory
//! obtained from `alloc`, calls `handle(ptr, len)`, then reads `out_len()` bytes at the
//! returned pointer. See `client/src/lib/engine/engineWasm.js`.

use std::cell::RefCell;

thread_local! {
    static OUT: RefCell<Vec<u8>> = const { RefCell::new(Vec::new()) };
}

#[no_mangle]
pub extern "C" fn alloc(len: usize) -> *mut u8 {
    let mut v = Vec::<u8>::with_capacity(len.max(1));
    let p = v.as_mut_ptr();
    std::mem::forget(v);
    p
}

/// # Safety
/// `ptr`/`len` must come from `alloc`.
#[no_mangle]
pub unsafe extern "C" fn dealloc(ptr: *mut u8, len: usize) {
    drop(Vec::from_raw_parts(ptr, 0, len.max(1)));
}

/// # Safety
/// `ptr..ptr+len` must be a valid UTF-8 buffer obtained from `alloc`.
#[no_mangle]
pub unsafe extern "C" fn handle(ptr: *const u8, len: usize) -> *const u8 {
    let input = std::str::from_utf8(std::slice::from_raw_parts(ptr, len)).unwrap_or("");
    let out = crate::api::handle(input);
    OUT.with(|o| {
        let mut o = o.borrow_mut();
        *o = out.into_bytes();
        o.as_ptr()
    })
}

#[no_mangle]
pub extern "C" fn out_len() -> usize {
    OUT.with(|o| o.borrow().len())
}
