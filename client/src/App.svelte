<script>
  import { onMount }     from 'svelte';
  import NavBar          from './NavBar.svelte';
  import HomePage        from './HomePage.svelte';
  import GamePage        from './GamePage.svelte';
  import LoginPage       from './LoginPage.svelte';
  import RegisterPage    from './RegisterPage.svelte';
  import SettingsPage    from './SettingsPage.svelte';
  import WatchPage       from './WatchPage.svelte';
  import ProfilePage     from './ProfilePage.svelte';
  import AnalyzePage     from './AnalyzePage.svelte';
  import RulesPage       from './RulesPage.svelte';
  import AboutPage       from './AboutPage.svelte';
  import { authState, clearAuth } from './lib/auth.svelte.js';
  import { getMe }       from './lib/api.js';

  let path = $state(window.location.pathname);

  window.addEventListener('popstate', () => { path = window.location.pathname; });

  const gameMatch    = $derived(path.match(/^\/game\/(\d+)/));
  const gameId       = $derived(gameMatch ? parseInt(gameMatch[1]) : null);
  const profileMatch = $derived(path.match(/^\/profile\/([^/]+)/));
  const profileUser  = $derived(profileMatch ? decodeURIComponent(profileMatch[1]) : null);

  const NAME = 'Goldrush Gambit';

  $effect(() => {
    if (gameId !== null) return; // GamePage sets its own title
    if (path === '/')          document.title = `${NAME} - Play`;
    else if (path === '/watch')    document.title = `${NAME} - Watch`;
    else if (path === '/analyze')  document.title = `${NAME} - Analyze`;
    else if (path === '/rules') document.title = `${NAME} - Rules`;
    else if (path === '/about') document.title = `${NAME} - About`;
    else                           document.title = NAME;
  });

  // Restore user session from stored JWT on first load
  onMount(async () => {
    if (authState.token && !authState.user) {
      const user = await getMe();
      if (user) {
        authState.user = user;
      } else {
        // Token is expired or invalid — clear it
        clearAuth();
      }
    }
  });
</script>

<NavBar />

{#if gameId !== null}
  {#key gameId}
    <GamePage {gameId} />
  {/key}
{:else if profileUser !== null}
  {#key profileUser}
    <ProfilePage username={profileUser} />
  {/key}
{:else if path === '/login'}
  <LoginPage />
{:else if path === '/register'}
  <RegisterPage />
{:else if path === '/settings'}
  <SettingsPage />
{:else if path === '/watch'}
  <WatchPage />
{:else if path === '/analyze'}
  <AnalyzePage />
{:else if path === '/rules'}
  <RulesPage />
{:else if path === '/about'}
  <AboutPage />
{:else}
  <HomePage />
{/if}

<style>
  :global(*, *::before, *::after) { box-sizing: border-box; }
  :global(body) {
    margin: 0;
    font-family: sans-serif;
    background: #1a1a2e;
    color: #eee;
    min-height: 100vh;
    display: flex;
    flex-direction: column;
  }
  :global(button) {
    cursor: pointer;
    font-family: inherit;
  }
</style>
