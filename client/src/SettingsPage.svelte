<script>
  import { onMount }   from 'svelte';
  import { authState } from './lib/auth.svelte.js';
  import { updateSettings } from './lib/api.js';
  import { navigate }  from './lib/navigate.js';

  let displayName        = $state('');
  let sameAsUsername     = $state(false);
  let saving             = $state(false);
  let saved              = $state(false);
  let error              = $state('');

  onMount(() => {
    if (!authState.token) { navigate('/login'); return; }
  });

  // authState.user is loaded asynchronously in App.svelte — use $effect so the
  // form initialises correctly even if the fetch completes after onMount runs.
  let _formInitialized = false;
  $effect(() => {
    const u = authState.user;
    if (u && !_formInitialized) {
      _formInitialized = true;
      sameAsUsername = u.displayName == null;
      displayName    = u.displayName ?? '';
    }
  });

  async function handleSave(e) {
    e.preventDefault();
    saving = true; saved = false; error = '';
    try {
      const user = await updateSettings(displayName || null, sameAsUsername);
      authState.user = { ...authState.user, ...user };
      saved = true;
    } catch (err) {
      error = err.message;
    } finally {
      saving = false;
    }
  }

</script>

<main>
  <h1>Account Settings</h1>

  {#if !authState.token}
    <p class="muted">Please log in to access settings.</p>
  {:else}

    <section class="card">
      <h2>Profile</h2>
      <p class="username-row">Username: <strong>{authState.user?.username ?? '…'}</strong></p>

      <form onsubmit={handleSave}>
        <div class="display-name-block">
          <label>
            Display name
            <input
              type="text"
              bind:value={displayName}
              disabled={sameAsUsername}
              placeholder={sameAsUsername ? (authState.user?.username ?? '') : ''}
              maxlength="40"
            />
          </label>
          <label class="checkbox-label">
            <input type="checkbox" bind:checked={sameAsUsername} />
            Same as username
          </label>
        </div>

        {#if error}<p class="error">{error}</p>{/if}
        {#if saved}<p class="success">Saved!</p>{/if}

        <button type="submit" disabled={saving}>
          {saving ? 'Saving…' : 'Save changes'}
        </button>
      </form>
    </section>

  {/if}
</main>

<style>
  main {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 1.5rem;
    padding: 2rem 1rem;
    flex: 1;
  }
  h1 { margin: 0; font-size: 2rem; }

  .card {
    width: 100%;
    max-width: 400px;
    background: #1e1e38;
    border: 1px solid #2a2a4a;
    border-radius: 8px;
    padding: 1.2rem 1.4rem;
    display: flex;
    flex-direction: column;
    gap: 0.85rem;
  }
  h2 { margin: 0 0 0.25rem; font-size: 1.1rem; color: #bbb; }

  .username-row { margin: 0; font-size: 0.9rem; color: #aaa; }
  .username-row strong { color: #eee; }

  form {
    display: flex;
    flex-direction: column;
    gap: 0.7rem;
  }
  label {
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
    font-size: 0.9rem;
    color: #ccc;
  }
  input[type="text"] {
    padding: 0.5rem 0.75rem;
    background: #22223a;
    border: 1px solid #444;
    border-radius: 6px;
    color: #eee;
    font-size: 1rem;
  }
  input[type="text"]:focus { outline: none; border-color: #7b8cde; }
  input[type="text"]:disabled { opacity: 0.4; cursor: default; }

  .display-name-block {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }
  .checkbox-label {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 0.85rem;
    color: #aaa;
    flex-direction: row;
    cursor: pointer;
  }

  button[type="submit"] {
    padding: 0.55rem 1.2rem;
    background: #4a6fa5;
    color: #fff;
    border: none;
    border-radius: 6px;
    font-size: 0.95rem;
    align-self: flex-start;
  }
  button[type="submit"]:disabled { opacity: 0.5; cursor: default; }

  .error   { color: #e07070; font-size: 0.85rem; margin: 0; }
  .success { color: #7cfc00; font-size: 0.85rem; margin: 0; }
  .muted   { color: #666; }

</style>
