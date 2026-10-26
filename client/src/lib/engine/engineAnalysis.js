// Continuous analysis with the Rust engine. Drop-in replacement for the old
// lib/analysisWorker.js: same postMessage({ type: 'start' | 'update' | 'stop', payload })
// interface, emits onmessage({ data: { type: 'depth-result', depth, topMoves, timeMs } })
// with topMoves = [{ move, score, mate, pv }] (score: gold, White's point of view).
import { EngineClient } from './engine.js';

const MULTI_PV = 5;
const MAX_DEPTH = 90;   // engine limit; in practice analysis runs until stopped

export class EngineAnalysis {
  onmessage = null;
  #client = new EngineClient();
  #position = null;
  #running = false;
  #version = 0;

  postMessage({ type, payload }) {
    if (type === 'start' || type === 'update') {
      this.#position = payload;
      if (type === 'start') this.#running = true;
      this.#restart();
    } else if (type === 'stop') {
      this.#running = false;
      this.#version++;
      this.#client.cancel();
    }
  }

  terminate() {
    this.postMessage({ type: 'stop' });
  }

  #emit(data) {
    this.onmessage?.({ data });
  }

  #restart() {
    this.#version++;
    this.#client.cancel();
    if (this.#running && this.#position) this.#loop(this.#version);
  }

  async #loop(version) {
    const position = this.#position;
    for (let depth = 1; depth <= MAX_DEPTH; depth++) {
      const t0 = performance.now();
      let res;
      try {
        res = await this.#client.call({ cmd: 'search', position, depth, multiPv: MULTI_PV });
      } catch (e) {
        if (version === this.#version && e.message !== 'cancelled') this.#emit({ type: 'error', message: e.message });
        return;
      }
      if (version !== this.#version) return;
      const topMoves = res.lines.map(l => ({ move: l.move, score: l.score, mate: l.mate, pv: l.pv }));
      this.#emit({ type: 'depth-result', depth, topMoves, timeMs: performance.now() - t0 });
      // Keep deepening until stopped (forced results just finish their iterations quickly)
      if (!topMoves.length) return;
    }
  }
}
