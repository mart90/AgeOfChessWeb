<script>
  import { onMount }   from 'svelte';
  import { navigate }  from './lib/navigate.js';
  import { register, googleAuth } from './lib/api.js';
  import { setAuth }   from './lib/auth.svelte.js';

  const GOOGLE_CLIENT_ID = import.meta.env.VITE_GOOGLE_CLIENT_ID ?? '';

  let username           = $state('');
  let password           = $state('');
  let confirmPwd         = $state('');
  let displayName        = $state('');
  let sameAsUsername     = $state(true);
  let error              = $state('');
  let loading            = $state(false);

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
    const existing = document.querySelector('script[src*="accounts.google.com/gsi"]');
    if (existing) {
      if (window.google?.accounts?.id) initGoogle();
      else existing.addEventListener('load', initGoogle, { once: true });
      return;
    }
    const script  = document.createElement('script');
    script.src    = 'https://accounts.google.com/gsi/client';
    script.async  = true;
    script.onload = initGoogle;
    document.head.appendChild(script);
  });

  async function handleRegister(e) {
    e.preventDefault();
    if (password !== confirmPwd) { error = 'Passwords do not match.'; return; }
    loading = true;
    error   = '';
    try {
      const { token, user } = await register(
        username, password,
        displayName || null,
        sameAsUsername,
      );
      setAuth(token, user);
      navigate('/');
    } catch (err) {
      error = err.message;
    } finally {
      loading = false;
    }
  }
</script>

<main>
  <h1>Register</h1>

  <form onsubmit={handleRegister}>
    <label>
      Username <span class="hint">(3–20 chars, letters / numbers / _ / -)</span>
      <input
        type="text"
        bind:value={username}
        autocomplete="username"
        required
        minlength="3"
        maxlength="20"
        pattern="[a-zA-Z0-9_\-]+"
        title="Letters, numbers, underscores and hyphens only"
      />
    </label>

    <label>
      Password <span class="hint">(min. 6 characters)</span>
      <input type="password" bind:value={password} autocomplete="new-password" required minlength="6" />
    </label>

    <label>
      Confirm password
      <input type="password" bind:value={confirmPwd} autocomplete="new-password" required />
    </label>

    <div class="display-name-block">
      <label>
        Display name
        <input
          type="text"
          bind:value={displayName}
          disabled={sameAsUsername}
          placeholder={sameAsUsername ? (username || 'Same as username') : ''}
          maxlength="40"
        />
      </label>
      <label class="checkbox-label">
        <input type="checkbox" bind:checked={sameAsUsername} />
        Same as username
      </label>
    </div>

    {#if error}<p class="error">{error}</p>{/if}

    <button type="submit" disabled={loading}>
      {loading ? 'Creating account…' : 'Register'}
    </button>
  </form>

  {#if GOOGLE_CLIENT_ID}
    <div class="divider"><span>or</span></div>
    <div id="google-btn"></div>
  {/if}

  <p class="switch">
    Already have an account?
    <button class="link-btn" onclick={() => navigate('/login')}>Log in</button>
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
    max-width: 340px;
  }
  label {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
    font-size: 0.9rem;
    color: #ccc;
  }
  .hint { font-size: 0.78rem; color: #666; }
  input[type="text"],
  input[type="password"] {
    padding: 0.5rem 0.75rem;
    background: #22223a;
    border: 1px solid #444;
    border-radius: 6px;
    color: #eee;
    font-size: 1rem;
  }
  input:focus { outline: none; border-color: #7b8cde; }
  input:disabled { opacity: 0.4; cursor: default; }

  .display-name-block {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
    padding: 0.6rem 0.8rem;
    background: #1e1e38;
    border-radius: 6px;
    border: 1px solid #2a2a4a;
  }
  .checkbox-label {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 0.85rem;
    color: #aaa;
    cursor: pointer;
    flex-direction: row;
  }
  .checkbox-label input[type="checkbox"] { width: auto; }

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
    max-width: 340px;
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
