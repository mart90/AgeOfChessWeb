<script>
  import { onMount } from 'svelte';
  import { navigate } from './lib/navigate.js';
  import { getProfile } from './lib/api.js';

  const { username } = $props();

  let profile    = $state(null);
  let loading    = $state(true);
  let error      = $state('');
  let totalGames = $state(0);
  let startIndex = $state(0);

  const PAGE_SIZE = 50;

  // ── Tabs & sorting ───────────────────────────────────────────────────────

  let activeTab = $state('all');   // 'all' | 'blitz' | 'rapid' | 'slow'
  let sortCol   = $state('endedAt');
  let sortDir   = $state('desc');  // 'asc' | 'desc'

  const TABS = ['all', 'bullet', 'blitz', 'rapid', 'slow'];

  const displayGames = $derived(profile?.games ?? []);

  // ── Table options (the two switches are remembered per browser) ──────────

  const TABLE_PREFS_KEY = 'profile_table';
  let tablePrefs = {};
  try { tablePrefs = JSON.parse(localStorage.getItem(TABLE_PREFS_KEY) ?? '{}'); } catch { /* defaults */ }
  let showUnranked   = $state(tablePrefs.showUnranked ?? false);
  let showHandicap   = $state(tablePrefs.showHandicap ?? false);
  let opponentFilter = $state('');
  $effect(() => {
    try { localStorage.setItem(TABLE_PREFS_KEY, JSON.stringify({ showUnranked, showHandicap })); } catch { /* not remembered */ }
  });

  // The filter applies 300 ms after typing stops
  const activeFilter = $derived(opponentFilter.trim());
  let appliedFilter = '';
  let filterTimer;
  function onFilterInput() {
    clearTimeout(filterTimer);
    filterTimer = setTimeout(() => { if (activeFilter !== appliedFilter) fetchGames(0); }, 300);
  }

  // The opponent filter lives in the Opponent column header (opened with the loupe); the two
  // switches in a popover at the end of the tab bar.
  let filterOpen  = $state(false);
  let optionsOpen = $state(false);
  const focusOnMount = (el) => el.focus();
  function clearFilter() {
    clearTimeout(filterTimer);
    opponentFilter = '';
    filterOpen = false;
    if (appliedFilter) fetchGames(0);
  }
  function closeOptions(e) {
    if (!e.target.closest('.table-options')) optionsOpen = false;
  }

  function sortBy(col) {
    if (sortCol === col) sortDir = sortDir === 'asc' ? 'desc' : 'asc';
    else { sortCol = col; sortDir = col === 'endedAt' || col === 'handicap' ? 'desc' : 'asc'; }
    fetchGames(0);
  }

  function sortArrow(col) {
    if (sortCol !== col) return '';
    return sortDir === 'asc' ? ' ▲' : ' ▼';
  }

  let fetchSeq = 0;   // only the latest request's answer is shown (filter typing sends several)
  async function fetchGames(si) {
    const seq = ++fetchSeq;
    startIndex = si;
    loading = true;
    error = '';
    try {
      const data = await getProfile(username, si, sortCol, sortDir, activeTab,
        { unranked: showUnranked, opponent: activeFilter, history: !profile });
      if (seq !== fetchSeq) return;
      appliedFilter = activeFilter;
      if (si === 0) {
        profile = { ...data, ratingHistory: data.ratingHistory ?? profile?.ratingHistory };   // history: first load only
      } else {
        profile = { ...profile, games: data.games };
      }
      totalGames = data.totalGames;
    } catch (e) {
      error = e.message ?? String(e);
    } finally {
      loading = false;
    }
  }

  // ── Rating graph ─────────────────────────────────────────────────────────

  const GW = 600, GH = 200;
  const PAD = { top: 12, right: 16, bottom: 28, left: 46 };
  const plotW = GW - PAD.left - PAD.right;
  const plotH = GH - PAD.top  - PAD.bottom;

  const CAT_COLORS = { bullet: '#ff6b9d', blitz: '#7b8cde', rapid: '#6ec97a', slow: '#f5c518' };

  const eloSeries = $derived.by(() => {
    if (!profile) return { bullet: [], blitz: [], rapid: [], slow: [] };
    // Whole history from the server (not just the games listed below)
    const result = {};
    for (const cat of ['bullet', 'blitz', 'rapid', 'slow']) {
      const pts = profile.ratingHistory?.[cat] ?? [];
      // No rated games yet — draw a flat line at current rating so the graph still renders
      const currentElo = profile.stats[cat].elo;
      result[cat] = pts.length ? pts : [currentElo, currentElo];
    }
    return result;
  });

  const graphData = $derived.by(() => {
    const allPts = Object.values(eloSeries).flat();
    if (allPts.length === 0) return null;

    const rawMin = Math.min(...allPts);
    const rawMax = Math.max(...allPts);
    const pad    = Math.max(30, Math.round((rawMax - rawMin) * 0.1));
    const yMin   = Math.floor((rawMin - pad) / 50) * 50;
    const yMax   = Math.ceil ((rawMax + pad) / 50) * 50;
    const maxLen = Math.max(...Object.values(eloSeries).map(s => s.length));

    function xOf(i)   { return PAD.left + (maxLen <= 1 ? plotW / 2 : (i / (maxLen - 1)) * plotW); }
    function yOf(elo) { return PAD.top  + plotH - ((elo - yMin) / (yMax - yMin)) * plotH; }

    const step = (yMax - yMin) > 400 ? 200 : 100;
    const gridY = [];
    for (let v = Math.ceil(yMin / step) * step; v <= yMax; v += step) gridY.push(v);

    const lines = {};
    for (const [cat, pts] of Object.entries(eloSeries)) {
      if (pts.length < 2) { lines[cat] = ''; continue; }
      lines[cat] = pts.map((elo, i) => `${xOf(i).toFixed(1)},${yOf(elo).toFixed(1)}`).join(' ');
    }

    return { lines, gridY, yOf };
  });

  // ── Helpers ──────────────────────────────────────────────────────────────

  function fmtDate(iso) {
    if (!iso) return '—';
    const d = new Date(iso);
    return d.toLocaleString(undefined, {
      month: 'short', day: 'numeric', year: 'numeric',
      hour: '2-digit', minute: '2-digit', hour12: false
    });
  }

  function fmtDelta(d) {
    if (d == null) return '—';
    return d >= 0 ? `+${d}` : `${d}`;
  }

  function tcLabel(g) {
    if (!g.timeControlEnabled) return '∞';
    const inc = g.timeIncrementSeconds > 0 ? `+${g.timeIncrementSeconds}` : '';
    return `${g.startTimeMinutes}${inc}`;
  }

  onMount(() => fetchGames(0));
