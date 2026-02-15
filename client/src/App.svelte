<script>
  import HomePage from './HomePage.svelte';
  import GamePage from './GamePage.svelte';

  let path = $state(window.location.pathname);

  window.addEventListener('popstate', () => { path = window.location.pathname; });

  const gameMatch = $derived(path.match(/^\/game\/(\d+)/));
  const gameId    = $derived(gameMatch ? parseInt(gameMatch[1]) : null);
</script>

{#if gameId !== null}
  <GamePage {gameId} />
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
  }
  :global(button) {
    cursor: pointer;
    font-family: inherit;
  }
</style>
