<script>
  import { onMount } from 'svelte';
  import { authState } from './lib/auth.svelte.js';
  import { navigate } from './lib/navigate.js';
  import { getLibrary, getLibraryGame, deleteLibraryGame } from './lib/api.js';
  import { pendingLibraryOpen, RESULT_SHORT } from './lib/library.js';

  let entries = $state(null);   // null = loading
  let error   = $state('');
  let opening = $state(null);   // id of the entry being opened

  const isLoggedIn = $derived(!!authState.token);

  async function load() {
    error = '';
    try { entries = await getLibrary(); }
    catch (e) { error = e.message; entries = []; }
  }

  onMount(() => { if (isLoggedIn) load(); });

  async function open(entry) {
    if (opening) return;
    opening = entry.id;
    try {
      pendingLibraryOpen.entry = await getLibraryGame(entry.id);
      navigate(`/analyze/${entry.shareId}`);
    } catch (e) {
      error = `Could not open "${entry.title}": ${e.message}`;
    } finally {
      opening = null;
    }
  }

  async function remove(entry, e) {
    e.stopPropagation();
    if (!confirm(`Remove "${entry.title}" from your library?`)) return;
    try {
      await deleteLibraryGame(entry.id);
      entries = entries.filter(x => x.id !== entry.id);
    } catch (err) {
      error = `Could not remove "${entry.title}": ${err.message}`;
    }
  }

  // ── Sorting (client-side: the whole library is loaded) ──
  let sortCol = $state('updatedAt');
  let sortDir = $state('desc');   // 'asc' | 'desc'

  const SORT_KEYS = {
    title:     e => e.title.toLowerCase(),
    result:    e => RESULT_SHORT[e.result] ?? '',
    moves:     e => e.plyCount,
    board:     e => e.mapSize,
    updatedAt: e => e.updatedAt,
  };

  function sortBy(col) {
    if (sortCol === col) sortDir = sortDir === 'asc' ? 'desc' : 'asc';
    else { sortCol = col; sortDir = col === 'updatedAt' ? 'desc' : 'asc'; }
  }
  function sortArrow(col) {
    if (sortCol !== col) return '';
    return sortDir === 'asc' ? ' ▲' : ' ▼';
  }

  const sorted = $derived.by(() => {
    if (!entries) return [];
    const key = SORT_KEYS[sortCol];
    const sign = sortDir === 'asc' ? 1 : -1;
    return [...entries].sort((a, b) => {
      const ka = key(a), kb = key(b);
      return (ka < kb ? -1 : ka > kb ? 1 : 0) * sign;
    });
  });

  function formatDate(iso) {
    const d = new Date(iso.endsWith('Z') ? iso : iso + 'Z');
    return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
  }
</script>

<main>
  {#if !isLoggedIn}
    <p class="muted">
      <button class="link" onclick={() => navigate('/login')}>Sign in</button> to save games to your library.
    </p>
  {:else if entries === null}
    <p class="muted">Loading…</p>
  {:else}
    {#if error}<p class="error">{error}</p>{/if}
    {#if entries.length === 0}
      <p class="muted">
        No games yet. Use "Add to library" after a game, or "Save to library" on the analysis board.
      </p>
    {:else}
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th onclick={() => sortBy('title')}>Title{sortArrow('title')}</th>
              <th onclick={() => sortBy('result')}>Result{sortArrow('result')}</th>
              <th onclick={() => sortBy('moves')}>Moves{sortArrow('moves')}</th>
              <th class="col-board" onclick={() => sortBy('board')}>Board{sortArrow('board')}</th>
              <th class="col-date" onclick={() => sortBy('updatedAt')}>Saved{sortArrow('updatedAt')}</th>
              <th class="no-sort"></th>
            </tr>
          </thead>
          <tbody>
            {#each sorted as entry (entry.id)}
              <tr class="game-row" class:opening={opening === entry.id} onclick={() => open(entry)}
                  title="Open on the analysis board">
                <td class="col-title">{entry.title}</td>
                <td>{RESULT_SHORT[entry.result] ?? '–'}</td>
                <td>{Math.ceil(entry.plyCount / 2)}</td>
                <td class="col-board">{entry.mapSize}×{entry.mapSize}</td>
                <td class="col-date">{formatDate(entry.updatedAt)}</td>
                <td class="col-remove">
                  <button class="remove-btn" onclick={(e) => remove(entry, e)} title="Remove from library"
                          aria-label="Remove from library">✕</button>
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    {/if}
  {/if}
</main>

<style>
  main {
    display: flex;
    flex-direction: column;
    flex: 1;
    padding: 1.5rem 1rem 3rem;
    gap: 1rem;
    width: 100%;
    max-width: 860px;
    margin: 0 auto;
    box-sizing: border-box;
  }

  .muted { color: var(--faint); }
  .error { color: #e07070; }
  .link {
    background: none; border: none; padding: 0; font: inherit;
    color: var(--accent); cursor: pointer; text-decoration: underline;
  }

  .table-wrap { width: 100%; overflow-x: auto; }
  table { width: 100%; border-collapse: collapse; font-size: 0.88rem; }
  thead th {
    text-align: left;
    padding: 0.5rem 0.75rem;
    color: var(--faint);
    font-weight: 600;
    font-size: 0.8rem;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    border-bottom: 1px solid var(--line);
    white-space: nowrap;
    cursor: pointer;
    user-select: none;
  }
  thead th:hover { color: var(--fg-2); }
  thead th.no-sort { cursor: default; }
  tbody td {
    padding: 0.55rem 0.75rem;
    border-bottom: 1px solid var(--line);
    vertical-align: middle;
    white-space: nowrap;
  }
  .col-title { white-space: normal; min-width: 10rem; }
  /* td only: the header cells share these classes but keep the header style */
  td.col-date   { color: var(--muted); font-size: 0.82rem; }
  .col-remove { width: 1%; text-align: right; }

  .game-row { cursor: pointer; transition: background 0.1s; }
  .game-row:hover { background: var(--surface); }
  .game-row.opening { opacity: 0.6; }

  .remove-btn {
    background: none; border: none; cursor: pointer;
    color: var(--faint); font-size: 0.95rem; padding: 0.2rem 0.4rem; border-radius: 4px;
  }
  .remove-btn:hover { color: #e07070; background: var(--line); }

  @media (max-width: 600px) {
    .col-board, .col-date { display: none; }
  }
</style>
