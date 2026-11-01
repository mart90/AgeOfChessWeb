// Loader for the Rust engine compiled to WebAssembly (engine/ → goldrush_engine.wasm).
// Rebuild with `npm run build:engine`. The module speaks JSON: see engine/src/api.rs.

/** Instantiate the engine from raw wasm bytes. Returns { call(request) → response }. */
export async function instantiateEngine(bytes) {
  const { instance } = await WebAssembly.instantiate(bytes, {
    env: { now_ms: () => performance.now() },
  });
  const ex = instance.exports;
  const enc = new TextEncoder();
  const dec = new TextDecoder();

  function call(request) {
    const input = enc.encode(JSON.stringify(request));
    const ptr = ex.alloc(input.length);
    new Uint8Array(ex.memory.buffer, ptr, input.length).set(input);
    const outPtr = ex.handle(ptr, input.length);
    const out = dec.decode(new Uint8Array(ex.memory.buffer, outPtr, ex.out_len()));
    ex.dealloc(ptr, input.length);
    const res = JSON.parse(out);
    if (res.error) throw new Error(res.error);
    return res;
  }

  /** Load an evaluation network (.ggnn bytes) for searches with `nnue: true`. */
  function loadNetwork(bytes) {
    const data = new Uint8Array(bytes);
    const ptr = ex.alloc(data.length);
    new Uint8Array(ex.memory.buffer, ptr, data.length).set(data);
    const ok = ex.load_nnue(ptr, data.length);
    ex.dealloc(ptr, data.length);
    if (!ok) throw new Error('invalid network file');
  }

  return { call, loadNetwork };
}

/** Whether this WebAssembly runtime supports SIMD (simd128): a tiny module using v128. */
export function simdSupported() {
  try {
    return WebAssembly.validate(new Uint8Array([
      0, 97, 115, 109, 1, 0, 0, 0, 1, 5, 1, 96, 0, 1, 123, 3, 2, 1, 0, 10, 10, 1, 8, 0, 65, 0, 253, 15, 253, 98, 11,
    ]));
  } catch {
    return false;
  }
}

/** Browser: fetch and instantiate from a URL. */
export async function loadEngine(url) {
  const bytes = await (await fetch(url)).arrayBuffer();
  return instantiateEngine(bytes);
}
