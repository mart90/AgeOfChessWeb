<script>
  import { onMount, onDestroy } from 'svelte';
  import { startHub } from './lib/hub.js';
  import { navigate as navigateTo } from './lib/navigate.js';
  import Board from './Board.svelte';
  import MoveList from './MoveList.svelte';
  import { settings } from './lib/settings.svelte.js';
  import { currentGame } from './lib/currentGame.svelte.js';
  import { playSound } from './lib/sound.js';

  const { gameId } = $props();

  // ── Shop pieces ─────────────────────────────────────────────────────────

  const SHOP = [
    { code: 'q', name: 'Queen',  cost: 70 },
    { code: 'r', name: 'Rook',   cost: 35 },
    { code: 'b', name: 'Bishop', cost: 26 },
    { code: 'n', name: 'Knight', cost: 28 },
    { code: 'p', name: 'Pawn',   cost: 20 },
  ];

  // ── Core state ───────────────────────────────────────────────────────────

  let hub            = null;
  let boardCanvas    = null;   // canvas element exposed by Board
  let boardColHeight = $state(0);
  let gameState      = $state(null);
  let playerToken    = $state(null);

  // Sync map seed to NavBar's Copy map seed button whenever gameState arrives
  $effect(() => { if (gameState?.mapSeed) currentGame.mapSeed = gameState.mapSeed; });
  onDestroy(() => { currentGame.mapSeed = null; document.title = 'Goldrush Gambit'; });

  // Page title: "{White} vs {Black}" once we know player names
  $effect(() => {
    if (gameState) document.title = `${gameState.white.name} vs ${gameState.black.name}`;
  });
  let isWhite        = $state(true);
  let statusMsg      = $state('Loading…');
  let inviteUrl      = $state('');
  let isSpectator    = $state(false);

  // ── Local clock state ────────────────────────────────────────────────────

  let localWhiteMs   = $state(0);
  let localBlackMs   = $state(0);
  let turnStartedAt  = 0;      // Unix ms when the current turn began (from server)
  let timeoutClaimed = false;  // prevent invoking ClaimTimeout twice per timeout

  // ── Bidding state ─────────────────────────────────────────────────────────

  let biddingState     = $state(null);
  let myBid            = $state('');
  let bidSubmitted     = $state(false);
  let localCreatorMs   = $state(0);
  let localJoinerMs    = $state(0);
  let wasCreator       = false;        // captured at BiddingStarted, before ColorAssigned changes isWhite
  let pendingBidReveal = $state(null); // { opponentBid } shown to winner until acknowledged
  let resignPending    = $state(false);

  // ── Rematch state ─────────────────────────────────────────────────────────

  let rematchRequested         = $state(false);
  let opponentRematchRequested = $state(false);

  // ── Chat state ───────────────────────────────────────────────────────────

  let chatMessages = $state([]);
  let chatInput    = $state('');
  let unreadChat   = $state(0);
  let activeTab    = $state('moves');  // 'moves' | 'chat'
  let chatEl       = $state(null);

  function switchTab(tab) {
    activeTab = tab;
    if (tab === 'chat') unreadChat = 0;
  }

  function sendChat() {
    const msg = chatInput.trim();
    if (!msg || !hub || isSpectator) return;
    hub.invoke('SendChat', playerToken, msg);
    chatInput = '';
  }

  $effect(() => {
    // Scroll chat to bottom whenever a new message arrives and chat is visible
    if (chatEl && chatMessages.length > 0) {
      chatEl.scrollTop = chatEl.scrollHeight;
    }
  });

  function syncLocalTimes(state) {
    if (!state) return;
    localWhiteMs  = state.white.timeMsRemaining;
    localBlackMs  = state.black.timeMsRemaining;
    turnStartedAt = state.turnStartedAt ?? 0;
    timeoutClaimed = false;
  }

  function tickClocks() {
    if (biddingState) {
      const elapsed = Date.now() - biddingState.startedAtUnixMs;
      localCreatorMs = biddingState.creatorBidPlaced
        ? biddingState.creatorFrozenMs
        : Math.max(0, biddingState.initialMs - elapsed);
      localJoinerMs = biddingState.joinerBidPlaced
        ? biddingState.joinerFrozenMs
        : Math.max(0, biddingState.initialMs - elapsed);
    }
    if (!gameState || gameState.gameEnded || !turnStartedAt || !gameState.timeControlEnabled) return;
    const elapsed = Date.now() - turnStartedAt;
    if (gameState.white.isActive) {
      localWhiteMs = Math.max(0, gameState.white.timeMsRemaining - elapsed);
      if (localWhiteMs === 0 && !timeoutClaimed) {
        timeoutClaimed = true;
        if (!isSpectator) hub?.invoke('ClaimTimeout', playerToken);
      }
    } else {
      localBlackMs = Math.max(0, gameState.black.timeMsRemaining - elapsed);
      if (localBlackMs === 0 && !timeoutClaimed) {
        timeoutClaimed = true;
        if (!isSpectator) hub?.invoke('ClaimTimeout', playerToken);
      }
    }
  }

  // ── Interaction state ────────────────────────────────────────────────────

  let selectedSquare  = $state(null);
  let legalDests      = $state([]);
  let dragOverSquare  = $state(null);

  /**
   * dragging = null | { type:'move', fromX, fromY, imgSrc, ghostX, ghostY }
   *                 | { type:'place', code, imgSrc, ghostX, ghostY }
   */
  let dragging = $state(null);

  // ── Replay & notation hover ──────────────────────────────────────────────

  let stateHistory  = [];          // GameStateDto snapshots; index = move count
  let replayIndex   = $state(null); // null = live; 0..n = replay
  let hoverSquares  = $state([]);   // squares to highlight from notation hover

  const displayedState = $derived(
    replayIndex !== null && stateHistory[replayIndex]
      ? stateHistory[replayIndex]
      : gameState
  );

  function navigate(dir) {
    const max      = stateHistory.length - 1;
    const gameOver = gameState?.gameEnded ?? false;
    // Helper: during a live game, reaching the last state means returning to live mode (null).
    const resolve = (idx) => (!gameOver && idx >= max) ? null : idx;

    if (dir === 'start') replayIndex = 0;
    else if (dir === 'end')  replayIndex = gameOver ? max : null;
    else if (dir === 'prev') replayIndex = resolve(Math.max(0, (replayIndex ?? max) - 1));
    else if (dir === 'next') replayIndex = resolve(Math.min(max, (replayIndex ?? max) + 1));
    // dir is a 0-indexed move number; stateHistory[n] = state after n moves, so +1
    else if (typeof dir === 'number') replayIndex = resolve(Math.max(0, Math.min(max, dir + 1)));
  }

  function handleHoverMove(parsed) {
    if (!parsed) { hoverSquares = []; return; }
    const sqs = [];
    if (parsed.from) sqs.push(parsed.from);
    if (parsed.to)   sqs.push(parsed.to);
    hoverSquares = sqs;
  }

  // Parse from/to squares out of a single notation string (same convention as MoveList)
  function parseNotation(notation) {
    if (!notation) return [];
    const clean = notation.replace(/[+#]$/, '');
    const place = clean.match(/^([a-z])(\d+)=([QRBNp])$/);
    if (place) return [{ x: place[1].charCodeAt(0) - 97, y: parseInt(place[2]) - 1 }];
    const move = clean.match(/^([a-z])(\d+)[-x]([a-z])(\d+)$/);
    if (move) return [
      { x: move[1].charCodeAt(0) - 97, y: parseInt(move[2]) - 1 },
      { x: move[3].charCodeAt(0) - 97, y: parseInt(move[4]) - 1 },
    ];
    return [];
  }

  const lastMoveSquares = $derived(parseNotation(displayedState?.moves?.at(-1)));

  // ── Derived ──────────────────────────────────────────────────────────────

  const me       = $derived(displayedState ? (isWhite ? displayedState.white : displayedState.black) : null);
  const opponent = $derived(displayedState ? (isWhite ? displayedState.black : displayedState.white) : null);
  const isMyTurn = $derived(replayIndex === null && !isSpectator && (gameState ? (isWhite ? gameState.white.isActive : gameState.black.isActive) : false));
  const mapSize  = $derived(
    displayedState?.squares?.length
      ? Math.max(...displayedState.squares.map(s => Math.max(s.x, s.y))) + 1
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
    const bx = isWhite ? cx : n - 1 - cx;
    const by = isWhite ? n - 1 - cy : cy;
    if (bx < 0 || bx >= n || by < 0 || by >= n) return null;
    return { x: bx, y: by };
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
    if (!isMyTurn || gameState?.gameEnded || biddingState) return;
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
      playSound('move');
    } else if (dragging.type === 'place') {
      hub?.invoke('PlacePiece', playerToken, toX, toY, dragging.code);
      playSound('move');
    }
    clearDrag();
  }

  function onSquareClick(x, y) {
    if (!isMyTurn || gameState?.gameEnded || biddingState) return;
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
      playSound('move');
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
    if (!isMyTurn || gameState?.gameEnded || biddingState) return;
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
      playSound('move');
    }
    // Board piece drops are handled by onDropOnBoard; this catches drops outside the canvas
    clearDrag();
  }

  // ── SignalR ───────────────────────────────────────────────────────────────

  onMount(async () => {
    // Global event listeners and clock — needed for both replay and live paths.
    window.addEventListener('pointermove', globalPointerMove);
    window.addEventListener('pointerup',   globalPointerUp);
    const clockInterval = setInterval(tickClocks, 250);
    function teardown() {
      window.removeEventListener('pointermove', globalPointerMove);
      window.removeEventListener('pointerup',   globalPointerUp);
      clearInterval(clockInterval);
    }

    // ── Probe: is this game live or finished? ─────────────────────────────────
    let probe;
    try {
      const res = await fetch(`/api/game/${gameId}`);
      if (!res.ok) { statusMsg = 'Game not found.'; return teardown; }
      probe = await res.json();
    } catch {
      statusMsg = 'Could not connect to server.';
      return teardown;
    }

    // ── Server-restarted game ─────────────────────────────────────────────────
    if (probe.serverRestarted) {
      statusMsg = 'This game was interrupted when the server restarted.';
      return teardown;
    }

    // ── Finished game: load replay from stored moves ──────────────────────────
    // Finished games return { movesJson, mapSeed, result, ... };
    // live games return a GameStateDto with a `squares` array instead.
    if ('movesJson' in probe) {
      const storedColor = localStorage.getItem(`color_${gameId}`);
      isWhite     = storedColor !== 'black';
      isSpectator = true;
      try {
        const r = await fetch(`/api/game/${gameId}/replay`);
        if (!r.ok) throw new Error();
        const { snapshots } = await r.json();
        if (!snapshots?.length) throw new Error();
        stateHistory = snapshots;
        gameState    = snapshots[snapshots.length - 1];
        replayIndex  = stateHistory.length - 1;
        statusMsg    = formatResult(gameState.result);
      } catch {
        statusMsg = 'Replay not available for this game.';
      }
      return teardown;
    }

    // ── Live game: connect to hub ─────────────────────────────────────────────
    const resolved = resolveToken();
    if (!resolved) {
      isSpectator = true;
    } else {
      playerToken = resolved.token;
      isWhite     = resolved.white;
    }

    hub = await startHub();

    hub.onreconnecting(() => {
      statusMsg = 'Connection lost — reconnecting…';
    });
    hub.onreconnected(async () => {
      // Re-probe to check if the game is still live (server may have restarted)
      try {
        const res = await fetch(`/api/game/${gameId}`);
        if (res.ok) {
          const reprobe = await res.json();
          if (reprobe.serverRestarted) {
            statusMsg = 'This game was interrupted when the server restarted.';
            return;
          }
          if (!('squares' in reprobe)) {
            // Game finished while we were disconnected — show result
            statusMsg = formatResult(reprobe.result) || 'Game ended.';
            return;
          }
          // Game is still live — clear any stale bidding UI from before the disconnect.
          // The hub will re-send BiddingStarted when both players reconnect if bidding
          // is still genuinely in progress.
          biddingState = null;
        }
      } catch { /* ignore probe failure, try to rejoin anyway */ }

      try {
        if (isSpectator) {
          await hub.invoke('WatchGame', gameId);
        } else if (playerToken) {
          await hub.invoke('JoinGame', playerToken);
          if (!gameState) statusMsg = 'Reconnected — waiting for opponent…';
        }
      } catch {
        statusMsg = 'Reconnected, but failed to rejoin game — please refresh.';
      }
    });
    hub.onclose(() => {
      statusMsg = 'Connection lost — please refresh the page.';
    });

    hub.on('GameStarted', (state) => {
      // If bidding just resolved and we're the winner, capture the opponent's bid for acknowledgement
      if (biddingState == null) {
        playSound('game_started')
      }
      if (biddingState?.revealedCreatorBid != null && isWhite) {
        const opponentBid = wasCreator
          ? biddingState.revealedJoinerBid
          : biddingState.revealedCreatorBid;
        pendingBidReveal = { opponentBid };
      }
      stateHistory = [state];
      gameState = state; biddingState = null; syncLocalTimes(state); statusMsg = ''; inviteUrl = '';
    });
    hub.on('StateUpdated', (state) => {
      stateHistory = [...stateHistory, state];
      gameState = state; syncLocalTimes(state); clearDrag(); resignPending = false;
      // Return to live mode so the player can interact (their turn may have come up)
      if (replayIndex !== null) replayIndex = null;
    });
    hub.on('GameEnded', (state) => {
      stateHistory = [...stateHistory, state];
      gameState = state; syncLocalTimes(state);
      statusMsg = formatResult(state.result); clearDrag();
      replayIndex = stateHistory.length - 1;  // enable replayer
      localStorage.removeItem(`inviteUrl_${gameId}`);
    });
    hub.on('Error', (msg) => {
      console.warn('[GameHub]', msg);
    });
    hub.on('ChatMessage', (msg) => {
      chatMessages = [...chatMessages, msg];
      if (activeTab !== 'chat') unreadChat++;
    });
    hub.on('ChatHistory', (msgs) => {
      chatMessages = msgs;
    });
    hub.on('BiddingStarted', (b) => { 
      biddingState = b; 
      bidSubmitted = isWhite ? b.creatorBidPlaced : b.joinerBidPlaced; 
      wasCreator = isWhite; 
      statusMsg = ''; inviteUrl = '';
      playSound('game_started') 
    });
    hub.on('BidPlaced',      (b) => { biddingState = b; });
    hub.on('ColorAssigned',  (white) => {
      isWhite = white;
      localStorage.setItem(`color_${gameId}`, white ? 'white' : 'black');
    });
    hub.on('RematchRequested', (requestedByWhite) => {
      if (requestedByWhite === isWhite) rematchRequested = true;
      else opponentRematchRequested = true;
    });
    hub.on('RematchReady', (newGameId, newToken, newIsWhite) => {
      localStorage.setItem(`token_${newGameId}`, newToken);
      localStorage.setItem(`color_${newGameId}`, newIsWhite ? 'white' : 'black');
      navigateTo(`/game/${newGameId}`);
    });

    if (isSpectator) {
      await hub.invoke('WatchGame', gameId);
    } else {
      await hub.invoke('JoinGame', playerToken);
      if (!gameState) {
        inviteUrl = localStorage.getItem(`inviteUrl_${gameId}`) ?? '';
        statusMsg = isWhite
          ? 'Share the invite link, then wait for your opponent…'
          : 'Waiting for the game to start…';
      }
    }

    return teardown;
  });

  function resign() {
    if (!hub || gameState?.gameEnded) return;
    hub.invoke('Resign', playerToken);
    resignPending = false;
  }

  function requestRematch() {
    if (!hub || rematchRequested) return;
    rematchRequested = true;
    hub.invoke('RequestRematch', playerToken);
  }

  function copyInvite() {
    navigator.clipboard.writeText(inviteUrl).catch(() => {});
  }

  function submitBid() {
    const amount = parseInt(myBid);
    if (isNaN(amount)) return;
    bidSubmitted = true;
    hub?.invoke('SubmitBid', playerToken, amount);
  }

  function handleKeydown(e) {
    if (replayIndex === null) return;
    if (e.key === 'ArrowLeft')  { e.preventDefault(); navigate('prev'); }
    if (e.key === 'ArrowRight') { e.preventDefault(); navigate('next'); }
    if (e.key === 'Home')       { e.preventDefault(); navigate('start'); }
    if (e.key === 'End')        { e.preventDefault(); navigate('end'); }
  }
