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

  function sortBy(col) {
    if (sortCol === col) sortDir = sortDir === 'asc' ? 'desc' : 'asc';
    else { sortCol = col; sortDir = col === 'endedAt' ? 'desc' : 'asc'; }
    fetchGames(0);
  }

  function sortArrow(col) {
    if (sortCol !== col) return '';
    return sortDir === 'asc' ? ' ▲' : ' ▼';
  }

  async function fetchGames(si) {
    startIndex = si;
    loading = true;
    error = '';
    try {
      const data = await getProfile(username, si, sortCol, sortDir, activeTab);
      if (si === 0) {
        profile = data;
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
    const asc = [...profile.games].sort((a, b) => new Date(a.endedAt) - new Date(b.endedAt));
    const result = {};
    for (const cat of ['bullet', 'blitz', 'rapid', 'slow']) {
      const catGames = asc.filter(g => g.category === cat && g.eloDelta != null);
      const currentElo = profile.stats[cat].elo;
      if (catGames.length === 0) {
        // No games yet — draw a flat line at current rating so the graph still renders
        result[cat] = [currentElo, currentElo];
        continue;
      }
      const pts = [catGames[0].myEloAtGame];
      for (const g of catGames) pts.push(g.myEloAtGame + g.eloDelta);
      result[cat] = pts;
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

<main>
  {#if loading}
    <p class="muted">Loading…</p>
  {:else if error}
    <p class="error">{error}</p>
  {:else if profile}

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

    <!-- Tabs -->
    <div class="tabs">
      {#each TABS as t}
        <button class="tab" class:active={activeTab === t} onclick={() => { activeTab = t; fetchGames(0); }}>
          {t.charAt(0).toUpperCase() + t.slice(1)}
          {#if t !== 'all'}
            <span class="tab-count">({profile.stats[t].gamesPlayed})</span>
          {/if}
        </button>
      {/each}
    </div>

    <!-- History table -->
    {#if displayGames.length === 0}
      <p class="muted no-games">No games in this category yet.</p>
    {:else}
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th onclick={() => sortBy('endedAt')}>Date{sortArrow('endedAt')}</th>
              <th onclick={() => sortBy('opponent')}>Opponent{sortArrow('opponent')}</th>
              <th onclick={() => sortBy('result')}>Result{sortArrow('result')}</th>
              <th onclick={() => sortBy('eloDelta')}>Rating Δ{sortArrow('eloDelta')}</th>
              <th class="col-tc" onclick={() => sortBy('timeControl')}>Time{sortArrow('timeControl')}</th>
              <th onclick={() => sortBy('moveCount')}>Moves{sortArrow('moveCount')}</th>
              <th onclick={() => sortBy('boardSize')}>Board{sortArrow('boardSize')}</th>
            </tr>
          </thead>
          <tbody>
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
                    <span class="opp-name">{g.opponentName}</span>
                  {/if}
                  {#if g.opponentEloAtGame != null}
                    <span class="opp-elo">({g.opponentEloAtGame})</span>
                  {/if}
                </td>
                <td class="col-result">
                  <span class="result-badge result-{g.result}">{g.resultDetail}</span>
                </td>
                <td class="col-delta" class:delta-pos={g.eloDelta > 0} class:delta-neg={g.eloDelta < 0}>
                  {#if g.isRanked === false}<span class="unranked">unranked</span>{:else}{fmtDelta(g.eloDelta)}{/if}
                </td>
                <td class="col-tc">{tcLabel(g)}</td>
                <td class="col-moves">{g.moveCount}</td>
                <td class="col-board">{g.boardSize}×{g.boardSize}{g.mapMode === 'r' ? ' 🎲' : ''}</td>
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
  .no-games { margin: 1rem 0; }

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

  /* ── Table ── */
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
  .col-delta { font-variant-numeric: tabular-nums; font-weight: 600; color: var(--muted); }
  .delta-pos { color: #7cfc00; }
  .delta-neg { color: #e07070; }

  .col-tc    { color: var(--muted); font-variant-numeric: tabular-nums; }
  .col-moves { color: var(--muted); }
  .col-board { color: var(--muted); }

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
