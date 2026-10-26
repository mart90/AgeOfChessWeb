// Main-thread access to the Rust engine.
// - engineCall(): synchronous calls (legal moves, game status) on a main-thread instance.
// - EngineClient: searches in its own Web Worker; cancel() terminates it.
// - engineSearch()/engineNewGame()/cancelSearch(): a shared client used for play mode.
import wasmUrl from './goldrush_engine.wasm?url';
import EngineWorker from './engineWorker.js?worker';
import { loadEngine } from './engineWasm.js';

let syncEngine = null;
let syncPromise = null;

/** Load the main-thread instance (call once before engineCall). */
export function loadSyncEngine() {
  if (!syncPromise) syncPromise = loadEngine(wasmUrl).then(e => (syncEngine = e));
  return syncPromise;
}

export function engineReady() {
  return syncEngine !== null;
}

/** Synchronous request: { cmd: 'legal' | 'status', position }. */
export function engineCall(request) {
  if (!syncEngine) throw new Error('Engine not loaded');
  return syncEngine.call(request);
}

/** An engine running in a dedicated Web Worker. */
export class EngineClient {
  #worker = null;
  #nextId = 1;
  #pending = new Map();

  #getWorker() {
    if (!this.#worker) {
      this.#worker = new EngineWorker();
      this.#worker.onmessage = (e) => {
        const { id, result, error } = e.data;
        const p = this.#pending.get(id);
        if (!p) return;
        this.#pending.delete(id);
        if (error) p.reject(new Error(error));
        else p.resolve(result);
      };
    }
    return this.#worker;
  }

  call(request) {
    return new Promise((resolve, reject) => {
      const id = this.#nextId++;
      this.#pending.set(id, { resolve, reject });
      this.#getWorker().postMessage({ id, request });
    });
  }

  /** Abort any running request by terminating the worker (pending promises reject). */
  cancel() {
    if (!this.#worker) return;
    this.#worker.terminate();
    this.#worker = null;
    for (const p of this.#pending.values()) p.reject(new Error('cancelled'));
    this.#pending.clear();
  }
}

const playClient = new EngineClient();

/** Search a position. `limits`: { timeMs?, nodes?, depth? }. Resolves to the search result. */
export function engineSearch(position, limits) {
  return playClient.call({ cmd: 'search', position, ...limits });
}

/** Clear the play worker's transposition table (new game). */
export function engineNewGame() {
  return playClient.call({ cmd: 'newGame' });
}

/** Abort the running play-mode search. */
export function cancelSearch() {
  playClient.cancel();
}
