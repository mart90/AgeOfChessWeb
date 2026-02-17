// Reactive authentication state shared across all Svelte components.
// Uses a $state object so components re-render when token/user change.

export const authState = $state({
  /** JWT string, or null when logged out. */
  token: localStorage.getItem('auth_token'),
  /** Parsed user object from the server, or null. */
  user: null,
});

/** Store a new token + user and persist the token to localStorage. */
export function setAuth(token, user) {
  authState.token = token;
  authState.user  = user;
  localStorage.setItem('auth_token', token);
}

/** Clear auth state and remove the stored token. */
export function clearAuth() {
  authState.token = null;
  authState.user  = null;
  localStorage.removeItem('auth_token');
}
