import * as signalR from '@microsoft/signalr';

/**
 * The connection to the game hub: one per page, kept up for as long as anything is subscribed.
 *
 * Phones drop the socket while the page is in the background, often without either side noticing
 * for a while, and SignalR's default reconnect gives up after four tries (which happen in the
 * background). So here reconnecting never gives up, and when the page becomes visible again the
 * connection is checked straight away: restarted if it's down or still waiting out a retry delay,
 * pinged if it looks up.
 *
 * Usage: register event handlers with `hub.on` (and remove them with `hub.off`), and do the
 * joining in `onConnected`, which runs once connected and again after every reconnect.
 */

const RETRY_DELAYS          = [0, 1000, 2000, 5000];   // then every 5 s
const CHECK_AFTER_HIDDEN_MS = 3000;
const PING_TIMEOUT_MS       = 1500;

const { Connected, Disconnected, Reconnecting } = signalR.HubConnectionState;
const retryDelay = (n) => RETRY_DELAYS[Math.min(n, RETRY_DELAYS.length - 1)];

let connection = null;
let connecting = null;          // the running connect loop, if any
let wake       = null;          // cuts the connect loop's current wait short
let restarting = false;
let hiddenAt   = null;
const connectedListeners = new Set();
const statusListeners    = new Set();

/** The game hub connection (handlers can be added before it's connected). */
export function getGameHub() {
  if (!connection) build();
  return connection;
}

/**
 * Runs `fn` now if connected and after every (re)connect. Starts the connection if needed.
 * Returns an unsubscribe function.
 */
export function onConnected(fn) {
  getGameHub();
  connectedListeners.add(fn);
  if (connection.state === Connected) fn();
  else ensureConnecting();
  return () => connectedListeners.delete(fn);
}

/** `fn('disconnected' | 'connected')` on connection changes. Returns an unsubscribe function. */
export function onConnectionStatus(fn) {
  statusListeners.add(fn);
  return () => statusListeners.delete(fn);
}

function build() {
  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hub/game', {
      // Called on every connect, so it picks up a login that happened after the page load
      accessTokenFactory: () => localStorage.getItem('auth_token') ?? '',
    })
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: ({ previousRetryCount }) => retryDelay(previousRetryCount) })
    .build();
  // The server pings every 5 s (Program.cs): notice a dead connection after 15 s instead of 30
  connection.serverTimeoutInMilliseconds = 15000;

  connection.onreconnecting(() => emitStatus('disconnected'));
  connection.onreconnected(connected);
  connection.onclose(() => {
    emitStatus('disconnected');
    if (connectedListeners.size) ensureConnecting();
  });

  document.addEventListener('visibilitychange', () => {
    if (document.hidden) { hiddenAt = Date.now(); return; }
    const away = hiddenAt == null ? 0 : Date.now() - hiddenAt;
    hiddenAt = null;
    check(away >= CHECK_AFTER_HIDDEN_MS);
  });
  window.addEventListener('online', () => check(true));
}

function emitStatus(s) { for (const fn of statusListeners) fn(s); }

function connected() {
  emitStatus('connected');
  for (const fn of connectedListeners) fn();
}

/** Starts the connection if it's down, retrying until it's up. */
function ensureConnecting() {
  if (connecting || restarting || connection.state !== Disconnected) return;
  connecting = (async () => {
    for (let attempt = 0; connection.state === Disconnected; attempt++) {
      try {
        await connection.start();
      } catch {
        await new Promise(resolve => { wake = resolve; setTimeout(resolve, retryDelay(attempt + 1)); });
        wake = null;
      }
    }
  })().finally(() => {
    connecting = null;
    if (connection.state === Connected) connected();
  });
}

/** Back in view / back online: get connected now rather than after the next retry delay. */
async function check(probe) {
  if (!connectedListeners.size) return;
  if (connection.state === Disconnected) { wake?.(); ensureConnecting(); }
  else if (connection.state === Reconnecting) restart();
  else if (connection.state === Connected && probe && !(await ping())) restart();
}

function ping() {
  return Promise.race([
    connection.invoke('Ping').then(() => true, () => false),
    new Promise(resolve => setTimeout(() => resolve(false), PING_TIMEOUT_MS)),
  ]);
}

async function restart() {
  if (restarting) return;
  restarting = true;
  try { await connection.stop(); } catch { /* already down */ }
  restarting = false;
  ensureConnecting();
}
