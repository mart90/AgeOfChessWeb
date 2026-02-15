<script>
  import { onMount } from 'svelte';
  import { startHub } from './lib/hub.js';
  import Board from './Board.svelte';

  const { gameId } = $props();

  // ── Piece shop definition ───────────────────────────────────────────────

  const SHOP = [
    { code: 'q', name: 'Queen',  cost: 70 },
    { code: 'r', name: 'Rook',   cost: 35 },
    { code: 'b', name: 'Bishop', cost: 25 },
    { code: 'n', name: 'Knight', cost: 30 },
    { code: 'p', name: 'Pawn',   cost: 20 },
  ];

  // ── State ───────────────────────────────────────────────────────────────

  let hub            = null;
  let gameState      = $state(null);
  let playerToken    = $state(null);
  let isWhite        = $state(true);
  let statusMsg      = $state('Connecting…');
  let inviteUrl      = $state('');
  let selectedSquare = $state(null);
  let placingPiece   = $state(null); // piece code while in buy mode

  // ── Derived ─────────────────────────────────────────────────────────────

  const me       = $derived(gameState ? (isWhite ? gameState.white : gameState.black) : null);
  const opponent = $derived(gameState ? (isWhite ? gameState.black : gameState.white) : null);
  const isMyTurn = $derived(me?.isActive ?? false);
  const mapSize  = $derived(
    gameState?.squares?.length
      ? Math.max(...gameState.squares.map(s => Math.max(s.x, s.y))) + 1
      : 12
  );

  // ── Helpers ─────────────────────────────────────────────────────────────

  function resolveToken() {
    const params    = new URLSearchParams(window.location.search);
    const urlToken  = params.get('t');
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
    if (ms === undefined || ms === null) return '';
    const total = Math.max(0, Math.floor(ms / 1000));
    const m = Math.floor(total / 60);
    const s = total % 60;
    return `${m}:${s.toString().padStart(2, '0')}`;
  }

  const RESULTS = {
    'w+c': 'White wins by checkmate',
    'b+c': 'Black wins by checkmate',
    'w+g': 'White wins by gold',
    'b+g': 'Black wins by gold',
    'w+s': 'White wins (stalemate)',
    'b+s': 'Black wins (stalemate)',
    'w+t': 'White wins on time',
    'b+t': 'Black wins on time',
    'w+r': 'White wins — Black resigned',
    'b+r': 'Black wins — White resigned',
  };

  function formatResult(r) { return RESULTS[r] ?? r ?? 'Game over'; }

  // ── Game interactions ───────────────────────────────────────────────────

  function handleSquareClick(x, y) {
    if (!isMyTurn || !hub || gameState?.gameEnded) return;

    if (placingPiece) {
      hub.invoke('PlacePiece', playerToken, x, y, placingPiece);
      placingPiece = null;
      return;
    }

    const sq = gameState.squares.find(s => s.x === x && s.y === y);

    if (selectedSquare) {
      if (selectedSquare.x === x && selectedSquare.y === y) {
        selectedSquare = null;
        return;
      }
      // Click own piece → switch selection
      if (sq?.piece && sq.piece.isWhite === isWhite) {
        selectedSquare = { x, y };
        return;
      }
      hub.invoke('MakeMove', playerToken, selectedSquare.x, selectedSquare.y, x, y);
      selectedSquare = null;
    } else {
      if (sq?.piece && sq.piece.isWhite === isWhite) {
        selectedSquare = { x, y };
      }
    }
  }

  function buyPiece(code) {
    if (!isMyTurn) return;
    placingPiece = placingPiece === code ? null : code;
    selectedSquare = null;
  }

  async function resign() {
    if (!hub || !playerToken || gameState?.gameEnded) return;
    if (!confirm('Resign this game?')) return;
    hub.invoke('Resign', playerToken);
  }

  function copyInvite() {
    navigator.clipboard.writeText(inviteUrl).catch(() => {});
  }

  // ── Mount ───────────────────────────────────────────────────────────────

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

    hub.on('GameStarted', (state) => {
      gameState  = state;
      statusMsg  = '';
      inviteUrl  = '';
    });

    hub.on('StateUpdated', (state) => {
      gameState      = state;
      selectedSquare = null;
      placingPiece   = null;
    });

    hub.on('GameEnded', (state) => {
      gameState      = state;
      statusMsg      = formatResult(state.result);
      selectedSquare = null;
      placingPiece   = null;
    });

    hub.on('Error', (msg) => {
      console.warn('[GameHub]', msg);
    });

    await hub.invoke('JoinGame', playerToken);

    if (!gameState) {
      statusMsg = isWhite ? 'Share the invite link, then wait for your opponent…' : 'Waiting for the game to start…';
    }
  });
</script>

<!-- ── Layout ─────────────────────────────────────────────────────────── -->

