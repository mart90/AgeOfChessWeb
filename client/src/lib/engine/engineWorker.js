// Web Worker running engine searches so the UI stays responsive.
import wasmUrl from './goldrush_engine.wasm?url';
import { loadEngine } from './engineWasm.js';

const ready = loadEngine(wasmUrl);

self.onmessage = async (e) => {
  const { id, request } = e.data;
  try {
    const engine = await ready;
    self.postMessage({ id, result: engine.call(request) });
  } catch (err) {
    self.postMessage({ id, error: err?.message ?? String(err) });
  }
};
