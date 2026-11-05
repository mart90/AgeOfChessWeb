import * as signalR from '@microsoft/signalr';

/**
 * The connection to the game hub, shared by the page and kept up while anything is subscribed.
 *
 * Phones freeze a page in the background and drop its socket, often without either side noticing
 * for a while. A connect attempt made on a network that isn't back yet can hang until the browser
 * gives up (30 s or more), and a SignalR connection can't be restarted while one is pending. So:
 *   - every connect attempt uses a fresh HubConnection and is abandoned after a time limit;
 *   - when the page comes back after being frozen, the old connection is replaced straight away
 *     (the server drops clients it hasn't heard from in 30 s anyway); after a shorter absence it's
 *     pinged, and replaced if it doesn't answer;
 *   - coming back also cuts a retry delay short, or a connect attempt that's taking too long.
 *
 * Usage: `hub.on` / `hub.off` for server events (they carry over to new connections), `hub.invoke`
 * for calls (made while reconnecting, they wait for the connection), and do the joining in
 * `onConnected`, which runs once connected and again after every reconnect.
 */

const RETRY_DELAYS     = [0, 1000, 2000, 4000];   // then every 4 s
const START_TIMEOUT_MS = 5000;     // per connect attempt; longer for later attempts (slow networks)
const KICK_AFTER_MS    = 1000;     // coming back abandons a connect attempt that's been going this long
const PING_TIMEOUT_MS  = 1500;
const PROBE_AFTER_MS   = 2000;     // back after being hidden this long: ping
const FROZEN_MS        = 20000;    // the page's timers didn't run this long: the connection is dead
const INVOKE_WAIT_MS   = 10000;    // calls made while disconnected wait this long for a connection
const IDLE_STOP_MS     = 5000;     // disconnect this long after the last subscriber left

const retryDelay   = (n) => RETRY_DELAYS[Math.min(n, RETRY_DELAYS.length - 1)];
const startTimeout = (n) => START_TIMEOUT_MS * Math.min(n + 1, 4);

let connection = null;          // the current connection, set once it's connected
let attempting = null;          // the connection being started, if any
let connecting = false;         // the connect loop is running
let kick       = null;          // cuts the connect loop's current wait (or slow attempt) short
let idleTimer  = null;
let watching   = false;
let lastTick   = Date.now();    // heartbeat, to notice the page was frozen
let wasFrozen  = false;
let hiddenAt   = null;
const handlers           = new Map();   // event → Set of handlers
const connectedListeners = new Set();
const statusListeners    = new Set();
const waiters            = new Set();   // invokes waiting for a connection

const hub = { on, off, invoke };

/** The game hub: `on`, `off` and `invoke` (handlers can be added before it's connected). */
export function getGameHub() {
  return hub;
}

/**
 * Runs `fn` now if connected and after every (re)connect. Starts the connection if needed.
 * Returns an unsubscribe function.
 */
export function onConnected(fn) {
  watchPage();
  clearTimeout(idleTimer);
  connectedListeners.add(fn);
  if (connection) fn();
  else ensureConnecting();
  return () => {
    connectedListeners.delete(fn);
    if (!connectedListeners.size) idleTimer = setTimeout(stopIfIdle, IDLE_STOP_MS);
  };
}

/** Reconnects with the current sign-in (the server reads who's connected when connecting). */
export function restartGameHub() {
  if (connection) replace();   // (an attempt under way checks the sign-in when it connects)
}

/** `fn('disconnected' | 'connected')` on connection changes. Returns an unsubscribe function. */
export function onConnectionStatus(fn) {
  statusListeners.add(fn);
  return () => statusListeners.delete(fn);
}

function on(event, fn) {
  if (!handlers.has(event)) {
    handlers.set(event, new Set());
    for (const conn of [connection, attempting]) if (conn) dispatch(conn, event);
  }
  handlers.get(event).add(fn);
}

function off(event, fn) {
  handlers.get(event)?.delete(fn);
}

async function invoke(method, ...args) {
  const conn = connection ?? await waitForConnection();
  return conn.invoke(method, ...args);
}

// ── Connecting ──────────────────────────────────────────────────────────────

function build() {
  const conn = new signalR.HubConnectionBuilder()
    .withUrl('/hub/game', {
      // Read on every connect, so it picks up a login that happened after the page load
      accessTokenFactory: () => (conn.usedToken = currentToken()),
    })
    .build();
  // The server pings every 5 s (Program.cs): notice a dead connection after 15 s instead of 30
  conn.serverTimeoutInMilliseconds = 15000;
  for (const event of handlers.keys()) dispatch(conn, event);
  conn.onclose(() => { if (conn === connection) lost(); });
  return conn;
}

