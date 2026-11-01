// Web Worker running engine searches so the UI stays responsive. Uses the SIMD build when the
// browser supports it. Messages: { id, request } (JSON API, engine/src/api.rs) and, before the
// first search with `nnue: true`, { id, network } with the .ggnn bytes (see engine.js).
import wasmUrl from './goldrush_engine.wasm?url';
import simdWasmUrl from './goldrush_engine_simd.wasm?url';
import { loadEngine, simdSupported } from './engineWasm.js';

const ready = loadEngine(simdSupported() ? simdWasmUrl : wasmUrl);

self.onmessage = async (e) => {
  const { id, request, network } = e.data;
  try {
    const engine = await ready;
    if (network) {
      engine.loadNetwork(network);
      self.postMessage({ id, result: { ok: true } });
    } else {
      self.postMessage({ id, result: engine.call(request) });
    }
  } catch (err) {
    self.postMessage({ id, error: err?.message ?? String(err) });
  }
};
