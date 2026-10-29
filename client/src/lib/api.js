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

/** Start a game vs the computer: { boardSize, mapMode, mapSeed, level, color: 'white'|'black'|'random' }. */
export async function createComputerGame(options = {}) {
  const res = await authFetch('/api/game/computer', {
    method: 'POST',
    body: JSON.stringify(options),
  });
  if (!res.ok) throw new Error(await res.text());
  return res.json(); // { gameId, playerToken, isWhite }
}

export async function getMyGames() {
  const res = await authFetch('/api/game/my');
  if (!res.ok) return [];
  return res.json();
}

export async function generateSandboxBoard(size, isRandom, seed = null) {
  const res = await fetch('/api/sandbox/board', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ size, isRandom, seed }),
  });
  if (!res.ok) throw new Error(await res.text());
  return res.json(); // { mapSeed, mapSize, squares }
}

export async function getOpenLobbies() {
  const res = await authFetch('/api/game/open-lobbies');
  if (!res.ok) return [];
  return res.json();
}

export async function joinLobby(gameId) {
  const res = await authFetch(`/api/game/${gameId}/join`, { method: 'POST' });
  if (!res.ok) throw new Error(await res.text());
  return res.json(); // { gameId, blackPlayerToken }
}

export async function cancelChallenge(gameId) {
  const res = await authFetch(`/api/game/${gameId}`, { method: 'DELETE' });
  if (!res.ok) throw new Error(await res.text());
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

export async function getProfile(username, startIndex = 0, sortCol = 'endedAt', sortDir = 'desc', category = 'all') {
  const params = new URLSearchParams({ startIndex, sortCol, sortDir, category });
  const res = await fetch(`/api/user/${encodeURIComponent(username)}?${params}`);
  if (!res.ok) throw new Error(await res.text());
  return res.json(); // { username, displayName, stats, games, totalGames }
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

// ── Game library ("My library") ──────────────────────────────────────────────
// entry: { title, gameId, mapSize, mapSeed, plyCount, result,
//          data: packed tree (gameTransfer.js) }

async function libraryError(res) {
  const text = await res.text();
  try { const p = JSON.parse(text); if (p?.title) return new Error(p.title); } catch { /* plain text */ }
  return new Error(text.replace(/^"|"$/g, '') || `Request failed (${res.status})`);
}

export async function getLibrary() {
  const res = await authFetch('/api/library');
  if (!res.ok) throw await libraryError(res);
  return res.json();
}

export async function getLibraryGame(id) {
  const res = await authFetch(`/api/library/${id}`);
  if (!res.ok) throw await libraryError(res);
  return res.json();
}

/** An entry opened through its share link (/analyze/{shareId}); works logged out for published entries. */
export async function getSharedLibraryGame(shareId) {
  const res = await authFetch(`/api/library/shared/${encodeURIComponent(shareId)}`);
  if (!res.ok) throw await libraryError(res);
  return res.json(); // { id (owner only), shareId, isPublic, owned, owner, ownerUsername, title, result, mapSize, mapSeed, data }
}

export async function setLibraryGamePublic(id, isPublic) {
  const res = await authFetch(`/api/library/${id}/public`, { method: 'PUT', body: JSON.stringify({ isPublic }) });
  if (!res.ok) throw await libraryError(res);
  return res.json();
}

// The tree is sent as a string: it nests one level per move, deeper than ASP.NET binds objects
const libraryBody = (entry) => JSON.stringify({ ...entry, data: JSON.stringify(entry.data) });

export async function addLibraryGame(entry) {
  const res = await authFetch('/api/library', { method: 'POST', body: libraryBody(entry) });
  if (!res.ok) throw await libraryError(res);
  return res.json(); // { id, shareId }
}

export async function updateLibraryGame(id, entry) {
  const res = await authFetch(`/api/library/${id}`, { method: 'PUT', body: libraryBody(entry) });
  if (!res.ok) throw await libraryError(res);
  return res.json();
}

export async function deleteLibraryGame(id) {
  const res = await authFetch(`/api/library/${id}`, { method: 'DELETE' });
  if (!res.ok) throw await libraryError(res);
}