/** Passes `event` on to the handlers, as long as `conn` is the current connection. */
function dispatch(conn, event) {
  conn.on(event, (...args) => {
    if (conn !== connection) return;
    for (const fn of [...handlers.get(event)]) fn(...args);
  });
}

/** Connects if not connected, retrying until it's up (or nothing needs it any more). */
function ensureConnecting() {
  if (connecting || connection || !connectedListeners.size) return;
  connecting = true;
  (async () => {
    for (let attempt = 0; connectedListeners.size; attempt++) {
      const conn = attempting = build();
      const result = await race(conn.start().then(() => 'ok', () => 'failed'), startTimeout(attempt), Date.now() + KICK_AFTER_MS);
      attempting = null;
      // (signed in or out while connecting: connect again, so the server knows who this is)
      if (result === 'ok' && connectedListeners.size && conn.usedToken === currentToken()) { connection = conn; return; }
      conn.stop().catch(() => {});   // abandoned: may still be hanging, it's not awaited
      if (result === 'kicked') { attempt = -1; continue; }
      if (await race(new Promise(resolve => setTimeout(resolve, retryDelay(attempt + 1))), null, 0) === 'kicked') attempt = -1;
    }
  })().finally(() => {
    connecting = false;
    if (connection) connected();
  });
}

/**
 * Waits for `promise`, `timeoutMs` (if given) or a kick, whichever comes first: resolves to the
 * promise's value, 'timeout' or 'kicked'. Kicks before `kickableAt` (a timestamp) are ignored.
 */
function race(promise, timeoutMs, kickableAt) {
  let timer;
  return new Promise(resolve => {
    if (timeoutMs != null) timer = setTimeout(() => resolve('timeout'), timeoutMs);
    kick = () => { if (Date.now() >= kickableAt) resolve('kicked'); };
    promise.then(resolve);
  }).finally(() => { clearTimeout(timer); kick = null; });
}

function connected() {
  emitStatus('connected');
  for (const fn of [...connectedListeners]) fn();
  for (const resolve of [...waiters]) resolve(connection);
}

/** The current connection closed. */
function lost() {
  connection = null;
  emitStatus('disconnected');
  ensureConnecting();
}

/** The current connection is (presumably) dead: drop it without waiting for it, and reconnect. */
function replace() {
  const old = connection;
  connection = null;   // ignores its events and its close from here on
  old?.stop().catch(() => {});
  emitStatus('disconnected');
  ensureConnecting();
}

function stopIfIdle() {
  if (connectedListeners.size || !connection) return;
  const old = connection;
  connection = null;
  old.stop().catch(() => {});
}

function waitForConnection() {
  ensureConnecting();
  return new Promise((resolve, reject) => {
    const done = (conn) => { clearTimeout(timer); waiters.delete(done); resolve(conn); };
    const timer = setTimeout(() => { waiters.delete(done); reject(new Error('Not connected to the game server')); }, INVOKE_WAIT_MS);
    waiters.add(done);
  });
}

function currentToken() { return localStorage.getItem('auth_token') ?? ''; }

function emitStatus(s) { for (const fn of statusListeners) fn(s); }

// ── Coming back (phone unlocked, tab shown, network back) ───────────────────

function watchPage() {
  if (watching) return;
  watching = true;
  setInterval(() => {
    const now = Date.now();
    if (now - lastTick > FROZEN_MS) {
      wasFrozen = true;
      if (!document.hidden) check(false);
    }
    lastTick = now;
  }, 1000);
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) { hiddenAt = Date.now(); return; }
    check(hiddenAt != null && Date.now() - hiddenAt >= PROBE_AFTER_MS);
    hiddenAt = null;
  });
  window.addEventListener('pageshow', (e) => { if (e.persisted) { wasFrozen = true; check(false); } });
  window.addEventListener('online', () => check(true));
}

/** Gets connected now rather than after the next retry; `probe` pings a connection that looks up. */
async function check(probe) {
  // The heartbeat may not have run yet since the page woke up
  const frozen = wasFrozen || Date.now() - lastTick > FROZEN_MS;
  wasFrozen = false;
  lastTick = Date.now();   // so the heartbeat doesn't report the same freeze again
  if (!connectedListeners.size) return;
  if (!connection) { kick?.(); ensureConnecting(); return; }
  if (frozen) { replace(); return; }
  if (!probe) return;
  const conn = connection;
  if (!(await ping(conn)) && conn === connection) replace();
}

function ping(conn) {
  return Promise.race([
    conn.invoke('Ping').then(() => true, () => false),
    new Promise(resolve => setTimeout(() => resolve(false), PING_TIMEOUT_MS)),
  ]);
}
