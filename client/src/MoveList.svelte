<script>
  /**
   * Props:
   *   Tree mode (game-over / analysis):
   *     rootNode    – MoveNode root; if provided, tree mode is used
   *     activeNode  – currently highlighted MoveNode
   *   Flat mode (live games):
   *     moves       – string[]   – move notation array
   *     activeIndex – number|null
   *   Common:
   *     replayMode  – boolean    – show nav buttons
   *     onHoverMove – ({from?,to}|null) => void
   *     onNavigate  – tree mode: (MoveNode) => void
   *                   flat mode: ('start'|'prev'|'next'|'end'|number) => void
   *     belowNav    – optional snippet rendered between the nav buttons and the list
   */
  let {
    rootNode     = null,
    activeNode   = null,
    treeRevision = 0,   // increment from parent after tree mutations to force re-derive
    moves        = [],
    activeIndex  = null,
    replayMode   = false,
    onHoverMove  = null,
    onNavigate   = null,
    belowNav     = null,
  } = $props();

  // ── Notation parsing (flat mode + hover) ─────────────────────────────────

  function parseMove(notation) {
    const place = notation?.match(/^([a-z])(\d+)=([QRBNp])[+#]?$/);
    if (place) return { to: { x: place[1].charCodeAt(0) - 97, y: parseInt(place[2]) - 1 } };
    const move = notation?.match(/^([a-z])(\d+)([-x])([a-z])(\d+)[+#]?$/);
    if (move) return {
      from:    { x: move[1].charCodeAt(0) - 97, y: parseInt(move[2]) - 1 },
      to:      { x: move[4].charCodeAt(0) - 97, y: parseInt(move[5]) - 1 },
      capture: move[3] === 'x',
    };
    return null;
  }

  // ── Flat mode: pair moves for display ────────────────────────────────────

  const pairs = $derived.by(() => {
    const result = [];
    for (let i = 0; i < moves.length; i += 2) {
      result.push({ num: Math.floor(i / 2) + 1, w: i, b: i + 1 < moves.length ? i + 1 : null });
    }
    return result;
  });

  // ── Tree mode: mainline as flat array ────────────────────────────────────

  const mainlineNodes = $derived.by(() => {
    void treeRevision; // re-derive when the tree is mutated (new variations added)
    if (!rootNode) return [];
    const result = [rootNode];
    let cur = rootNode;
    while (cur.children[0]) { cur = cur.children[0]; result.push(cur); }
    return result;
  });

  /** Follow first-child chain from startNode to get a variation's main line. */
  function getVarLine(startNode) {
    const line = [];
    let cur = startNode;
    while (cur) { line.push(cur); cur = cur.children[0]; }
    return line;
  }

  /** Group a node array into {moveNum, white, black} pairs for grid-row rendering. */
  function pairsFromNodes(nodes) {
    const result = [];
    const ns = nodes.filter(n => n.san);
    for (let i = 0; i < ns.length; ) {
      const node = ns[i];
      if (node.isWhiteMove) {
        const next = ns[i + 1];
        if (next && !next.isWhiteMove && next.moveNum === node.moveNum) {
          result.push({ moveNum: node.moveNum, white: node, black: next });
          i += 2;
        } else {
          result.push({ moveNum: node.moveNum, white: node, black: null });
          i++;
        }
      } else {
        result.push({ moveNum: node.moveNum, white: null, black: node });
        i++;
      }
    }
    return result;
  }

  const mainlinePairs = $derived.by(() => {
    void treeRevision;
    if (!rootNode) return [];
    return pairsFromNodes(mainlineNodes);
  });

  // ── Scroll active move into view ──────────────────────────────────────────

  let listEl = $state(null);

  // Runs after every new move / navigation, so the list follows the game as it grows
  $effect(() => {
    void activeNode; void activeIndex; void treeRevision; void moves.length;
    if (!listEl) return;
    let el = null;
    if (rootNode) {
      el = listEl.querySelector('.move-token.active');
    } else if (activeIndex != null) {
      el = listEl.querySelector(`[data-move-idx="${activeIndex}"]`);
    }
    if (!el) return;
    // Positions relative to the list's visible area
    const list = listEl.getBoundingClientRect(), r = el.getBoundingClientRect();
    if (r.top < list.top)            listEl.scrollTop += r.top - list.top;
    else if (r.bottom > list.bottom) listEl.scrollTop += r.bottom - list.bottom;
  });

  // ── Tree mode nav helpers ─────────────────────────────────────────────────

  // The root node holds the starting position (before the first move)
  function treeNavStart() {
    onNavigate?.(rootNode);
  }
  function treeNavEnd() {
    let n = activeNode ?? rootNode;
    while (n?.children[0]) n = n.children[0];
    onNavigate?.(n);
  }
</script>

<!-- ── Replayer controls ─────────────────────────────────────────────────── -->
{#if replayMode}
  <div class="replay-bar">
    {#if rootNode}
      <button class="nav-btn" title="Start"   onclick={treeNavStart}>⏮</button>
      <button class="nav-btn" title="Back"    onclick={() => onNavigate?.(activeNode?.parent ?? activeNode)}>◀</button>
      <button class="nav-btn" title="Forward" onclick={() => onNavigate?.(activeNode?.children[0] ?? activeNode)}>▶</button>
      <button class="nav-btn" title="End"     onclick={treeNavEnd}>⏭</button>
    {:else}
      <button class="nav-btn" title="Start"   onclick={() => onNavigate?.('start')}>⏮</button>
      <button class="nav-btn" title="Back"    onclick={() => onNavigate?.('prev')} >◀</button>
      <button class="nav-btn" title="Forward" onclick={() => onNavigate?.('next')} >▶</button>
      <button class="nav-btn" title="End"     onclick={() => onNavigate?.('end')}>⏭</button>
    {/if}
  </div>
{/if}
{#if belowNav}{@render belowNav()}{/if}

<!-- ── Recursive variation renderer ─────────────────────────────────────── -->
<!--
  Renders a variation line starting from startNode (follows children[0] chain).
  For each node, sub-variations (children[1..]) are rendered recursively.
  Move numbers are shown at the first node, every white move, and any node
  whose predecessor had sub-variations (to re-orient the reader after a branch).
-->
{#snippet renderVarLine(startNode)}
  {#each pairsFromNodes(getVarLine(startNode)) as pair}
    <div class="tree-row">
      <span class="num-n">{pair.moveNum}.</span>
      {#if pair.white}
        <button
          class="move-token variation-move"
          class:active={pair.white === activeNode}
          onmouseenter={() => onHoverMove?.(parseMove(pair.white.san))}
          onmouseleave={() => onHoverMove?.(null)}
          onclick={() => onNavigate?.(pair.white)}
        >{pair.white.san}</button>
      {:else}
        <span class="move-dots">...</span>
      {/if}
      {#if pair.black}
        <button
          class="move-token variation-move"
          class:active={pair.black === activeNode}
          onmouseenter={() => onHoverMove?.(parseMove(pair.black.san))}
          onmouseleave={() => onHoverMove?.(null)}
          onclick={() => onNavigate?.(pair.black)}
        >{pair.black.san}</button>
      {/if}
    </div>
    {#each (pair.white?.children.slice(1) ?? []) as subVarRoot}
      <div class="var-block">{@render renderVarLine(subVarRoot)}</div>
    {/each}
    {#each (pair.black?.children.slice(1) ?? []) as subVarRoot}
      <div class="var-block">{@render renderVarLine(subVarRoot)}</div>
    {/each}
  {/each}
{/snippet}

<!-- ── Move list ─────────────────────────────────────────────────────────── -->
<div class="move-list" bind:this={listEl}>

  {#if rootNode}
    <!-- ── Tree mode ─────────────────────────────────────────────────────── -->
    {#if mainlineNodes.length <= 1}
      <span class="empty">No moves yet</span>
    {:else}
      {#key treeRevision}
      <div class="tree-flow">
        {#each mainlinePairs as pair}
          <div class="tree-row">
            <span class="num-n">{pair.moveNum}.</span>
            {#if pair.white}
              <button
                class="move-token"
                class:active={pair.white === activeNode}
                onmouseenter={() => onHoverMove?.(parseMove(pair.white.san))}
                onmouseleave={() => onHoverMove?.(null)}
                onclick={() => onNavigate?.(pair.white)}
              >{pair.white.san}</button>
            {:else}
              <span class="move-dots">...</span>
            {/if}
            {#if pair.black}
              <button
                class="move-token"
                class:active={pair.black === activeNode}
                onmouseenter={() => onHoverMove?.(parseMove(pair.black.san))}
                onmouseleave={() => onHoverMove?.(null)}
                onclick={() => onNavigate?.(pair.black)}
              >{pair.black.san}</button>
            {/if}
          </div>
          {#each (pair.white?.children.slice(1) ?? []) as varRoot}
            <div class="var-block">{@render renderVarLine(varRoot)}</div>
          {/each}
          {#each (pair.black?.children.slice(1) ?? []) as varRoot}
            <div class="var-block">{@render renderVarLine(varRoot)}</div>
          {/each}
        {/each}
      </div>
      {/key}
    {/if}

  {:else}
    <!-- ── Flat mode ─────────────────────────────────────────────────────── -->
    {#if moves.length === 0}
      <span class="empty">No moves yet</span>
    {/if}
    {#each pairs as { num, w, b }}
      <div class="move-row">
        <span class="move-num">{num}.</span>
        <button
          class="move-token"
          class:active={activeIndex === w}
          data-move-idx={w}
          onmouseenter={() => onHoverMove?.(parseMove(moves[w]))}
          onmouseleave={() => onHoverMove?.(null)}
          onclick={() => onNavigate?.(w)}
        >{moves[w]}</button>
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
  {/if}

</div>

<style>
  .replay-bar {
    display: flex;
    gap: 0.25rem;
    padding: 0.5rem 0.6rem;
    border-bottom: 1px solid var(--line);
    background: var(--surface);
    flex-shrink: 0;
  }
  .nav-btn {
    flex: 1;
    padding: 0.4rem 0;
    background: var(--surface-2);
    border: 1px solid var(--line-strong);
    color: var(--fg-2);
    border-radius: 4px;
    font-size: 0.9rem;
  }
  .nav-btn:hover { background: var(--surface-3); color: #fff; }

  .move-list {
    flex: 1;
    overflow-y: auto;
    padding: 0.4rem 0;
    font-size: 0.88rem;
    font-family: var(--font-body);
    font-variant-numeric: tabular-nums;
    scrollbar-width: thin;
    scrollbar-color: var(--surface-3) var(--bg);
  }
  .move-list::-webkit-scrollbar { width: 6px; }
  .move-list::-webkit-scrollbar-track { background: var(--bg); }
  .move-list::-webkit-scrollbar-thumb { background: var(--surface-3); border-radius: 3px; }
  .move-list::-webkit-scrollbar-thumb:hover { background: var(--faint); }

  /* ── Tree mode ── */
  .tree-flow {
    padding: 0 0.3rem;
  }

  .tree-row {
    display: grid;
    grid-template-columns: 2.4em 1fr 1fr;
    align-items: center;
    line-height: 1.9;
  }
  .tree-row:hover { background: rgba(255,255,255,0.04); }

  .num-n {
    color: var(--faint);
    user-select: none;
    text-align: right;
    padding-right: 0.3em;
  }
  .move-dots {
    color: var(--faint);
    user-select: none;
    padding: 0.12rem 0.35rem;
  }

  .var-block {
    display: block;
    background: var(--surface-2);
    border-left: 2px solid var(--accent);
    padding: 0.1rem 0.4rem;
    margin: 0.15rem 0.2rem;
    border-radius: 2px;
  }

  /* ── Flat mode ── */
  .move-row {
    display: flex;
    align-items: center;
    gap: 0.25rem;
    padding: 0 0.4rem;
  }
  .move-row:hover { background: rgba(255,255,255,0.04); }

  /* ── Shared ── */
  .move-num {
    color: var(--faint);
    user-select: none;
    margin-right: 0.1em;
    /* flat mode */
    min-width: 2em;
    text-align: right;
    flex-shrink: 0;
  }
  .move-token {
    padding: 0.12rem 0.35rem;
    border-radius: 3px;
    cursor: pointer;
    color: var(--fg-2);
    white-space: nowrap;
    background: none;
    border: none;
    font-family: var(--font-body);
    font-variant-numeric: tabular-nums;
    font-size: inherit;
    text-align: left;
  }
  /* flat mode: tokens fill their column */
  .move-row .move-token { flex: 1; }

  .move-token:hover         { background: rgba(216, 166, 74, 0.25); color: #fff; }
  .move-token.active        { background: rgba(216, 166, 74, 0.4);  color: #fff; font-weight: 600; }
  .move-token.variation-move { color: var(--muted); }
  .move-token.variation-move:hover { color: #fff; }

  .empty { padding: 0.5rem 0.75rem; color: var(--faint); font-style: italic; display: block; }
</style>
