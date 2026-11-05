// Notifications (the bell in the nav bar). Stored on the server and pushed live over the game hub,
// whose connection stays up on every page while signed in. Read ones disappear.
import { getGameHub, onConnected } from './hub.js';
import { playSound } from './sound.js';

export const notifications = $state({
  /** Unread, newest first: { id, type, gameId, opponentName, createdAt }. */
  items: [],
  /** The game whose page is open (GamePage sets it): it shows its own news. */
  openGameId: null,
});

const hub = getGameHub();
let stopConnected = null;

function authHeaders() {
  const token = localStorage.getItem('auth_token');
  return token ? { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` } : null;
}

async function refresh() {
  const headers = authHeaders();
  if (!headers) { notifications.items = []; return; }
  try {
    const res = await fetch('/api/notifications', { headers });
    if (res.ok) notifications.items = await res.json();
  } catch { /* kept as it was; fetched again on the next connect */ }
}

function onNotification(n) {
  if (n.gameId != null && n.gameId === notifications.openGameId) {
    // The game's page shows it (and plays its own sound); read once it's seen
    if (!document.hidden) { markRead({ ids: [n.id] }, true); return; }
    notifications.items = [n, ...notifications.items.filter(x => x.id !== n.id)];
    return;
  }
  notifications.items = [n, ...notifications.items.filter(x => x.id !== n.id)];
  if (n.type === 'gameStarted') playSoundOnce(`game-started-${n.gameId}`, 'game_started');
}

/** Starts receiving notifications (signed in). Returns a function that stops it again. */
export function startNotifications() {
  hub.on('Notification', onNotification);
  hub.on('NotificationsChanged', refresh);
  stopConnected = onConnected(refresh);   // also keeps the connection up on every page
  return () => {
    hub.off('Notification', onNotification);
    hub.off('NotificationsChanged', refresh);
    stopConnected?.();
    stopConnected = null;
    notifications.items = [];
  };
}

/** Marks notifications read: `{ ids }` or all about `{ gameId }` (only if listed, unless `always`). */
export async function markRead({ ids, gameId }, always = false) {
  const headers = authHeaders();
  if (!headers) return;
  const matches = (n) => ids ? ids.includes(n.id) : n.gameId === gameId;
  if (!always && !notifications.items.some(matches)) return;
  notifications.items = notifications.items.filter(n => !matches(n));
  try {
    await fetch('/api/notifications/read', { method: 'POST', headers, body: JSON.stringify(ids ? { ids } : { gameId }) });
  } catch { /* still unread on the server: back on the next fetch */ }
}

/**
 * Plays a sound in only one of the user's open tabs (they all get the news): whichever takes the
 * lock first, a visible tab before hidden ones. Best effort: a tab not clicked in since it loaded
 * can't play sound, and older browsers without locks play it in every tab.
 */
export function playSoundOnce(key, sound) {
  const play = () => {
    if (!navigator.locks) { playSound(sound); return; }
    navigator.locks.request(`sound-${key}`, { ifAvailable: true }, async (lock) => {
      if (!lock) return;
      playSound(sound);
      await new Promise(resolve => setTimeout(resolve, 5000));   // the other tabs have had theirs by then
    });
  };
  if (document.hidden) setTimeout(play, 300); else play();
}
