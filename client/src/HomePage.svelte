<script>
  import { onMount, onDestroy } from 'svelte';
  import { createGame, getMyGames, getGameToken } from './lib/api.js';
  import { navigate }   from './lib/navigate.js';
  import { startMatchmakingHub, stopMatchmakingHub } from './lib/matchmakingHub.js';
  import { authState } from './lib/auth.svelte.js';

  // ── Shared settings ───────────────────────────────────────────────────────

  const MODES = [
    // Blitz
    { label: '3+0',     settings: { timeControlEnabled: true,  startTimeMinutes: 3,  timeIncrementSeconds: 0  } },
    { label: '3+2',     settings: { timeControlEnabled: true,  startTimeMinutes: 3,  timeIncrementSeconds: 2  } },
    { label: '5+0',     settings: { timeControlEnabled: true,  startTimeMinutes: 5,  timeIncrementSeconds: 0  } },
    { label: '5+3',     settings: { timeControlEnabled: true,  startTimeMinutes: 5,  timeIncrementSeconds: 3  } },
    // Rapid
    { label: '10+5',    settings: { timeControlEnabled: true,  startTimeMinutes: 10, timeIncrementSeconds: 5  } },
    { label: '15+10',   settings: { timeControlEnabled: true,  startTimeMinutes: 15, timeIncrementSeconds: 10 } },
    // Slow
    { label: '30+15',   settings: { timeControlEnabled: true,  startTimeMinutes: 30, timeIncrementSeconds: 15 } },
    { label: '60+30',   settings: { timeControlEnabled: true,  startTimeMinutes: 60, timeIncrementSeconds: 30 } },
    { label: 'No timer',settings: { timeControlEnabled: false, startTimeMinutes: 0,  timeIncrementSeconds: 0  } },
  ];

  const MODE_GROUPS = [
    { label: 'Blitz', indices: [0, 1, 2, 3] },
    { label: 'Rapid', indices: [4, 5] },
    { label: 'Slow',  indices: [6, 7, 8] },
  ];

  // ── Settings persistence ─────────────────────────────────────────────────
  function getStored(key, def) {
    try { return JSON.parse(localStorage.getItem(key) ?? 'null') ?? def; } catch { return def; }
  }
  const _ap = getStored('autopair_settings', {});
  const _lb = getStored('lobby_settings', {});

  let tab           = $state(authState.token ? 'autopair' : 'lobby');  // 'autopair' | 'lobby'
  let selectedMode  = $state(_lb.selectedMode ?? 3);
  let boardSize     = $state(_lb.boardSize ?? 10);
  let boardSizeMin  = $state(_ap.boardSizeMin ?? 8);
  let boardSizeMax  = $state(_ap.boardSizeMax ?? 12);

  const trackGradient = $derived.by(() => {
    const pct = (v) => ((v - 6) / 10) * 100;
    return `background: linear-gradient(to right, #3a3a5a ${pct(boardSizeMin)}%, #7b8cde ${pct(boardSizeMin)}%, #7b8cde ${pct(boardSizeMax)}%, #3a3a5a ${pct(boardSizeMax)}%)`;
  });

  // ── Create lobby ─────────────────────────────────────────────────────────

  let biddingEnabled = $state(_lb.biddingEnabled ?? false);
  let creating       = $state(false);
  let lobbyError     = $state('');

  async function handleCreate() {
    creating = true;
    lobbyError = '';
    try {
      const modeSettings = MODES[selectedMode].settings;
      const settings = { ...modeSettings, boardSize, biddingEnabled };
      const { gameId, whitePlayerToken, inviteUrl: serverInviteUrl } = await createGame(settings);
      const serverUrl = new URL(serverInviteUrl);
      const inviteUrl = window.location.origin + serverUrl.pathname + serverUrl.search;
      localStorage.setItem(`token_${gameId}`,     whitePlayerToken);
      localStorage.setItem(`color_${gameId}`,     'white');
      localStorage.setItem(`inviteUrl_${gameId}`, inviteUrl);
      navigate(`/game/${gameId}`);
    } catch (e) {
      lobbyError = String(e.message ?? e);
    } finally {
      creating = false;
    }
  }

  // ── Auto pair ─────────────────────────────────────────────────────────────

  const BIDDING_OPTIONS = ['Disabled', 'Enabled', 'Any'];
  const TC_PREFS = [
    { value: 'blitz', label: 'Any blitz' },
    { value: 'rapid', label: 'Any rapid' },
    { value: 'slow',  label: 'Any slow'  },
    { value: 'any',   label: 'Any'       },
  ];
  // autopair: either a mode index (number) for a specific TC, or a TC_PREFS value (string)
  let autoTimeSel   = $state(_ap.autoTimeSel ?? 'any');
  let biddingPref   = $state(_ap.biddingPref ?? 'Any');

  $effect(() => {
    localStorage.setItem('autopair_settings', JSON.stringify({ autoTimeSel, biddingPref, boardSizeMin, boardSizeMax }));
  });
  $effect(() => {
    localStorage.setItem('lobby_settings', JSON.stringify({ selectedMode, boardSize, biddingEnabled }));
  });
  let queueing      = $state(false);
  let queueCount    = $state(0);
  let queueError    = $state('');
  let hub           = null;

  async function joinQueue() {
    queueError = '';
    queueing   = true;
    try {
      hub = await startMatchmakingHub();

      hub.on('QueueCount', (count) => { queueCount = count; });
      hub.on('MatchFound', ({ gameId, playerToken, isWhite }) => {
        localStorage.setItem(`token_${gameId}`, playerToken);
        localStorage.setItem(`color_${gameId}`, isWhite ? 'white' : 'black');
        stopMatchmakingHub();
        navigate(`/game/${gameId}`);
      });

      const isSpecific = typeof autoTimeSel === 'number';
      const m = isSpecific ? MODES[autoTimeSel].settings : { timeControlEnabled: false, startTimeMinutes: 0, timeIncrementSeconds: 0 };
      const timePref = isSpecific ? 'none' : autoTimeSel;
      await hub.invoke('JoinQueue',
        boardSizeMin, boardSizeMax,
        m.timeControlEnabled, m.startTimeMinutes, m.timeIncrementSeconds,
        timePref, biddingPref,
      );
    } catch (e) {
      queueError = String(e.message ?? e);
      queueing   = false;
    }
  }

  async function cancelQueue() {
    try { await hub?.invoke('LeaveQueue'); } catch { /* ignore */ }
    await stopMatchmakingHub();
    queueing   = false;
    queueCount = 0;
  }

  onDestroy(() => {
    if (queueing) cancelQueue();
  });

  // ── Ongoing games ─────────────────────────────────────────────────────────

  let myGames = $state([]);

  onMount(async () => {
    if (authState.token) {
      try { myGames = await getMyGames(); } catch { /* ignore */ }
    }
  });

  function timeLabel(g) {
    if (!g.timeControlEnabled) return 'No timer';
    const inc = g.timeIncrementSeconds > 0 ? `+${g.timeIncrementSeconds}` : '+0';
    return `${g.startTimeMinutes}${inc}`;
  }

  async function openGame(g) {
    // Always write color (authoritative from server)
    localStorage.setItem(`color_${g.id}`, g.isWhite ? 'white' : 'black');
    // Fetch token if missing (e.g. first visit from a new device)
    if (!localStorage.getItem(`token_${g.id}`)) {
      try {
        const { playerToken } = await getGameToken(g.id);
        localStorage.setItem(`token_${g.id}`, playerToken);
      } catch { /* navigate anyway; GamePage will fall back to spectator */ }
    }
    navigate(`/game/${g.id}`);
  }
