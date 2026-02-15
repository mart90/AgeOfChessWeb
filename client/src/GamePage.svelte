<script>
  import { onMount } from 'svelte';
  import { startHub } from './lib/hub.js';
  import Board from './Board.svelte';

  const { gameId } = $props();

  // ── Shop pieces ─────────────────────────────────────────────────────────

  const SHOP = [
    { code: 'q', name: 'Queen',  cost: 70 },
    { code: 'r', name: 'Rook',   cost: 35 },
    { code: 'b', name: 'Bishop', cost: 25 },
    { code: 'n', name: 'Knight', cost: 30 },
    { code: 'p', name: 'Pawn',   cost: 20 },
  ];

  // ── Core state ───────────────────────────────────────────────────────────

  let hub            = null;
  let boardCanvas    = null;   // canvas element exposed by Board
  let gameState      = $state(null);
  let playerToken    = $state(null);
  let isWhite        = $state(true);
  let statusMsg      = $state('Connecting…');
  let inviteUrl      = $state('');

  // ── Interaction state ────────────────────────────────────────────────────

  let selectedSquare  = $state(null);
  let legalDests      = $state([]);
  let dragOverSquare  = $state(null);

  /**
   * dragging = null | { type:'move', fromX, fromY, imgSrc, ghostX, ghostY }
   *                 | { type:'place', code, imgSrc, ghostX, ghostY }
   */
  let dragging = $state(null);

  // ── Derived ──────────────────────────────────────────────────────────────

  const me       = $derived(gameState ? (isWhite ? gameState.white : gameState.black) : null);
  const opponent = $derived(gameState ? (isWhite ? gameState.black : gameState.white) : null);
  const isMyTurn = $derived(me?.isActive ?? false);
  const mapSize  = $derived(
    gameState?.squares?.length
      ? Math.max(...gameState.squares.map(s => Math.max(s.x, s.y))) + 1
      : 12
  );

  // ── Helpers ──────────────────────────────────────────────────────────────

  function resolveToken() {
    const params   = new URLSearchParams(window.location.search);
    const urlToken = params.get('t');
    if (urlToken) {
      localStorage.setItem(`token_${gameId}`,  urlToken);
      localStorage.setItem(`color_${gameId}`,  'black');
      history.replaceState({}, '', `/game/${gameId}`);
      return { token: urlToken, white: false };
    }
    const stored = localStorage.getItem(`token_${gameId}`);
    const color  = localStorage.getItem(`color_${gameId}`);
    if (stored) return { token: stored, white: color !== 'black' };
    return null;
  }

  function formatTime(ms) {
    if (!ms) return '';
    const t = Math.max(0, Math.floor(ms / 1000));
    return `${Math.floor(t / 60)}:${String(t % 60).padStart(2, '0')}`;
  }

  const RESULT_LABELS = {
    'w+c': 'White wins by checkmate',     'b+c': 'Black wins by checkmate',
    'w+g': 'White wins by gold',          'b+g': 'Black wins by gold',
    'w+s': 'White wins (stalemate)',      'b+s': 'Black wins (stalemate)',
    'w+t': 'White wins on time',          'b+t': 'Black wins on time',
    'w+r': 'White wins — Black resigned', 'b+r': 'Black wins — White resigned',
  };
  function formatResult(r) { return RESULT_LABELS[r] ?? 'Game over'; }

  function shopImgSrc(code) {
    const name = SHOP.find(s => s.code === code)?.name?.toLowerCase() ?? code;
    return `/assets/objects/${isWhite ? 'w' : 'b'}_${name}.png`;
  }

  // ── Board coordinate helper (for shop drag hit-testing) ──────────────────

  function globalCursorToBoard(clientX, clientY) {
    if (!boardCanvas) return null;
    const rect   = boardCanvas.getBoundingClientRect();
    const n      = mapSize;
    const cellPx = rect.width / n;
    let cx = Math.floor((clientX - rect.left) / cellPx);
    let cy = Math.floor((clientY - rect.top)  / cellPx);
    if (!isWhite) { cx = n - 1 - cx; cy = n - 1 - cy; }
    if (cx < 0 || cx >= n || cy < 0 || cy >= n) return null;
    return { x: cx, y: cy };
  }

  // ── Legal move / placement fetching ──────────────────────────────────────

  async function fetchLegalMoves(fromX, fromY) {
    if (!hub) return [];
    try {
      const raw = await hub.invoke('GetLegalMoves', playerToken, fromX, fromY);
      return (raw ?? []).map(([x, y]) => ({ x, y }));
    } catch { return []; }
  }

  async function fetchLegalPlacements(code) {
    if (!hub) return [];
    try {
      const raw = await hub.invoke('GetLegalPlacements', playerToken, code);
      return (raw ?? []).map(([x, y]) => ({ x, y }));
    } catch { return []; }
  }

  // ── Board callbacks ───────────────────────────────────────────────────────

  async function onPieceGrabbed(x, y, clientX, clientY) {
    if (!isMyTurn || gameState?.gameEnded) return;
    const sq = gameState.squares.find(s => s.x === x && s.y === y);
    if (!sq?.piece || sq.piece.isWhite !== isWhite) return;

    selectedSquare = { x, y };
    legalDests = await fetchLegalMoves(x, y);
    const pieceName = sq.piece.type.replace(/^(White|Black)/, '').toLowerCase();
    const imgSrc = `/assets/objects/${isWhite ? 'w' : 'b'}_${pieceName}.png`;
    dragging = { type: 'move', fromX: x, fromY: y, imgSrc, ghostX: clientX, ghostY: clientY };
  }

  function onDropOnBoard(toX, toY) {
    if (!dragging) return;
    if (dragging.type === 'move') {
      hub?.invoke('MakeMove', playerToken, dragging.fromX, dragging.fromY, toX, toY);
    } else if (dragging.type === 'place') {
      hub?.invoke('PlacePiece', playerToken, toX, toY, dragging.code);
    }
    clearDrag();
  }

  function onSquareClick(x, y) {
    if (!isMyTurn || gameState?.gameEnded) return;
    const sq = gameState.squares.find(s => s.x === x && s.y === y);

    if (selectedSquare) {
      if (selectedSquare.x === x && selectedSquare.y === y) {
        selectedSquare = null; legalDests = [];
        return;
      }
      if (sq?.piece?.isWhite === isWhite) {
        selectedSquare = { x, y };
        fetchLegalMoves(x, y).then(d => { legalDests = d; });
        return;
      }
      hub?.invoke('MakeMove', playerToken, selectedSquare.x, selectedSquare.y, x, y);
      selectedSquare = null; legalDests = [];
    } else {
      if (sq?.piece?.isWhite === isWhite) {
        selectedSquare = { x, y };
        fetchLegalMoves(x, y).then(d => { legalDests = d; });
      }
    }
  }

  function onHoverSquare(sq) {
    if (dragging) dragOverSquare = sq;
  }

  function clearDrag() {
    dragging = null;
    dragOverSquare = null;
    selectedSquare = null;
    legalDests = [];
  }

  // ── Shop drag ─────────────────────────────────────────────────────────────

  async function shopPointerDown(e, code) {
    if (!isMyTurn || gameState?.gameEnded) return;
    const item = SHOP.find(s => s.code === code);
    if (!item || (me?.gold ?? 0) < item.cost) return;

    e.preventDefault();
    legalDests = await fetchLegalPlacements(code);
    dragging = { type: 'place', code, imgSrc: shopImgSrc(code), ghostX: e.clientX, ghostY: e.clientY };
  }

  // ── Global pointer tracking ───────────────────────────────────────────────

  function globalPointerMove(e) {
    if (!dragging) return;
    dragging = { ...dragging, ghostX: e.clientX, ghostY: e.clientY };
    dragOverSquare = globalCursorToBoard(e.clientX, e.clientY);
  }

  function globalPointerUp(e) {
    if (!dragging) return;
    // Shop piece dropped anywhere on the board
    if (dragging.type === 'place') {
      const sq = globalCursorToBoard(e.clientX, e.clientY);
      if (sq) hub?.invoke('PlacePiece', playerToken, sq.x, sq.y, dragging.code);
    }
    // Board piece drops are handled by onDropOnBoard; this catches drops outside the canvas
    clearDrag();
  }

  // ── SignalR ───────────────────────────────────────────────────────────────

  onMount(async () => {
    const resolved = resolveToken();
    if (!resolved) {
      statusMsg = 'No player token found. Did you create or join this game?';
      return;
    }
    playerToken = resolved.token;
    isWhite     = resolved.white;
    inviteUrl   = localStorage.getItem(`inviteUrl_${gameId}`) ?? '';

    hub = await startHub();

    hub.on('GameStarted', (state) => { gameState = state; statusMsg = ''; inviteUrl = ''; });
    hub.on('StateUpdated', (state) => { gameState = state; clearDrag(); });
    hub.on('GameEnded',   (state) => { gameState = state; statusMsg = formatResult(state.result); clearDrag(); });
    hub.on('Error',       (msg)   => console.warn('[GameHub]', msg));

    await hub.invoke('JoinGame', playerToken);
    if (!gameState) {
      statusMsg = isWhite
        ? 'Share the invite link, then wait for your opponent…'
        : 'Waiting for the game to start…';
    }

    window.addEventListener('pointermove', globalPointerMove);
    window.addEventListener('pointerup',   globalPointerUp);
    return () => {
      window.removeEventListener('pointermove', globalPointerMove);
      window.removeEventListener('pointerup',   globalPointerUp);
    };
  });

  async function resign() {
    if (!hub || gameState?.gameEnded) return;
    if (!confirm('Resign this game?')) return;
    hub.invoke('Resign', playerToken);
  }

  function copyInvite() {
    navigator.clipboard.writeText(inviteUrl).catch(() => {});
  }
