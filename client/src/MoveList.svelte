<script>
  /**
   * Props:
   *   moves        – string[]         – move notation array (server format)
   *   activeIndex  – number|null      – index of the highlighted move in replayer (-1 = none)
   *   replayMode   – boolean          – show nav buttons
   *   onHoverMove  – ({from?,to}|null) => void
   *   onNavigate   – ('start'|'prev'|'next'|'end') => void
   */
  let {
    moves       = [],
    activeIndex = null,
    replayMode  = false,
    onHoverMove = null,
    onNavigate  = null,
  } = $props();

  // ── Notation parsing ────────────────────────────────────────────────────

  // Notation: file 'a'=x=0, rank '1'=y=0 (1-indexed display, 0-indexed internal).
  function parseMove(notation) {
    // Placement: letter + rank + '=' + piece_code + optional suffix  (e.g. "a1=Q", "c3=p+")
    const place = notation.match(/^([a-z])(\d+)=([QRBNp])[+#]?$/);
    if (place) {
      return {
        to: { x: place[1].charCodeAt(0) - 97, y: parseInt(place[2]) - 1 },
      };
    }
    // Move: from letter+rank ['-'|'x'] to letter+rank + optional suffix  (e.g. "a2-b3+", "c5xd6#")
    const move = notation.match(/^([a-z])(\d+)([-x])([a-z])(\d+)[+#]?$/);
    if (move) {
      return {
        from:    { x: move[1].charCodeAt(0) - 97, y: parseInt(move[2]) - 1 },
        to:      { x: move[4].charCodeAt(0) - 97, y: parseInt(move[5]) - 1 },
        capture: move[3] === 'x',
      };
    }
    return null;
  }

  // ── Scroll active move into view ────────────────────────────────────────

  let listEl = $state(null);

  $effect(() => {
    if (activeIndex == null || !listEl) return;
    const row = listEl.querySelector(`[data-move-idx="${activeIndex}"]`);
    row?.scrollIntoView({ block: 'nearest' });
  });

  // ── Pair moves for display (white / black per row) ───────────────────────

  const pairs = $derived(() => {
    const result = [];
    for (let i = 0; i < moves.length; i += 2) {
      result.push({ num: Math.floor(i / 2) + 1, w: i, b: i + 1 < moves.length ? i + 1 : null });
    }
    return result;
  });
</script>

<!-- ── Replayer controls ────────────────────────────────────────────────── -->
{#if replayMode}
  <div class="replay-bar">
    <button class="nav-btn" title="Start"   onclick={() => onNavigate?.('start')}>⏮</button>
    <button class="nav-btn" title="Back"    onclick={() => onNavigate?.('prev')} >◀</button>
    <button class="nav-btn" title="Forward" onclick={() => onNavigate?.('next')} >▶</button>
    <button class="nav-btn" title="End"     onclick={() => onNavigate?.('end')}>⏭</button>
  </div>
{/if}

<!-- ── Move list ─────────────────────────────────────────────────────────── -->
<div class="move-list" bind:this={listEl}>
  {#if moves.length === 0}
    <span class="empty">No moves yet</span>
  {/if}
  {#each pairs() as { num, w, b }}
    <div class="move-row">
      <span class="move-num">{num}.</span>
      <!-- White move -->
      <button
        class="move-token"
        class:active={activeIndex === w}
        data-move-idx={w}
        onmouseenter={() => onHoverMove?.(parseMove(moves[w]))}
        onmouseleave={() => onHoverMove?.(null)}
        onclick={() => onNavigate?.(w)}
      >{moves[w]}</button>
      <!-- Black move (may be absent on the last row) -->
      {#if b !== null}
        <button
          class="move-token"
          class:active={activeIndex === b}
          data-move-idx={b}
          onmouseenter={() => onHoverMove?.(parseMove(moves[b]))}
          onmouseleave={() => onHoverMove?.(null)}
          onclick={() => onNavigate?.(b)}
        >{moves[b]}</button>
      {/if}
    </div>
  {/each}
</div>

<style>
  .replay-bar {
    display: flex;
    gap: 0.25rem;
    padding: 0.5rem 0.6rem;
    border-bottom: 1px solid #2a2a4a;
    background: #1e1e38;
    flex-shrink: 0;
  }
  .nav-btn {
    flex: 1;
    padding: 0.4rem 0;
    background: #2d2d4a;
    border: 1px solid #3a3a5a;
    color: #ccc;
    border-radius: 4px;
    font-size: 0.9rem;
  }
  .nav-btn:hover { background: #3a3a6a; color: #fff; }

  .move-list {
    flex: 1;
    overflow-y: auto;
    padding: 0.4rem 0;
    font-size: 0.88rem;
    font-family: 'Courier New', monospace;
  }

  .move-row {
    display: flex;
    align-items: center;
    gap: 0.25rem;
    padding: 0 0.4rem;
  }
  .move-row:hover { background: rgba(255,255,255,0.04); }

  .move-num {
    color: #666;
    min-width: 2em;
    text-align: right;
    user-select: none;
    flex-shrink: 0;
  }

  .move-token {
    flex: 1;
    padding: 0.12rem 0.35rem;
    border-radius: 3px;
    cursor: default;
    color: #ccc;
    white-space: nowrap;
    background: none;
    border: none;
    font-family: 'Courier New', monospace;
    font-size: inherit;
    text-align: left;
  }
  .move-token:hover { background: rgba(123, 140, 222, 0.25); color: #fff; }
  .move-token.active { background: rgba(123, 140, 222, 0.4); color: #fff; font-weight: 600; }

  .empty { padding: 0.5rem 0.75rem; color: #555; font-style: italic; }
</style>