</script>

<svelte:window onkeydown={handleKeydown} />

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
<div class="game-layout">

  <!-- Opponent panel (top) — hidden until opponent connects -->
  {#if opponent}
  <div class="player-panel">
    <span class="dot" class:active={opponent.isActive && !biddingState}></span>
    <span class="name">
      {opponent.name}
      {#if opponent.elo != null}<span class="elo-label">({opponent.elo})</span>{/if}
      {#if !biddingState}<span class="color-label">{isWhite ? 'Black' : 'White'}</span>{/if}
    </span>
    <span class="gold">⚙ {opponent.gold}g</span>
    {#if biddingState}
      {@const oppBidPlaced = isWhite ? biddingState.joinerBidPlaced : biddingState.creatorBidPlaced}
      {@const oppBidMs    = isWhite ? localJoinerMs : localCreatorMs}
      {#if biddingState.initialMs < 86400000}
        <span class="clock" class:ticking={!oppBidPlaced}>{formatTime(oppBidMs)}</span>
      {/if}
      {#if oppBidPlaced}<span class="bid-check">✓</span>{/if}
    {:else if opponent.timeMsRemaining > 0}
      {@const oppMs = isWhite ? localBlackMs : localWhiteMs}
      <span class="clock" class:ticking={opponent.isActive}>{formatTime(oppMs)}</span>
    {/if}
  </div>
  {/if}

  <!-- Status / invite bar -->
  {#if statusMsg || inviteUrl}
    <div class="status-bar">
      <span>{statusMsg}</span>
      {#if inviteUrl}
        <button class="copy-btn" onclick={copyInvite}>Copy invite link</button>
      {/if}
    </div>
  {/if}

  <!-- Board + move list side by side -->
  <div class="game-body">
    <div class="board-col">
      <div class="board-main" bind:clientHeight={boardColHeight}>
      <div class="board-wrap">
        {#if displayedState}
          <Board
            squares={displayedState.squares}
            selectedSquare={replayIndex !== null ? null : selectedSquare}
            legalDests={replayIndex !== null ? [] : legalDests}
            dragOverSquare={replayIndex !== null ? null : dragOverSquare}
            {hoverSquares}
            {lastMoveSquares}
            showCoords={settings.showCoordinates}
            {mapSize}
            {isWhite}
            onPieceGrabbed={replayIndex !== null ? null : onPieceGrabbed}
            onDropOnBoard={replayIndex !== null ? null : onDropOnBoard}
            onHoverSquare={replayIndex !== null ? null : onHoverSquare}
            onSquareClick={replayIndex !== null ? null : onSquareClick}
            onCanvasReady={(el) => { boardCanvas = el; }}
          />
        {:else}
          <div class="board-placeholder">Waiting for game…</div>
        {/if}
      </div>

  <!-- Controls: bidding form OR bid-reveal acknowledgement OR shop+resign -->
  {#if biddingState && !isSpectator}
    <div class="controls bid-controls">
      {#if biddingState.revealedCreatorBid != null}
        <span class="bid-label">
          {isWhite ? 'You' : 'Opp'}: <strong>{biddingState.revealedCreatorBid}g</strong>
          &nbsp;·&nbsp;
          {isWhite ? 'Opp' : 'You'}: <strong>{biddingState.revealedJoinerBid}g</strong>
        </span>
        <span class="bid-waiting">Resolving…</span>
      {:else if !bidSubmitted}
        <span class="bid-label">Bid for White:</span>
        <span class="bid-help">
          <button class="bid-help-btn" tabindex="-1">?</button>
          <div class="bid-help-tip">
            Both players secretly bid gold for the right to play White. The higher bidder
            wins White and their bid is added to Black's starting gold.
            <br><br>Bids can be negative when Black is better, which can happen on full random maps. In that case, Black starts with negative gold.
            <br><br>
            <strong>Example:</strong> You bid 8g, the opponent bids 6g. You play White, and the opponent plays Black with 8 starting gold.
          </div>
        </span>
        <input
          class="bid-input"
          type="number"
          bind:value={myBid}
          onkeydown={(e) => e.key === 'Enter' && submitBid()}
        />
        <button class="bid-submit-btn" onclick={submitBid}>Submit</button>
      {:else}
        <span class="bid-waiting">Bid submitted — waiting for opponent…</span>
      {/if}
    </div>
  {:else if pendingBidReveal && !isSpectator}
    <div class="controls bid-controls">
      <span class="bid-label">
        You won White — opponent bid <strong>{pendingBidReveal.opponentBid}g</strong>
      </span>
      <button class="bid-submit-btn" onclick={() => pendingBidReveal = null}>Got it</button>
    </div>
  {:else if gameState && !gameState.gameEnded && !isSpectator}
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
    </div>
  {/if}
      </div><!-- /.board-main -->

    </div><!-- /.board-col -->

    <!-- Move list / chat panel -->
    {#if displayedState}
      <div class="move-list-col" style={boardColHeight ? `max-height: ${boardColHeight}px` : ''}>
        <div class="tab-bar">
          <button class="tab" class:active={activeTab === 'moves'} onclick={() => switchTab('moves')}>Moves</button>
          <button class="tab" class:active={activeTab === 'chat'} onclick={() => switchTab('chat')}>
            Chat
            {#if unreadChat > 0}<span class="badge">{unreadChat}</span>{/if}
          </button>
        </div>
        {#if activeTab === 'moves'}
          <MoveList
            moves={gameState?.moves ?? []}
            activeIndex={replayIndex !== null ? replayIndex - 1 : (gameState?.moves?.length ?? 0) - 1}
            replayMode={true}
            onHoverMove={handleHoverMove}
            onNavigate={navigate}
          />
        {:else}
          <div class="chat-messages" bind:this={chatEl}>
            {#each chatMessages as msg}
              <div class="chat-msg">
                <span class="chat-sender">{msg.senderName}:</span>
                <span class="chat-text">{msg.message}</span>
              </div>
            {/each}
            {#if chatMessages.length === 0}
              <span class="chat-empty">No messages yet</span>
            {/if}
          </div>
          {#if !isSpectator && gameState && !gameState.gameEnded}
            <div class="chat-input-row">
              <input
                class="chat-input"
                type="text"
                maxlength="500"
                placeholder="Message…"
                bind:value={chatInput}
                onkeydown={(e) => e.key === 'Enter' && sendChat()}
              />
              <button class="chat-send-btn" onclick={sendChat}>Send</button>
            </div>
          {/if}
        {/if}
      </div>
    {/if}

  <!-- My panel — inside game-body so CSS order can place it above the move list on mobile -->
  <div class="player-panel me">
    {#if me}
      <span class="dot" class:active={me.isActive && !biddingState}></span>
      <span class="name">
        {me.name}
        {#if me.elo != null}<span class="elo-label">({me.elo})</span>{/if}
        {#if !biddingState}
          <span class="color-label">{isWhite ? 'White' : 'Black'}</span>
          {#if gameState && !gameState.gameEnded && !isSpectator}
            {#if resignPending}
              <button class="resign-confirm" onclick={resign} title="Confirm resign">✓</button>
              <button class="resign-cancel" onclick={() => resignPending = false} title="Cancel">✗</button>
            {:else}
              <button class="resign-flag" onclick={() => resignPending = true} title="Resign">⚑</button>
            {/if}
          {/if}
        {/if}
      </span>
      <span class="gold">⚙ {me.gold}g</span>
      {#if biddingState}
        {@const myBidPlaced = isWhite ? biddingState.creatorBidPlaced : biddingState.joinerBidPlaced}
        {@const myBidMs    = isWhite ? localCreatorMs : localJoinerMs}
        {#if biddingState.initialMs < 86400000}
          <span class="clock" class:ticking={!myBidPlaced}>{formatTime(myBidMs)}</span>
        {/if}
        {#if myBidPlaced}<span class="bid-check">✓</span>{/if}
      {:else if me.timeMsRemaining > 0}
        {@const myMs = isWhite ? localWhiteMs : localBlackMs}
        <span class="clock" class:ticking={me.isActive}>{formatTime(myMs)}</span>
      {/if}
    {:else}
      <span class="name muted">You</span>
    {/if}
  </div>

  </div><!-- /.game-body -->

  <!-- Rematch bar (only for players, after game ends) -->
  {#if gameState?.gameEnded && !isSpectator}
    <div class="rematch-bar">
      {#if opponentRematchRequested && !rematchRequested}
        <button class="rematch-btn accept" onclick={requestRematch}>Accept Rematch</button>
      {:else if rematchRequested}
        <span class="rematch-waiting">Rematch requested — waiting for opponent…</span>
      {:else}
        <button class="rematch-btn" onclick={requestRematch}>Request Rematch</button>
      {/if}
    </div>
  {/if}

</div><!-- /.game-layout -->
</div><!-- /.page -->

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
    flex: 1;
    padding: 0.5rem;
  }

  /* ── Outer column that constrains everything to the same width ── */
  .game-layout {
    display: flex;
    flex-direction: column;
    width: 100%;
    max-width: 1100px;
    gap: 0.5rem;
  }

  /* ── Game body (board + move list + my panel) ── */
  .game-body {
    display: flex;
    flex-direction: row;
    flex-wrap: wrap;
    align-items: stretch;
    gap: 0.5rem;
    width: 100%;
  }
  .board-col {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
    flex: 1 1 auto;
    min-width: 0;
  }
  .board-main {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }
  .move-list-col {
    display: flex;
    flex-direction: column;
    background: #1e1e38;
    border: 1px solid #2a2a4a;
    border-radius: 6px;
    overflow: hidden;
    flex: 0 0 280px;
  }

  /* ── Tabs ── */
  .tab-bar {
    display: flex;
    border-bottom: 1px solid #2a2a4a;
    flex-shrink: 0;
  }
  .tab {
    flex: 1;
    padding: 0.45rem 0.5rem;
    background: none;
    border: none;
    border-bottom: 2px solid transparent;
    color: #888;
    font-size: 0.82rem;
    font-weight: 600;
    letter-spacing: 0.04em;
    cursor: pointer;
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.35rem;
  }
  .tab:hover { color: #ccc; }
  .tab.active { color: #fff; border-bottom-color: #7b8cde; }
  .badge {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 1.2em;
    height: 1.2em;
    padding: 0 0.25em;
    border-radius: 999px;
    background: #d04040;
    color: #fff;
    font-size: 0.72rem;
    font-weight: 700;
    line-height: 1;
  }

  /* ── Chat ── */
  .chat-messages {
    flex: 1;
    overflow-y: auto;
    padding: 0.5rem 0.6rem;
    display: flex;
    flex-direction: column;
    gap: 0.3rem;
    font-size: 0.85rem;
  }
  .chat-msg {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3em;
    line-height: 1.4;
  }
  .chat-sender {
    color: #7b8cde;
    font-weight: 600;
    white-space: nowrap;
    flex-shrink: 0;
  }
  .chat-text { color: #ddd; word-break: break-word; }
  .chat-empty { color: #555; font-style: italic; padding: 0.25rem 0; }
  .chat-input-row {
    display: flex;
    gap: 0.4rem;
    padding: 0.5rem 0.6rem;
    border-top: 1px solid #2a2a4a;
    flex-shrink: 0;
  }
  .chat-input {
    flex: 1;
    min-width: 0;
    padding: 0.3rem 0.5rem;
    background: #2d2d4a;
    border: 1px solid #444;
    border-radius: 4px;
    color: #eee;
    font-size: 0.85rem;
  }
  .chat-input:focus { outline: none; border-color: #7b8cde; }
  .chat-send-btn {
    padding: 0.3rem 0.7rem;
    background: #4a6fa5;
    color: #fff;
    border: none;
    border-radius: 4px;
    font-size: 0.82rem;
    white-space: nowrap;
  }
  .chat-send-btn:hover { background: #5c82c0; }
  @media (max-width: 700px) {
    .game-body { flex-direction: column; flex-wrap: nowrap; }
    .board-col    { order: 1; }
    .player-panel.me { order: 2; width: 100%; flex: none; }
    .move-list-col { order: 3; flex: none; max-height: 260px; width: 100%; }
  }

  /* ── Player panels ── */
  .player-panel {
    display: flex;
    align-items: center;
    gap: 0.7rem;
    width: 100%;
    padding: 0.55rem 1rem;
    background: #22223a;
    border-radius: 6px;
    font-size: 1rem;
  }
  .player-panel.me { flex: 0 0 100%; }

  .dot { width: 10px; height: 10px; border-radius: 50%; background: #444; flex-shrink: 0; }
  .dot.active { background: #7cfc00; }
  .name { font-weight: 600; flex: 1; }
  .muted { opacity: 0.45; }
  .elo-label   { font-weight: 400; font-size: 0.82rem; color: #aaa; margin-left: 0.2rem; }
  .color-label { font-weight: 400; font-size: 0.8rem; color: #999; margin-left: 0.3rem; }
  .gold { color: #f5c518; font-size: 0.9rem; }

  .clock {
    font-variant-numeric: tabular-nums;
    font-size: 1rem;
    background: #333;
    padding: 0.15rem 0.5rem;
    border-radius: 4px;
  }
  .clock.ticking { background: #4a6fa5; color: #fff; }

  /* ── Status bar ── */
  .status-bar {
    display: flex; align-items: center; gap: 0.75rem;
    width: 100%;
    padding: 0.55rem 1rem;
    background: #2a2a18; border: 1px solid #555; border-radius: 6px;
    font-size: 0.95rem;
  }
  .copy-btn {
    padding: 0.2rem 0.7rem; background: #4a6fa5; color: #fff;
    border: none; border-radius: 4px; font-size: 0.82rem; white-space: nowrap;
  }

  /* ── Board ── */
  .board-wrap {
    width: 100%;
    display: flex; justify-content: center;
  }
  .board-placeholder {
    width: 100%; aspect-ratio: 1;
    background: #22223a; border-radius: 6px;
    display: flex; align-items: center; justify-content: center; color: #555;
  }

  /* ── Controls ── */
  .controls {
    display: flex; align-items: center; gap: 0.6rem;
    width: 100%;
    padding: 0.6rem 0.85rem;
    background: #22223a; border-radius: 6px;
  }
  .shop { display: flex; gap: 0.35rem; flex: 1; justify-content: center; }

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

  .resign-flag {
    background: #3a1a1a; border: 1px solid #6a2a2a; border-radius: 4px;
    padding: 0.18rem 0.6rem; font-size: 0.95rem; color: #d08080;
    cursor: pointer; line-height: 1.4; vertical-align: middle; margin-left: 0.5rem;
  }
  .resign-flag:hover { background: #5a1f1f; border-color: #a03030; color: #e8a0a0; }

  .resign-confirm, .resign-cancel {
    border: none; border-radius: 3px; font-size: 0.75rem;
    padding: 0.1rem 0.35rem; cursor: pointer; line-height: 1.3;
    vertical-align: middle; margin-left: 0.15rem;
  }
  .resign-confirm { background: #2d5a2d; color: #7cfc00; }
  .resign-confirm:hover { background: #3a773a; }
  .resign-cancel  { background: #3a3a3a; color: #aaa; }
  .resign-cancel:hover  { background: #505050; }

  /* ── Bidding help tooltip ── */
  .bid-help {
    position: relative;
    display: inline-flex;
    align-items: center;
  }
  .bid-help-btn {
    width: 1.35em; height: 1.35em;
    border-radius: 50%;
    background: #2d2d4a;
    border: 1px solid #555;
    color: #7b8cde;
    font-size: 0.75rem;
    font-weight: 700;
    cursor: default;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
  }
  .bid-help-tip {
    display: none;
    position: absolute;
    bottom: calc(100% + 8px);
    left: 50%;
    transform: translateX(-50%);
    width: 270px;
    background: #1a1a30;
    border: 1px solid #3a3a5a;
    border-radius: 6px;
    padding: 0.6rem 0.75rem;
    font-size: 0.8rem;
    color: #aaa;
    line-height: 1.55;
    z-index: 100;
    pointer-events: none;
    text-align: left;
  }
  .bid-help-tip strong { color: #ccc; }
  .bid-help:hover .bid-help-tip,
  .bid-help:focus-within .bid-help-tip { display: block; }

  /* ── Bidding controls bar ── */
  .bid-controls { justify-content: center; gap: 0.6rem; flex-wrap: wrap; }
  .bid-label { font-size: 0.9rem; color: #ccc; }
  .bid-label strong { color: #f5c518; }
  .bid-input {
    width: 90px; padding: 0.35rem 0.5rem;
    background: #2d2d4a; border: 2px solid #555; border-radius: 6px;
    color: #eee; font-size: 1rem; text-align: center;
  }
  .bid-input:focus { outline: none; border-color: #7b8cde; }
  .bid-submit-btn {
    padding: 0.35rem 1rem; background: #4a6fa5; color: #fff;
    border: none; border-radius: 6px; font-size: 0.9rem;
  }
  .bid-submit-btn:hover { background: #5c82c0; }
  .bid-waiting { font-size: 0.85rem; color: #aaa; font-style: italic; }

  /* ── Bid check / clock ── */
  .bid-check { font-size: 0.9rem; color: #7cfc00; flex-shrink: 0; }

  /* ── Rematch bar ── */
  .rematch-bar {
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 0.5rem;
  }
  .rematch-btn {
    padding: 0.45rem 1.4rem;
    background: #1e2840;
    border: 1px solid #364870;
    border-radius: 6px;
    color: #7a96cc;
    font-size: 0.9rem;
    font-weight: 600;
    cursor: pointer;
  }
  .rematch-btn:hover { background: #263252; border-color: #4a6090; color: #90aae0; }
  .rematch-btn.accept {
    background: #1a3248;
    border-color: #36688a;
    color: #6aaecc;
  }
  .rematch-btn.accept:hover { background: #224060; border-color: #4a84aa; color: #80c4e0; }
  .rematch-waiting { font-size: 0.85rem; color: #aaa; font-style: italic; }
</style>