</script>

<!-- ── Drag ghost (fixed overlay, follows cursor) ──────────────────────── -->
{#if dragging}
  <img
    class="drag-ghost"
    src={dragging.imgSrc}
    alt=""
    style="left:{dragging.ghostX}px; top:{dragging.ghostY}px;"
    draggable="false"
  />
{/if}

<!-- ── Page ───────────────────────────────────────────────────────────── -->
<div class="page">

  <!-- Opponent panel (top) -->
  <div class="player-panel">
    {#if opponent}
      <span class="dot" class:active={opponent.isActive}></span>
      <span class="name">{opponent.name}</span>
      <span class="gold">⚙ {opponent.gold}g</span>
      {#if opponent.timeMsRemaining > 0}
        <span class="clock" class:ticking={opponent.isActive}>{formatTime(opponent.timeMsRemaining)}</span>
      {/if}
    {:else}
      <span class="name muted">Waiting for opponent…</span>
    {/if}
  </div>

  <!-- Status / invite bar -->
  {#if statusMsg || inviteUrl}
    <div class="status-bar">
      <span>{statusMsg}</span>
      {#if inviteUrl}
        <button class="copy-btn" onclick={copyInvite}>Copy invite link</button>
      {/if}
    </div>
  {/if}

  <!-- Board -->
  <div class="board-wrap">
    {#if gameState}
      <Board
        squares={gameState.squares}
        {selectedSquare}
        {legalDests}
        {dragOverSquare}
        {mapSize}
        {isWhite}
        {onPieceGrabbed}
        {onDropOnBoard}
        {onHoverSquare}
        {onSquareClick}
        onCanvasReady={(el) => { boardCanvas = el; }}
      />
    {:else}
      <div class="board-placeholder">Waiting for game…</div>
    {/if}
  </div>

  <!-- Shop + controls (always visible, disabled when not your turn) -->
  {#if gameState && !gameState.gameEnded}
    <div class="controls">
      <div class="shop">
        {#each SHOP as item}
          {@const canAfford = (me?.gold ?? 0) >= item.cost}
          <button
            class="shop-piece"
            class:active={dragging?.type === 'place' && dragging.code === item.code}
            disabled={!isMyTurn || !canAfford}
            title="{item.name} — {item.cost}g"
            onpointerdown={(e) => shopPointerDown(e, item.code)}
          >
            <img src={shopImgSrc(item.code)} alt={item.name} width="44" height="44" draggable="false" />
            <span class="cost" class:dim={!canAfford}>{item.cost}g</span>
          </button>
        {/each}
      </div>
      <button class="resign-btn" onclick={resign} disabled={!isMyTurn}>Resign</button>
    </div>
  {/if}

  <!-- My panel (bottom) -->
  <div class="player-panel me">
    {#if me}
      <span class="dot" class:active={me.isActive}></span>
      <span class="name">
        {me.name}
        <span class="color-label">{isWhite ? 'White' : 'Black'}</span>
      </span>
      <span class="gold">⚙ {me.gold}g</span>
      {#if me.timeMsRemaining > 0}
        <span class="clock" class:ticking={me.isActive}>{formatTime(me.timeMsRemaining)}</span>
      {/if}
    {:else}
      <span class="name muted">You</span>
    {/if}
  </div>

</div>

<style>
  /* ── Drag ghost ── */
  .drag-ghost {
    position: fixed;
    width: 64px; height: 64px;
    transform: translate(-50%, -50%);
    pointer-events: none;
    z-index: 1000;
    image-rendering: pixelated;
    filter: drop-shadow(0 4px 10px rgba(0,0,0,0.7));
  }

  /* ── Page ── */
  .page {
    display: flex;
    flex-direction: column;
    align-items: center;
    min-height: 100vh;
    padding: 0.5rem;
    gap: 0.4rem;
  }

  /* ── Player panels ── */
  .player-panel {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    width: 100%;
    max-width: 640px;
    padding: 0.45rem 0.75rem;
    background: #22223a;
    border-radius: 6px;
    font-size: 0.9rem;
  }
  .player-panel.me { order: 10; }

  .dot { width: 9px; height: 9px; border-radius: 50%; background: #444; flex-shrink: 0; }
  .dot.active { background: #7cfc00; }
  .name { font-weight: 600; flex: 1; }
  .muted { opacity: 0.45; }
  .color-label { font-weight: 400; font-size: 0.75rem; color: #999; margin-left: 0.25rem; }
  .gold { color: #f5c518; font-size: 0.85rem; }

  .clock {
    font-variant-numeric: tabular-nums;
    font-size: 0.95rem;
    background: #333;
    padding: 0.1rem 0.45rem;
    border-radius: 4px;
  }
  .clock.ticking { background: #4a6fa5; color: #fff; }

  /* ── Status bar ── */
  .status-bar {
    display: flex; align-items: center; gap: 0.75rem;
    width: 100%; max-width: 640px;
    padding: 0.45rem 0.75rem;
    background: #2a2a18; border: 1px solid #555; border-radius: 6px;
    font-size: 0.88rem;
  }
  .copy-btn {
    padding: 0.2rem 0.7rem; background: #4a6fa5; color: #fff;
    border: none; border-radius: 4px; font-size: 0.82rem; white-space: nowrap;
  }

  /* ── Board ── */
  .board-wrap {
    width: 100%; max-width: 640px;
    display: flex; justify-content: center;
  }
  .board-placeholder {
    width: 100%; aspect-ratio: 1;
    background: #22223a; border-radius: 6px;
    display: flex; align-items: center; justify-content: center; color: #555;
  }

  /* ── Controls ── */
  .controls {
    display: flex; align-items: center; gap: 0.5rem;
    width: 100%; max-width: 640px;
    padding: 0.5rem 0.6rem;
    background: #22223a; border-radius: 6px;
  }
  .shop { display: flex; gap: 0.35rem; flex: 1; flex-wrap: wrap; }

  .shop-piece {
    display: flex; flex-direction: column; align-items: center;
    padding: 0.3rem 0.4rem;
    background: #2d2d4a; border: 2px solid transparent; border-radius: 8px;
    color: #eee; gap: 2px;
    transition: border-color 0.1s, background 0.1s;
    touch-action: none; user-select: none;
  }
  .shop-piece:not(:disabled):hover { border-color: #7b8cde; background: #3a3a60; }
  .shop-piece.active { border-color: #f5c518; background: #3a3a20; }
  .shop-piece:disabled { opacity: 0.3; cursor: default; }
  .shop-piece img { image-rendering: pixelated; }

  .cost { font-size: 0.68rem; color: #f5c518; }
  .cost.dim { color: #666; }

  .resign-btn {
    padding: 0.45rem 1rem; background: #7a1f1f; color: #fff;
    border: none; border-radius: 6px; white-space: nowrap; align-self: stretch;
  }
  .resign-btn:not(:disabled):hover { background: #a02828; }
  .resign-btn:disabled { opacity: 0.35; cursor: default; }
</style>
