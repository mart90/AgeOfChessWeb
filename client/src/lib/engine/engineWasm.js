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

  return { call };
}

/** Browser: fetch and instantiate from a URL. */
export async function loadEngine(url) {
  const bytes = await (await fetch(url)).arrayBuffer();
  return instantiateEngine(bytes);
}
