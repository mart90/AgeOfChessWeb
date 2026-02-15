<script>
  import { createGame } from './lib/api.js';
  import { navigate }   from './lib/navigate.js';

  const MODES = [
    { label: '5+5 Blitz',   settings: { timeControlEnabled: true,  startTimeMinutes: 5,  timeIncrementSeconds: 5  } },
    { label: '10+10 Rapid', settings: { timeControlEnabled: true,  startTimeMinutes: 10, timeIncrementSeconds: 10 } },
    { label: 'No timer',    settings: { timeControlEnabled: false } },
  ];

  let selectedMode = $state(0);
  let creating     = $state(false);
  let error        = $state('');

  async function handleCreate() {
    creating = true;
    error = '';
    try {
      const { gameId, whitePlayerToken, inviteUrl } = await createGame(MODES[selectedMode].settings);
      localStorage.setItem(`token_${gameId}`,     whitePlayerToken);
      localStorage.setItem(`color_${gameId}`,     'white');
      localStorage.setItem(`inviteUrl_${gameId}`, inviteUrl);
      navigate(`/game/${gameId}`);
    } catch (e) {
      error = String(e.message ?? e);
    } finally {
      creating = false;
    }
  }
</script>

<main>
  <h1>Age of Chess</h1>

  <div class="modes">
    {#each MODES as mode, i}
      <button
        class="mode-btn"
        class:selected={selectedMode === i}
        onclick={() => selectedMode = i}
      >{mode.label}</button>
    {/each}
  </div>

  <button class="create-btn" onclick={handleCreate} disabled={creating}>
    {creating ? 'Creating…' : 'Create Game'}
  </button>

  {#if error}<p class="error">{error}</p>{/if}
</main>

<style>
  main {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    min-height: 100vh;
    gap: 1rem;
    padding: 1rem;
  }
  h1 { font-size: 2.5rem; margin: 0 0 1rem; }
  .modes { display: flex; gap: 0.5rem; flex-wrap: wrap; justify-content: center; }
  .mode-btn {
    padding: 0.5rem 1rem;
    background: #2d2d4a;
    border: 2px solid #444;
    color: #eee;
    border-radius: 6px;
  }
  .mode-btn.selected { border-color: #7b8cde; background: #3d3d6a; }
  .create-btn {
    padding: 0.75rem 2rem;
    font-size: 1.1rem;
    background: #4a6fa5;
    color: #fff;
    border: none;
    border-radius: 8px;
  }
  .create-btn:disabled { opacity: 0.5; cursor: default; }
  .error { color: #e07070; }
</style>