</script>

<svelte:window onclick={closeOptions} />

<main>
  {#if !profile}
    <!-- First load only: later fetches (tabs, sorting, filters) keep the page and dim the table -->
    {#if error}<p class="error">{error}</p>{:else}<p class="muted">Loading…</p>{/if}
  {:else}

    <!-- Header -->
    <div class="profile-header">
      <h1 class="display-name">{profile.displayName}</h1>
      {#if profile.displayName !== profile.username}
        <span class="username-sub">@{profile.username}</span>
      {/if}
    </div>

    <!-- Stats cards -->
    <div class="stats-row">
      {#each [['bullet', 'Bullet', '🔥'], ['blitz', 'Blitz', '⚡'], ['rapid', 'Rapid', '🕐'], ['slow', 'Slow', '☕']] as [cat, label, icon]}
        {@const s = profile.stats[cat]}
        <div class="stat-card">
          <div class="stat-label">{icon} {label}</div>
          <div class="stat-elo">{s.elo}</div>
          <div class="stat-games">{s.gamesPlayed} {s.gamesPlayed === 1 ? 'game' : 'games'}</div>
        </div>
      {/each}
    </div>

    <!-- Rating graph -->
    {#if graphData}
      <div class="graph-wrap">
        <div class="graph-legend">
          {#each Object.entries(CAT_COLORS) as [cat, color]}
            {#if eloSeries[cat].length >= 2}
              <span class="legend-item">
                <svg width="20" height="3"><line x1="0" y1="1.5" x2="20" y2="1.5" stroke={color} stroke-width="2.5"/></svg>
                {cat.charAt(0).toUpperCase() + cat.slice(1)}
              </span>
            {/if}
          {/each}
        </div>
        <svg viewBox="0 0 {GW} {GH}" width="100%" preserveAspectRatio="xMidYMid meet" class="graph-svg">
          {#each graphData.gridY as v}
            {@const y = graphData.yOf(v).toFixed(1)}
            <line x1={PAD.left} y1={y} x2={GW - PAD.right} y2={y} class="gridline" />
            <text x={PAD.left - 6} y={y} class="grid-label" dominant-baseline="middle">{v}</text>
          {/each}
          {#each Object.entries(graphData.lines) as [cat, pts]}
            {#if pts}
              <polyline points={pts} fill="none" stroke={CAT_COLORS[cat]} stroke-width="2.2"
                        stroke-linejoin="round" stroke-linecap="round" />
            {/if}
          {/each}
          <line x1={PAD.left} y1={GH - PAD.bottom} x2={GW - PAD.right} y2={GH - PAD.bottom} class="axis" />
        </svg>
      </div>
    {/if}

    <!-- Tabs, with the table options at the end -->
    <div class="tabs">
      {#each TABS as t}
        <button class="tab" class:active={activeTab === t} onclick={() => { activeTab = t; fetchGames(0); }}>
          {t.charAt(0).toUpperCase() + t.slice(1)}
          {#if profile.gameCounts}
            <span class="tab-count">({profile.gameCounts[t]})</span>
          {/if}
        </button>
      {/each}
      <div class="table-options">
        <!-- The dot: an option differs from its default -->
        <button class="options-btn" class:changed={showUnranked || showHandicap} title="Table options"
                aria-label="Table options" aria-expanded={optionsOpen} onclick={() => (optionsOpen = !optionsOpen)}>
          <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h9M17 7h3M4 17h3M11 17h9"/><circle cx="15" cy="7" r="2"/><circle cx="9" cy="17" r="2"/></svg>
        </button>
        {#if optionsOpen}
          <div class="options-pop">
            <label class="tool-toggle">
              Show unranked games
              <input type="checkbox" bind:checked={showUnranked} onchange={() => fetchGames(0)} />
            </label>
            <label class="tool-toggle">
              Show handicap column
              <input type="checkbox" bind:checked={showHandicap} />
            </label>
          </div>
        {/if}
      </div>
    </div>

    {#if error}<p class="error">{error}</p>{/if}

    <!-- History table (the header stays when nothing matches, so the filter can be changed) -->
    <div class="table-wrap" class:refreshing={loading}>
        <table>
          <thead>
            <tr>
              <th onclick={() => sortBy('endedAt')}>Date{sortArrow('endedAt')}</th>
              <th class="th-opp" class:filtering={filterOpen} onclick={() => { if (!filterOpen) sortBy('opponent'); }}>
                {#if filterOpen}
                  <span class="th-filter">
                    <input class="opp-filter" type="text" placeholder="Opponent…" aria-label="Filter by opponent"
                           use:focusOnMount bind:value={opponentFilter} oninput={onFilterInput}
                           onkeydown={(e) => { if (e.key === 'Escape') clearFilter(); }}
                           onblur={() => { if (!opponentFilter.trim()) clearFilter(); }} />
                    <button class="th-icon-btn" title="Clear filter" aria-label="Clear filter"
                            onmousedown={(e) => e.preventDefault()} onclick={clearFilter}>✕</button>
                  </span>
                {:else}
                  Opponent{sortArrow('opponent')}
                  <button class="th-icon-btn" title="Filter by opponent" aria-label="Filter by opponent"
                          onclick={(e) => { e.stopPropagation(); filterOpen = true; }}><svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><circle cx="10.5" cy="10.5" r="6"/><path d="m15 15 5.5 5.5"/></svg></button>
                {/if}
              </th>
              <th onclick={() => sortBy('result')}>Result{sortArrow('result')}</th>
              <th onclick={() => sortBy('rating')}>Rating{sortArrow('rating')}</th>
              <th class="col-tc" onclick={() => sortBy('timeControl')}>Time{sortArrow('timeControl')}</th>
              <th onclick={() => sortBy('moveCount')}>Moves{sortArrow('moveCount')}</th>
              <th class="col-board" onclick={() => sortBy('boardSize')}>Board{sortArrow('boardSize')}</th>
              {#if showHandicap}
                <th class="col-handicap" onclick={() => sortBy('handicap')}>Handicap{sortArrow('handicap')}</th>
              {/if}
            </tr>
          </thead>
          <tbody>
            {#if displayGames.length === 0}
              <tr><td class="no-games" colspan="8">
                {appliedFilter || !showUnranked ? 'No games match.' : 'No games in this category yet.'}
              </td></tr>
            {/if}
            {#each displayGames as g}
              <tr class="game-row" class:win={g.result === 'win'} class:loss={g.result === 'loss'}
                  onclick={() => navigate(`/game/${g.gameId}`)}>
                <td class="col-date">{fmtDate(g.endedAt)}</td>
                <td class="col-opp">
                  {#if g.opponentUsername}
                    <button class="opp-link" onclick={(e) => { e.stopPropagation(); navigate(`/profile/${g.opponentUsername}`); }}>
                      {g.opponentName}
                    </button>
                  {:else}
                    <span class="opp-name" class:computer={g.vsComputer}>{g.opponentName}</span>
                  {/if}
                  {#if g.opponentEloAtGame != null}
                    <span class="opp-elo">({g.opponentEloAtGame})</span>
                  {/if}
                </td>
                <td class="col-result">
                  <span class="result-badge result-{g.result}">{g.resultDetail}</span>
                </td>
                <td class="col-rating">
                  {#if g.isRanked === false}<span class="unranked">unranked</span>
                  {:else if g.myEloAtGame == null || g.eloDelta == null}—
                  {:else}{g.myEloAtGame + g.eloDelta}
                    <span class="delta" class:delta-pos={g.eloDelta > 0} class:delta-neg={g.eloDelta < 0}>{fmtDelta(g.eloDelta)}</span>
                  {/if}
                </td>
                <td class="col-tc">{tcLabel(g)}</td>
                <td class="col-moves">{Math.ceil(g.moveCount / 2)}</td>
                <td class="col-board">{g.boardSize}×{g.boardSize}{g.mapMode === 'r' ? ' 🎲' : ''}</td>
                {#if showHandicap}<td class="col-handicap">{g.handicap ?? ''}</td>{/if}
              </tr>
            {/each}
          </tbody>
        </table>
    </div>
    {#if totalGames > PAGE_SIZE}
        <div class="pagination">
          <button class="page-btn" disabled={startIndex === 0}
                  onclick={() => fetchGames(startIndex - PAGE_SIZE)}>← Prev</button>
          <span class="page-info">
            {startIndex + 1}–{Math.min(startIndex + PAGE_SIZE, totalGames)} of {totalGames}
          </span>
          <button class="page-btn" disabled={startIndex + PAGE_SIZE >= totalGames}
                  onclick={() => fetchGames(startIndex + PAGE_SIZE)}>Next →</button>
        </div>
    {/if}

  {/if}
</main>

<style>
  .unranked { color: var(--faint); font-size: 0.8em; font-style: italic; }
  main {
    display: flex;
    flex-direction: column;
    align-items: center;
    flex: 1;
    padding: 1.5rem 1rem 3rem;
    gap: 1.25rem;
    width: 100%;
    max-width: 860px;
    margin: 0 auto;
  }

  .muted  { color: var(--faint); }
  .error  { color: #e07070; }
  .no-games { color: var(--muted); padding: 1rem 0.75rem; }

  /* ── Header ── */
  .profile-header {
    width: 100%;
    display: flex;
    align-items: baseline;
    gap: 0.75rem;
  }
  .display-name {
    margin: 0;
    font-size: 1.7rem;
    font-weight: 700;
    color: var(--fg);
  }
  .username-sub { color: var(--faint); font-size: 0.95rem; }

  /* ── Stats cards ── */
  .stats-row {
    display: flex;
    gap: 0.85rem;
    width: 100%;
  }
  .stat-card {
    flex: 1;
    background: var(--surface);
    border: 1px solid var(--line);
    border-radius: 8px;
    padding: 1rem 1.25rem;
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
  }
  .stat-label { font-size: 0.8rem; text-transform: uppercase; letter-spacing: 0.06em; color: var(--faint); }
  .stat-elo   { font-size: 2rem; font-weight: 700; color: var(--fg); line-height: 1.1; }
  .stat-games { font-size: 0.82rem; color: var(--faint); }

  /* ── Graph ── */
  .graph-wrap {
    width: 100%;
    background: var(--surface);
    border: 1px solid var(--line);
    border-radius: 8px;
    padding: 0.75rem 0.5rem 0.5rem;
  }
  .graph-legend {
    display: flex;
    gap: 1rem;
    padding: 0 0.75rem 0.5rem;
    font-size: 0.8rem;
    color: var(--muted);
  }
  .legend-item { display: flex; align-items: center; gap: 0.4rem; }
  .graph-svg { display: block; }

  .gridline { stroke: var(--line); stroke-width: 1; }
  .axis     { stroke: var(--line-strong); stroke-width: 1; }
  .grid-label { fill: var(--faint); font-size: 12px; text-anchor: end; }

  /* ── Tabs ── */
  .tabs {
    display: flex;
    border-bottom: 2px solid var(--line);
    width: 100%;
  }
  .tab {
    flex: 1;
    padding: 0.65rem 0.5rem;
    background: none;
    border: none;
    color: var(--muted);
    font-size: 0.9rem;
    border-bottom: 2px solid transparent;
    margin-bottom: -2px;
  }
  .tab.active { color: var(--fg); border-bottom-color: var(--accent); }
  .tab:hover:not(.active) { color: var(--fg-2); }
  .tab-count { font-size: 0.8rem; color: var(--faint); margin-left: 0.2rem; }

  /* ── Table options: popover at the end of the tab bar ── */
  .icon {
    width: 1.1em; height: 1.1em; flex-shrink: 0;
    fill: none; stroke: currentColor; stroke-width: 2;
    stroke-linecap: round; stroke-linejoin: round;
  }
  .table-options { position: relative; display: flex; align-items: center; }
  .options-btn {
    position: relative;
    display: flex; align-items: center;
    background: none; border: none; border-radius: 4px;
    color: var(--muted);
    font-size: 1.05rem;
    padding: 0.35rem 0.45rem;
    cursor: pointer;
  }
  .options-btn:hover, .options-btn[aria-expanded="true"] { background: var(--line); color: var(--fg); }
  .options-btn.changed::after {
    content: ''; position: absolute; top: 0.25rem; right: 0.25rem;
    width: 6px; height: 6px; border-radius: 50%; background: var(--accent);
  }
  .options-pop {
    position: absolute; right: 0; top: calc(100% + 6px); z-index: 50;
    display: flex; flex-direction: column; gap: 0.7rem;
    padding: 0.8rem 0.9rem;
    background: var(--surface);
    border: 1px solid var(--line-strong);
    border-radius: 6px;
    box-shadow: 0 4px 16px rgba(0,0,0,0.5);
    white-space: nowrap;
  }
  .tool-toggle {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1.2rem;
    font-size: 0.88rem;
    color: var(--fg-2);
    cursor: pointer;
  }

  /* ── Opponent filter, in the column header ── */
  .th-icon-btn {
    display: inline-flex; align-items: center; vertical-align: -0.15em;
    background: none; border: none; padding: 0 0.15rem; margin-left: 0.25rem;
    color: var(--faint); font-size: 0.95rem; cursor: pointer;
  }
  .th-icon-btn:hover { color: var(--fg); }
  .th-opp.filtering { cursor: default; padding-top: 0.3rem; padding-bottom: 0.3rem; }
  .th-filter { display: inline-flex; align-items: center; }
  .opp-filter {
    width: 8.5rem;
    background: var(--surface-2);
    border: 1px solid var(--line);
    border-radius: 4px;
    color: var(--fg);
    padding: 0.2rem 0.45rem;
    font: inherit;
    font-size: 0.85rem;
    text-transform: none;
    letter-spacing: normal;
    font-weight: 400;
  }
  .opp-filter:focus { outline: none; border-color: var(--accent); }

  /* ── Table ── */
  .refreshing { opacity: 0.6; transition: opacity 0.15s 0.1s; }
  .table-wrap {
    width: 100%;
    overflow-x: auto;
  }
  table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.88rem;
  }
  thead th {
    text-align: left;
    padding: 0.5rem 0.75rem;
    color: var(--faint);
    font-weight: 600;
    font-size: 0.8rem;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    border-bottom: 1px solid var(--line);
    cursor: pointer;
    white-space: nowrap;
    user-select: none;
  }
  thead th:hover { color: var(--fg-2); }
  tbody td {
    padding: 0.55rem 0.75rem;
    border-bottom: 1px solid var(--line);
    vertical-align: middle;
    white-space: nowrap;
  }

  .game-row {
    cursor: pointer;
    transition: background 0.1s;
  }
  .game-row:hover { background: var(--surface); }
  .game-row.win  { border-left: 3px solid rgba(100,220,80,0.35); }
  .game-row.loss { border-left: 3px solid rgba(220,80,80,0.35); }

  .col-date  { color: var(--muted); font-size: 0.82rem; }
  .col-opp   { font-weight: 600; color: var(--fg); }
  .opp-elo   { font-weight: 400; font-size: 0.8rem; color: var(--faint); margin-left: 0.25rem; }
  .opp-name  { color: var(--fg); }
  .opp-name.computer { font-weight: 400; }
  .opp-link  {
    background: none; border: none; padding: 0;
    color: var(--accent); font-weight: 600; font-size: inherit;
    font-family: inherit; cursor: pointer;
  }
  .opp-link:hover { text-decoration: underline; }

  .col-result { display: flex; align-items: center; gap: 0.5rem; }
  .result-badge {
    font-size: 0.79rem;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    padding: 0.1rem 0.45rem;
    border-radius: 4px;
  }
  .result-win  { color: #7cfc00; background: rgba(124,252,0,0.1);  border: 1px solid rgba(124,252,0,0.25); }
  .result-loss { color: #e07070; background: rgba(220,80,80,0.1);  border: 1px solid rgba(220,80,80,0.25); }
  .result-draw { color: var(--muted);    background: rgba(180,180,180,0.1); border: 1px solid rgba(180,180,180,0.2); }
  .col-rating { font-variant-numeric: tabular-nums; color: var(--fg); }
  .delta { font-weight: 600; color: var(--muted); margin-left: 0.2rem; }
  .delta-pos { color: #7cfc00; }
  .delta-neg { color: #e07070; }

  td.col-tc    { color: var(--muted); font-variant-numeric: tabular-nums; }
  .col-moves { color: var(--muted); }
  td.col-board { color: var(--muted); }
  td.col-handicap { color: var(--muted); font-variant-numeric: tabular-nums; }

  /* ── Pagination ── */
  .pagination {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 1rem;
    padding: 0.75rem 0 0.25rem;
    width: 100%;
  }
  .page-btn {
    background: var(--surface);
    border: 1px solid var(--line);
    border-radius: 6px;
    color: var(--muted);
    padding: 0.35rem 0.85rem;
    font-size: 0.85rem;
    cursor: pointer;
  }
  .page-btn:hover:not(:disabled) { background: var(--line); color: var(--fg); }
  .page-btn:disabled { opacity: 0.35; cursor: default; }
  .page-info { font-size: 0.82rem; color: var(--faint); }

  @media (max-width: 600px) {
    .stats-row { flex-direction: column; }
    .stat-elo  { font-size: 1.6rem; }
    .col-tc    { display: none; }
    .col-board { display: none; }
  }
</style>
