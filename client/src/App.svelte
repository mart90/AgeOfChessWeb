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
  /* Site-wide palette and type (warm dark wood + gold, matching the board). Components use these
     variables instead of literal colours. */
  :global(:root) {
    color-scheme: dark;
    --bg:          #1b1916;   /* page */
    --surface:     #24211d;   /* panels, cards, nav */
    --surface-2:   #2d2924;   /* inputs, idle buttons */
    --surface-3:   #3a332b;   /* hover / selected surfaces */
    --line:        #3a352e;   /* borders, dividers */
    --line-strong: #4a433a;   /* stronger borders, tracks */
    --fg:          #ece5d8;   /* main text */
    --fg-2:        #cfc6b6;   /* secondary text */
    --muted:       #a59a8a;   /* labels, hints */
    --faint:       #7a7064;   /* very quiet text */
    --accent:      #d8a64a;   /* gold: links, selection, focus */
    --accent-2:    #e8c27a;   /* lighter gold: headings, hover */
    --button:      #9b6f24;   /* primary button background */
    --button-hover:#b0802c;
    --font-display: "Alegreya SC", Georgia, "Times New Roman", serif;
    --font-body:    "Alegreya Sans", "Segoe UI", system-ui, sans-serif;
  }
  :global(*, *::before, *::after) { box-sizing: border-box; }
  :global(body) {
    margin: 0;
    font-family: var(--font-body);
    font-size: 16px;
    background: var(--bg);
    color: var(--fg);
    min-height: 100vh;
    display: flex;
    flex-direction: column;
  }
  :global(h1, h2, h3) {
    font-family: var(--font-display);
    font-weight: 700;
    letter-spacing: 0.01em;
  }
  :global(button, input, select, textarea) {
    font-family: inherit;
  }
  :global(button) {
    cursor: pointer;
  }
</style>
