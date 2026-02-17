<script>
  import { onMount } from 'svelte';
  import { navigate }   from './lib/navigate.js';
  import { login, googleAuth } from './lib/api.js';
  import { setAuth }    from './lib/auth.svelte.js';

  // Set VITE_GOOGLE_CLIENT_ID in your .env to enable the Google Sign-In button
  const GOOGLE_CLIENT_ID = import.meta.env.VITE_GOOGLE_CLIENT_ID ?? '';

  let username = $state('');
  let password = $state('');
  let error    = $state('');
  let loading  = $state(false);

  async function handleLogin(e) {
    e.preventDefault();
    loading = true;
    error   = '';
    try {
      const { token, user } = await login(username, password);
      setAuth(token, user);
      navigate('/');
    } catch (err) {
      error = err.message;
    } finally {
      loading = false;
    }
  }

  // ── Google Sign-In ──────────────────────────────────────────────────────────

  function initGoogle() {
    if (!GOOGLE_CLIENT_ID || !window.google?.accounts?.id) return;
    window.google.accounts.id.initialize({
      client_id: GOOGLE_CLIENT_ID,
      callback: async ({ credential }) => {
        loading = true; error = '';
        try {
          const { token, user } = await googleAuth(credential);
          setAuth(token, user);
          navigate('/');
        } catch (err) {
          error = err.message;
        } finally {
          loading = false;
        }
      },
    });
    window.google.accounts.id.renderButton(
      document.getElementById('google-btn'),
      { theme: 'outline', size: 'large', width: 280 },
    );
  }

  onMount(() => {
    if (!GOOGLE_CLIENT_ID) return;

    // Load the Google Identity Services script dynamically
    const existing = document.querySelector('script[src*="accounts.google.com/gsi"]');
    if (existing) {
      if (window.google?.accounts?.id) initGoogle();
      else existing.addEventListener('load', initGoogle, { once: true });
      return;
    }

    const script   = document.createElement('script');
    script.src     = 'https://accounts.google.com/gsi/client';
    script.async   = true;
    script.onload  = initGoogle;
    document.head.appendChild(script);
  });
</script>

<main>
  <h1>Log in</h1>

  <form onsubmit={handleLogin}>
    <label>
      Username
      <input type="text" bind:value={username} autocomplete="username" required />
    </label>
    <label>
      Password
      <input type="password" bind:value={password} autocomplete="current-password" required />
    </label>

    {#if error}<p class="error">{error}</p>{/if}

    <button type="submit" disabled={loading}>
      {loading ? 'Logging in…' : 'Log in'}
    </button>
  </form>

  {#if GOOGLE_CLIENT_ID}
    <div class="divider"><span>or</span></div>
    <div id="google-btn"></div>
  {/if}

  <p class="switch">
    Don't have an account?
    <button class="link-btn" onclick={() => navigate('/register')}>Register</button>
  </p>
</main>

<style>
  main {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    flex: 1;
    gap: 1.2rem;
    padding: 2rem 1rem;
  }
  h1 { margin: 0 0 0.5rem; font-size: 2rem; }

  form {
    display: flex;
    flex-direction: column;
    gap: 0.8rem;
    width: 100%;
    max-width: 320px;
  }
  label {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
    font-size: 0.9rem;
    color: #ccc;
  }
  input {
    padding: 0.5rem 0.75rem;
    background: #22223a;
    border: 1px solid #444;
    border-radius: 6px;
    color: #eee;
    font-size: 1rem;
  }
  input:focus { outline: none; border-color: #7b8cde; }

  button[type="submit"] {
    padding: 0.6rem;
    background: #4a6fa5;
    color: #fff;
    border: none;
    border-radius: 6px;
    font-size: 1rem;
    margin-top: 0.3rem;
  }
  button[type="submit"]:disabled { opacity: 0.5; cursor: default; }

  .error { color: #e07070; font-size: 0.88rem; margin: 0; }

  .divider {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    width: 100%;
    max-width: 320px;
    color: #555;
    font-size: 0.85rem;
  }
  .divider::before,
  .divider::after {
    content: '';
    flex: 1;
    height: 1px;
    background: #333;
  }

  .switch { color: #aaa; font-size: 0.85rem; }
  .link-btn {
    background: none; border: none;
    color: #7b8cde; text-decoration: underline;
    font-size: inherit; padding: 0; cursor: pointer;
  }
</style>