<div class="page">

  <!-- Opponent panel (top) -->
  <div class="player-panel opponent">
    {#if opponent}
      <span class="name">{opponent.name}</span>
      <span class="gold">⚙ {opponent.gold}g</span>
      {#if opponent.timeMsRemaining > 0}
        <span class="time" class:active={opponent.isActive}>{formatTime(opponent.timeMsRemaining)}</span>
      {/if}
      {#if opponent.isActive}<span class="turn-dot"></span>{/if}
    {:else}
      <span class="name dimmed">Waiting for opponent…</span>
    {/if}
  </div>

  <!-- Status / invite -->
  {#if statusMsg}
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
        {mapSize}
        {isWhite}
        onSquareClick={handleSquareClick}
      />
    {:else}
      <div class="board-placeholder">
        <p>Waiting for game to start…</p>
      </div>
    {/if}
  </div>

  <!-- Controls: piece shop + resign (only when it's your turn and game is live) -->
  {#if gameState && !gameState.gameEnded && isMyTurn}
    <div class="controls">
      {#each SHOP as item}
        <button
          class="shop-btn"
          class:active={placingPiece === item.code}
          disabled={me.gold < item.cost}
          onclick={() => buyPiece(item.code)}
          title="Place {item.name} ({item.cost}g)"
        >
          <img src="/assets/objects/{isWhite ? 'w' : 'b'}_{item.name.toLowerCase()}.png"
               alt={item.name} width="36" height="36" />
          <span class="cost">{item.cost}g</span>
        </button>
      {/each}
      <button class="resign-btn" onclick={resign}>Resign</button>
    </div>
  {/if}

  <!-- My panel (bottom) -->
  <div class="player-panel me">
    {#if me}
      <span class="name">{me.name} {isWhite ? '(White)' : '(Black)'}</span>
      <span class="gold">⚙ {me.gold}g</span>
      {#if me.timeMsRemaining > 0}
        <span class="time" class:active={me.isActive}>{formatTime(me.timeMsRemaining)}</span>
      {/if}
      {#if me.isActive}<span class="turn-dot"></span>{/if}
    {:else}
      <span class="name dimmed">You</span>
    {/if}
  </div>

</div>

<style>
  .page {
    display: flex;
    flex-direction: column;
    align-items: center;
    min-height: 100vh;
    padding: 0.5rem;
    gap: 0.5rem;
  }

  .player-panel {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    width: 100%;
    max-width: 640px;
    padding: 0.5rem 0.75rem;
    background: #22223a;
    border-radius: 6px;
    font-size: 0.95rem;
  }

  .player-panel.me { order: 10; }

  .name { font-weight: bold; flex: 1; }
  .name.dimmed { opacity: 0.5; }
  .gold { color: #f5c518; }
  .time {
    font-variant-numeric: tabular-nums;
    font-size: 1rem;
    background: #333;
    padding: 0.1rem 0.4rem;
    border-radius: 4px;
  }
  .time.active { background: #4a6fa5; color: #fff; }
  .turn-dot {
    width: 10px;
    height: 10px;
    background: #7cfc00;
    border-radius: 50%;
  }

  .status-bar {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    width: 100%;
    max-width: 640px;
    padding: 0.5rem 0.75rem;
    background: #2d2d1e;
    border: 1px solid #666;
    border-radius: 6px;
    font-size: 0.9rem;
  }

  .copy-btn {
    padding: 0.25rem 0.75rem;
    background: #4a6fa5;
    color: #fff;
    border: none;
    border-radius: 4px;
    font-size: 0.85rem;
  }

  .board-wrap {
    width: 100%;
    max-width: 640px;
    display: flex;
    justify-content: center;
  }

  .board-placeholder {
    width: 100%;
    aspect-ratio: 1;
    background: #22223a;
    border-radius: 6px;
    display: flex;
    align-items: center;
    justify-content: center;
    color: #666;
  }

  .controls {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    width: 100%;
    max-width: 640px;
    padding: 0.4rem 0.5rem;
    background: #22223a;
    border-radius: 6px;
    flex-wrap: wrap;
  }

  .shop-btn {
    display: flex;
    flex-direction: column;
    align-items: center;
    padding: 0.3rem 0.5rem;
    background: #2d2d4a;
    border: 2px solid #444;
    border-radius: 6px;
    color: #eee;
    gap: 2px;
  }
  .shop-btn:disabled { opacity: 0.35; cursor: default; }
  .shop-btn.active   { border-color: #f5c518; background: #3d3d2a; }
  .shop-btn img      { image-rendering: pixelated; }
  .cost              { font-size: 0.7rem; color: #f5c518; }

  .resign-btn {
    margin-left: auto;
    padding: 0.4rem 0.9rem;
    background: #7a1f1f;
    color: #fff;
    border: none;
    border-radius: 6px;
  }
  .resign-btn:hover { background: #a02a2a; }
</style>
