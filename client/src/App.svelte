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
  /* Slightly above the browser default (16px → 18px); everything sized in rem scales with it.
     A percentage keeps the user's own browser font-size setting in effect. */
  :global(html) { font-size: 112.5%; }
  :global(*, *::before, *::after) { box-sizing: border-box; }
  :global(body) {
    margin: 0;
    font-family: var(--font-body);
    font-size: 1rem;
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
  /* Checkboxes render as on/off switches site-wide. Sized in em so they follow the label's text. */
  :global(input[type="checkbox"]) {
    appearance: none;
    -webkit-appearance: none;
    position: relative;
    flex-shrink: 0;
    width: 2.8em;
    height: 1.55em;
    margin: 0;
    border: none;
    border-radius: 999px;
    background: var(--surface-3);
    box-shadow: inset 0 0 0 1px var(--line-strong);
    vertical-align: middle;
    cursor: pointer;
    transition: background 0.18s, box-shadow 0.18s;
  }
  :global(input[type="checkbox"]::before) {
    content: "";
    position: absolute;
    top: 0.2em;
    left: 0.2em;
    width: 1.15em;
    height: 1.15em;
    border-radius: 50%;
    background: var(--muted);
    box-shadow: 0 1px 2px rgba(0, 0, 0, 0.5);
    transition: transform 0.18s, background 0.18s;
  }
  :global(input[type="checkbox"]:hover::before) { background: var(--fg-2); }
  :global(input[type="checkbox"]:checked) {
    background: var(--button);
    box-shadow: inset 0 0 0 1px var(--accent);
  }
  :global(input[type="checkbox"]:checked:hover) { background: var(--button-hover); }
  :global(input[type="checkbox"]:checked::before) {
    transform: translateX(1.25em);
    background: var(--fg);
  }
  :global(input[type="checkbox"]:focus-visible) { outline: 2px solid var(--accent); outline-offset: 2px; }
  :global(input[type="checkbox"]:disabled) { opacity: 0.4; cursor: default; }
</style>
