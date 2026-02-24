<script>
  import { onMount } from 'svelte';
  import { navigate } from './lib/navigate.js';
  import Board from './Board.svelte';

  let games   = $state([]);
  let loading = $state(true);
  let error   = $state('');

  onMount(async () => {
    try {
      const res = await fetch('/api/game/live');
      if (!res.ok) throw new Error(await res.text());
      const raw = await res.json();
      games = raw.map(g => ({ ...g, ...parseSeed(g.mapSeed) }));
    } catch (e) {
      error = e.message;
    } finally {
      loading = false;
    }
  });

  // ── Client-side seed parser ──────────────────────────────────────────────
  // Mirrors MapGenerator.GenerateFromSeed + MirrorGeneratedHalf logic.

  function parseSeed(seed) {
    const [mirrorFlag, dims, encoded] = seed.split('_');
    const [W, H] = dims.split('x').map(Number);
    const total  = W * H;

    // Build all squares with checkerboard base types
    const squares = [];
    for (let y = 0; y < H; y++) {
      for (let x = 0; x < W; x++) {
        const id   = y * W + x;
        const type = (y % 2 === 0)
          ? (x % 2 === 0 ? 'Grass' : 'Dirt')
          : (x % 2 === 0 ? 'Dirt'  : 'Grass');
        squares.push({ x, y, id, type, piece: null, highlight: 'None' });
      }
    }

    // Parse the encoded first half
    let sqId = 0;
    for (const ch of encoded) {
      const digit = parseInt(ch, 10);
      if (!isNaN(digit)) {
        sqId += digit;
      } else {
        const sq = squares[sqId];
        if      (ch === 'k') sq.piece = { type: 'WhiteKing', isWhite: true  };
        else if (ch === 't') sq.piece = { type: 'Treasure',  isWhite: false };
        else if (ch === 'm') sq.type  = sq.type === 'Dirt' ? 'DirtMine'  : 'GrassMine';
        else if (ch === 'r') sq.type  = sq.type === 'Dirt' ? 'DirtRocks' : 'GrassRocks';
        else if (ch === 'f') sq.type  = sq.type === 'Dirt' ? 'DirtTrees' : 'GrassTrees';
        sqId++;
      }
    }

    // Mirror first half onto second half (ids 0 … total/2−2)
    for (let id = 0; id < total / 2 - 1; id++) {
      const sq     = squares[id];
      const mirror = squares[total - 1 - id];
      mirror.type  = sq.type;
      if      (sq.piece?.type === 'Treasure')  mirror.piece = { type: 'Treasure',  isWhite: false };
      else if (sq.piece?.type === 'WhiteKing') mirror.piece = { type: 'BlackKing', isWhite: false };
    }

    return { squares, mapSize: W };
  }

  // ── Display helpers ──────────────────────────────────────────────────────

  function timeLabel(g) {
    if (!g.timeControlEnabled) return 'No timer';
    const inc = g.timeIncrementSeconds > 0 ? `+${g.timeIncrementSeconds}` : '+0';
    return `${g.startTimeMinutes}${inc}`;
  }

  function playerLabel(name, elo) {
    return elo != null ? `${name} (${elo})` : name;
  }
</script>

<main>
  {#if loading}
    <p class="muted">Loading…</p>
  {:else if error}
    <p class="error">{error}</p>
  {:else if games.length === 0}
    <p class="muted">No games in progress right now.</p>
  {:else}
    <div class="grid">
      {#each games as g}
        <button class="game-card" onclick={() => navigate(`/game/${g.id}`)}>
          <!-- White player (top) + time control (top-right) -->
          <div class="player-row">
            <span class="player-name">{playerLabel(g.whiteName, g.whiteElo)}</span>
            <span class="time-label">{timeLabel(g)}</span>
          </div>

          <!-- Board showing starting position, non-interactive -->
          <div class="board-wrap">
            <Board squares={g.squares} mapSize={g.mapSize} isWhite={true} showCoords={false} />
          </div>

          <!-- Black player (bottom) -->
          <div class="player-row">
            <span class="player-name">{playerLabel(g.blackName, g.blackElo)}</span>
          </div>
        </button>
      {/each}
    </div>
  {/if}
</main>

<style>
  main {
    display: flex;
    flex-direction: column;
    align-items: center;
    flex: 1;
    padding: 1.5rem 1rem;
  }

  .muted { color: #666; }
  .error { color: #e07070; }

  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
    gap: 1.25rem;
    width: 100%;
    max-width: 1280px;
  }

  .game-card {
    display: flex;
    flex-direction: column;
    gap: 0.45rem;
    background: #22223a;
    border: 1px solid #2a2a4a;
    border-radius: 8px;
    padding: 0.75rem;
    text-align: left;
    color: inherit;
    cursor: pointer;
  }
  .game-card:hover { background: #2a2a4a; border-color: #3a3a6a; }

  .player-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
    min-height: 1.25rem;
  }

  .player-name {
    font-size: 0.88rem;
    color: #ddd;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .time-label {
    font-size: 0.82rem;
    color: #7b8cde;
    white-space: nowrap;
    flex-shrink: 0;
  }

  /* Block canvas pointer events so the card button handles all clicks */
  .board-wrap {
    pointer-events: none;
    border-radius: 4px;
    overflow: hidden;
  }
</style>
