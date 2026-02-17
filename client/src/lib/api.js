// ── Helpers ──────────────────────────────────────────────────────────────────

function getToken() {
  return localStorage.getItem('auth_token');
}

/** fetch() with an Authorization header when logged in. */
function authFetch(url, options = {}) {
  const token = getToken();
  const headers = { 'Content-Type': 'application/json', ...options.headers };
  if (token) headers['Authorization'] = `Bearer ${token}`;
  return fetch(url, { ...options, headers });
}

// ── Anonymous game API ───────────────────────────────────────────────────────

export async function createGame(options = {}) {
  // Send with auth header so the server can associate the creator's user account
  const res = await authFetch('/api/game', {
    method: 'POST',
    body: JSON.stringify(options),
  });
  if (!res.ok) throw new Error(await res.text());
  return res.json(); // { gameId, whitePlayerToken, inviteUrl }
}

export async function getMyGames() {
  const res = await authFetch('/api/game/my');
  if (!res.ok) return [];
  return res.json();
}

export async function getGameToken(gameId) {
  const res = await authFetch(`/api/game/${gameId}/token`);
  if (!res.ok) throw new Error(await res.text());
  return res.json(); // { playerToken, isWhite }
}

export async function getGame(gameId) {
  const res = await fetch(`/api/game/${gameId}`);
  if (!res.ok) throw new Error(await res.text());
  return res.json();
}

export async function getProfile(username) {
  const res = await fetch(`/api/user/${encodeURIComponent(username)}`);
  if (!res.ok) throw new Error(await res.text());
  return res.json(); // { username, displayName, stats, games }
}

// ── Auth API ─────────────────────────────────────────────────────────────────

export async function register(username, password, displayName, displayNameSameAsUsername) {
  const res = await fetch('/api/auth/register', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password, displayName, displayNameSameAsUsername }),
  });
  if (!res.ok) throw new Error(await res.text());
  return res.json(); // { token, user }
}

export async function login(username, password) {
  const res = await fetch('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  });
  if (!res.ok) throw new Error(await res.text());
  return res.json(); // { token, user }
}

export async function googleAuth(credential) {
  const res = await fetch('/api/auth/google', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ credential }),
  });
  if (!res.ok) throw new Error(await res.text());
  return res.json(); // { token, user }
}

export async function getMe() {
  const token = getToken();
  if (!token) return null;
  const res = await fetch('/api/auth/me', {
    headers: { 'Authorization': `Bearer ${token}` },
  });
  if (!res.ok) return null;
  return res.json();
}

// ── User settings ────────────────────────────────────────────────────────────

export async function updateSettings(displayName, displayNameSameAsUsername) {
  const res = await authFetch('/api/user/settings', {
    method: 'PUT',
    body: JSON.stringify({ displayName, displayNameSameAsUsername }),
  });
  if (!res.ok) throw new Error(await res.text());
  return res.json(); // updated user
}