</script>

<main>
  <!-- Ongoing games (logged-in users only) -->
  {#if myGames.length > 0}
    <div class="ongoing-section">
      <div class="ongoing-label">Ongoing games</div>
      <div class="ongoing-list">
        {#each myGames as g}
          <button class="ongoing-card" onclick={() => openGame(g)}>
            <span class="ongoing-opp">vs {g.opponentName}</span>
            <span class="ongoing-right">
              <span class="ongoing-tc">{timeLabel(g)}</span>
              {#if g.isMyTurn}<span class="your-turn">Your turn</span>{/if}
            </span>
          </button>
        {/each}
      </div>
    </div>
  {/if}

  <!-- Tab bar -->
  <div class="tabs">
    <button
      class="tab"
      class:active={tab === 'autopair'}
      onclick={() => { if (queueing) cancelQueue(); tab = 'autopair'; }}
    >Auto pair</button>
    <button
      class="tab"
      class:active={tab === 'lobby'}
      onclick={() => { if (queueing) cancelQueue(); tab = 'lobby'; }}
    >Create lobby</button>
  </div>

  <!-- Settings panel (shared) -->
  <div class="panel">
    <!-- Time control -->
    <div class="time-control-grid">
      {#each MODE_GROUPS as group}
        <div class="tc-group">
          <div class="section-label">{group.label}</div>
          <div class="modes">
            {#each group.indices as i}
              <button
                class="mode-btn"
                class:selected={tab === 'lobby' ? selectedMode === i : autoTimeSel === i}
                onclick={() => tab === 'lobby' ? (selectedMode = i) : (autoTimeSel = i)}
              >{MODES[i].label}</button>
            {/each}
          </div>
        </div>
      {/each}
    </div>

    <!-- Time control category flexibility (autopair only) -->
    {#if tab === 'autopair'}
      <div class="tc-group">
        <div class="section-label">All</div>
        <div class="modes">
          {#each TC_PREFS as { value, label }}
            <button class="mode-btn" class:selected={autoTimeSel === value} onclick={() => autoTimeSel = value}>{label}</button>
          {/each}
        </div>
      </div>
    {/if}

    <!-- Board size -->
    {#if tab === 'lobby'}
      <div class="option-row">
        <label for="board-size">Board size: <strong>{boardSize}×{boardSize}</strong></label>
        <input id="board-size" type="range" min="6" max="16" step="2" bind:value={boardSize} />
      </div>
    {:else}
      <div class="option-row">
        <span class="board-range-label">Board size: <strong>{boardSizeMin}×{boardSizeMin}{boardSizeMax !== boardSizeMin ? ` – ${boardSizeMax}×${boardSizeMax}` : ''}</strong></span>
      </div>
      <div class="dual-range">
        <div class="dual-range-track" style={trackGradient}></div>
        <input type="range" min="6" max="16" step="2"
          value={boardSizeMin}
          style="z-index: {boardSizeMin === boardSizeMax ? 4 : 2}"
          oninput={(e) => { boardSizeMin = +e.currentTarget.value; if (boardSizeMin > boardSizeMax) boardSizeMax = boardSizeMin; }} />
        <input type="range" min="6" max="16" step="2"
          value={boardSizeMax}
          style="z-index: 3"
          oninput={(e) => { boardSizeMax = +e.currentTarget.value; if (boardSizeMax < boardSizeMin) boardSizeMin = boardSizeMax; }} />
      </div>
    {/if}

    <!-- Bidding (different control per tab) -->
    {#if tab === 'lobby'}
      <div class="option-row">
        <label class="checkbox-label">
          <input type="checkbox" bind:checked={biddingEnabled} />
          Bid for colors
        </label>
      </div>
    {:else}
      <div class="option-row">
        <label for="bidding-pref">Bid for colors</label>
        <select id="bidding-pref" bind:value={biddingPref}>
          {#each BIDDING_OPTIONS as opt}
            <option value={opt}>{opt}</option>
          {/each}
        </select>
      </div>
    {/if}

    <!-- Tab-specific bottom section -->
    {#if tab === 'lobby'}
      {#if lobbyError}<p class="error">{lobbyError}</p>{/if}
      <button class="action-btn" onclick={handleCreate} disabled={creating}>
        {creating ? 'Creating…' : 'Create game'}
      </button>

    {:else}
      {#if !authState.token}
        <p class="login-prompt">
          <button class="link-btn" onclick={() => navigate('/login')}>Log in</button>
          or
          <button class="link-btn" onclick={() => navigate('/register')}>register</button>
          to use matchmaking.
        </p>
      {:else if !queueing}
        {#if queueError}<p class="error">{queueError}</p>{/if}
        <button class="action-btn" onclick={joinQueue}>Find game</button>
      {:else}
        <div class="queue-status">
          <div class="spinner"></div>
          <span>Looking for opponent…</span>
          {#if queueCount > 0}
            <span class="queue-count">{queueCount} {queueCount === 1 ? 'player' : 'players'} in queue</span>
          {/if}
        </div>
        <button class="cancel-btn" onclick={cancelQueue}>Cancel</button>
      {/if}
    {/if}
  </div>
</main>

<style>
  main {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    flex: 1;
    padding: 2rem 1rem;
  }

  /* ── Tabs ── */
  .tabs {
    display: flex;
    border-bottom: 2px solid #2a2a4a;
    width: 100%;
    max-width: 660px;
  }
  .tab {
    flex: 1;
    padding: 0.75rem 1rem;
    background: none;
    border: none;
    color: #888;
    font-size: 1rem;
    border-bottom: 2px solid transparent;
    margin-bottom: -2px;
  }
  .tab.active {
    color: #eee;
    border-bottom-color: #7b8cde;
  }
  .tab:hover:not(.active) { color: #ccc; }

  /* ── Settings panel ── */
  .panel {
    display: flex;
    flex-direction: column;
    gap: 1rem;
    width: 100%;
    max-width: 660px;
    padding: 1.75rem 2rem 1.5rem;
    background: #22223a;
    border: 1px solid #2a2a4a;
    border-top: none;
    border-radius: 0 0 8px 8px;
  }

  .time-control-grid {
    display: flex;
    flex-direction: column;
    gap: 0.6rem;
  }
  .tc-group {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
  }
  .section-label {
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: #666;
  }

  .modes {
    display: flex;
    gap: 0.4rem;
  }
  .mode-btn {
    flex: 1;
    padding: 0.55rem 0.5rem;
    background: #2d2d4a;
    border: 2px solid #3a3a5a;
    color: #ccc;
    border-radius: 6px;
    font-size: 0.95rem;
    white-space: nowrap;
  }
  .mode-btn.selected { border-color: #7b8cde; background: #3a3a6a; color: #eee; }

  .option-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.75rem;
  }
  .option-row label { font-size: 0.95rem; color: #ccc; white-space: nowrap; }
  .option-row input[type="range"] { flex: 1; accent-color: #7b8cde; }
  .board-range-label { font-size: 0.95rem; color: #ccc; white-space: nowrap; }

  .dual-range {
    position: relative;
    width: 100%;
    height: 20px;
  }
  .dual-range-track {
    position: absolute;
    left: 0; right: 0;
    top: 50%; transform: translateY(-50%);
    height: 4px;
    border-radius: 2px;
    pointer-events: none;
  }
  .dual-range input[type="range"] {
    position: absolute;
    width: 100%; height: 20px;
    top: 0; left: 0;
    margin: 0; padding: 0;
    background: transparent;
    -webkit-appearance: none; appearance: none;
    pointer-events: none;
  }
  .dual-range input[type="range"]::-webkit-slider-runnable-track {
    background: transparent; height: 4px;
  }
  .dual-range input[type="range"]::-moz-range-track {
    background: transparent; height: 4px;
  }
  .dual-range input[type="range"]::-webkit-slider-thumb {
    -webkit-appearance: none; appearance: none;
    pointer-events: all;
    width: 16px; height: 16px;
    border-radius: 50%;
    background: #7b8cde;
    cursor: pointer;
    margin-top: -6px;
    border: none;
    box-shadow: 0 0 0 2px #22223a;
  }
  .dual-range input[type="range"]::-moz-range-thumb {
    pointer-events: all;
    width: 12px; height: 12px;
    border-radius: 50%;
    background: #7b8cde;
    cursor: pointer;
    border: 2px solid #22223a;
  }
  .checkbox-label {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 0.95rem;
    color: #ccc;
    cursor: pointer;
  }

  select {
    background: #2d2d4a;
    border: 1px solid #3a3a5a;
    color: #eee;
    border-radius: 5px;
    padding: 0.35rem 0.6rem;
    font-size: 0.95rem;
  }

  .action-btn {
    padding: 0.8rem;
    font-size: 1.05rem;
    background: #4a6fa5;
    color: #fff;
    border: none;
    border-radius: 7px;
    margin-top: 0.5rem;
  }
  .action-btn:disabled { opacity: 0.5; cursor: default; }

  .cancel-btn {
    padding: 0.5rem;
    font-size: 0.9rem;
    background: #3a2a2a;
    color: #e07070;
    border: 1px solid #5a3a3a;
    border-radius: 7px;
  }

  .queue-status {
    display: flex;
    align-items: center;
    gap: 0.7rem;
    color: #ccc;
    font-size: 0.9rem;
    flex-wrap: wrap;
  }
  .queue-count { color: #7b8cde; font-size: 0.85rem; }

  .spinner {
    width: 18px;
    height: 18px;
    border: 2px solid #444;
    border-top-color: #7b8cde;
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
    flex-shrink: 0;
  }
  @keyframes spin { to { transform: rotate(360deg); } }

  .error { color: #e07070; font-size: 0.85rem; margin: 0; }

  .login-prompt {
    color: #888;
    font-size: 0.9rem;
    margin: 0.25rem 0;
  }
  .link-btn {
    background: none;
    border: none;
    color: #7b8cde;
    text-decoration: underline;
    font-size: inherit;
    padding: 0;
    cursor: pointer;
  }

  /* ── Ongoing games ── */
  .ongoing-section {
    width: 100%;
    max-width: 660px;
    margin-bottom: 1.25rem;
  }
  .ongoing-label {
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.07em;
    color: #666;
    margin-bottom: 0.4rem;
  }
  .ongoing-list {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }
  .ongoing-card {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.75rem;
    width: 100%;
    padding: 0.65rem 1rem;
    background: #22223a;
    border: 1px solid #2a2a4a;
    border-radius: 7px;
    color: #eee;
    font-size: 0.92rem;
    text-align: left;
  }
  .ongoing-card:hover { background: #2a2a4a; }
  .ongoing-opp { font-weight: 600; }
  .ongoing-right {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    flex-shrink: 0;
  }
  .ongoing-tc { color: #888; font-size: 0.85rem; }
  .your-turn {
    font-size: 0.78rem;
    color: #7cfc00;
    background: rgba(124, 252, 0, 0.1);
    border: 1px solid rgba(124, 252, 0, 0.3);
    border-radius: 4px;
    padding: 0.1rem 0.45rem;
  }
</style>
