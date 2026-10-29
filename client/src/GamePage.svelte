<script>
  import { onMount, onDestroy } from 'svelte';
  import { startHub } from './lib/hub.js';
  import { navigate as navigateTo } from './lib/navigate.js';
  import Board from './Board.svelte';
  import MoveList from './MoveList.svelte';
  import { settings } from './lib/settings.svelte.js';
  import { currentGame } from './lib/currentGame.svelte.js';
  import { playSound, stopClockSound, preloadClockSound } from './lib/sound.js';
  import { SHOP as SHOP_BASE } from './lib/constants.js';
  import { buildFromMainline, lastMainlineNode, addChild } from './lib/moveTree.js';
  import { getNodePath } from './lib/treeStorage.js';
  import { packGameTree, mainlineLength, GAME_TRANSFER_KEY, GAME_TRANSFER_FORMAT } from './lib/gameTransfer.js';
  import { addLibraryGame, updateLibraryGame } from './lib/api.js';
  import { authState } from './lib/auth.svelte.js';
  import { applyMoveToState, moveToNotation, applyPlacementToState, placementToNotation, sameSquare } from './lib/boardUtils.js';
  import { computeLegalMoves } from './lib/pathFinder.js';
  import { createBoardState, getAllLegalNewPiecePlacements } from './lib/analysis.js';
  import { EngineClient } from './lib/engine/engine.js';
  import { wheelNav } from './lib/wheelNav.js';
  import { pieceSrc } from './lib/boardTheme.js';
  import { computeComputerBid, BID_BUDGET_FACTOR } from './lib/engine/computerBid.js';
  import { createComputerGame } from './lib/api.js';

  const { gameId } = $props();

  // ── Shop pieces ─────────────────────────────────────────────────────────

  const SHOP = SHOP_BASE.map((item, i) => ({
    code: ['q', 'r', 'b', 'n', 'p'][i],
    name: item.type,
    cost: item.cost
  }));

  // ── Core state ───────────────────────────────────────────────────────────

  let hub            = null;
  let boardCanvas    = null;   // canvas element exposed by Board
  let boardColHeight = $state(0);

  // Fit the board to the window height: the layout width is chosen so the board fills exactly
  // the height left over by the panels (no vertical scrolling, also on 1080p and 1440p).
  const MOVE_LIST_WIDTH = 280 + 8;   // .move-list-col + .game-body gap
  let gameBodyEl   = $state(null);
  let boardWrapEl  = $state(null);
  let layoutWidth  = $state(null);
  function fitBoard() {
    if (!gameBodyEl || !boardWrapEl || window.innerWidth <= 700) { layoutWidth = null; return; }
    const pageBottomPadding = 8;
    const other = gameBodyEl.getBoundingClientRect().bottom + window.scrollY
                - boardWrapEl.offsetHeight + pageBottomPadding;
    const board = Math.floor(window.innerHeight - other);
    layoutWidth = Math.max(360, board) + MOVE_LIST_WIDTH;
  }
  $effect(() => {
    if (!gameBodyEl) return;
    fitBoard();
    const ro = new ResizeObserver(fitBoard);
    ro.observe(gameBodyEl);
    window.addEventListener('resize', fitBoard);
    return () => { ro.disconnect(); window.removeEventListener('resize', fitBoard); };
  });
  let gameState      = $state(null);
  let playerToken    = $state(null);

  // Sync map seed to NavBar's Copy map seed button whenever gameState arrives
  $effect(() => { if (gameState?.mapSeed) currentGame.mapSeed = gameState.mapSeed; });
  onDestroy(() => { currentGame.mapSeed = null; document.title = 'Goldrush Gambit'; });

  // Page title updates based on turn
  $effect(() => {
    if (!gameState) return;
    if (gameState.gameEnded) {
      document.title = 'Game over';
    } else {
      document.title = isMyTurn ? 'Your move' : `${gameState.white.name} vs ${gameState.black.name}`;
    }
  });
  let isWhite        = $state(true);
  let statusMsg      = $state('Loading…');
  let inviteUrl      = $state('');
  let statusDismissed = $state(null);   // statusMsg the user closed (shown again when it changes)
  let isSpectator    = $state(false);

  // ── Local clock state ────────────────────────────────────────────────────

  let localWhiteMs   = $state(0);
  let localBlackMs   = $state(0);
  let turnStartedAt  = 0;      // Unix ms when the current turn began (from server)
  let timeoutClaimed = false;  // prevent invoking ClaimTimeout twice per timeout
  let clockSoundPlayed = false;  // prevent playing clock sound multiple times per turn

  // ── Bidding state ─────────────────────────────────────────────────────────

  let biddingState     = $state(null);
  let myBid            = $state('');
  let bidSubmitted     = $state(false);
  let localCreatorMs   = $state(0);
  let localJoinerMs    = $state(0);
  let wasCreator       = false;        // captured at BiddingStarted, before ColorAssigned changes isWhite
  let pendingBidReveal = $state(null); // { opponentBid } shown to winner until acknowledged
  let resignPending    = $state(false);

  // ── Draw offers ── (the server lets a player offer again only a full move after their last offer)
  let drawOfferBy        = $state(null);   // standing offer: 'w' | 'b' | null
  let drawOfferedAtPly   = $state(-2);     // ply of my last offer
  // Draw messages in the status bar; they go away once the offer is answered or we move
  const DRAW_MSG = {
    offered:          'Your opponent offers a draw.',
    declined:         'Your draw offer was declined.',
    computerDeclined: 'The computer declines the draw.',
  };
  function clearDrawMsg(...msgs) {
    if (msgs.includes(statusMsg)) statusMsg = '';
  }
  let computerDrawThinking = $state(false);
  const DRAW_DECLINE_ADVANTAGE = 10;       // the computer declines when it's more than this much gold ahead

  // ── Rematch state ─────────────────────────────────────────────────────────

  let rematchRequested         = $state(false);
  let opponentRematchRequested = $state(false);
  let opponentWantsSameSeed    = $state(false);
  let sameSeedRematch          = $state(false);

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
    stopClockSound();  // Stop clock sound when turn changes
    localWhiteMs  = state.white.timeMsRemaining;
    localBlackMs  = state.black.timeMsRemaining;
    turnStartedAt = state.turnStartedAt ?? 0;
    timeoutClaimed = false;
    clockSoundPlayed = false;
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
      // Play clock sound if it's our turn and we're below 10 seconds
      if (!clockSoundPlayed && isWhite && isMyTurn && localWhiteMs > 0 && localWhiteMs <= 10000) {
        clockSoundPlayed = true;
        playSound('clock');
      }
    } else {
      localBlackMs = Math.max(0, gameState.black.timeMsRemaining - elapsed);
      if (localBlackMs === 0 && !timeoutClaimed) {
        timeoutClaimed = true;
        if (!isSpectator) hub?.invoke('ClaimTimeout', playerToken);
      }
      // Play clock sound if it's our turn and we're below 10 seconds
      if (!clockSoundPlayed && !isWhite && isMyTurn && localBlackMs > 0 && localBlackMs <= 10000) {
        clockSoundPlayed = true;
        playSound('clock');
      }
    }
  }

  // ── Computer opponent ────────────────────────────────────────────────────
  // In games vs the computer, the engine runs here (Web Worker) and its moves are submitted
  // to the server with SubmitComputerMove. The server validates and broadcasts them as usual.

  const isComputerGame   = $derived(gameState?.computerLevel != null);
  const COMPUTER_MIN_THINK_MS = 400;   // so instant replies from low levels don't feel abrupt
  const PIECE_CODES = { Queen: 'q', Rook: 'r', Bishop: 'b', Knight: 'n', Pawn: 'p' };
  let computerEngine     = null;
  let computerSearchPly  = -1;       // ply the current/last search was started for
  let computerThinking   = $state(false);
  let playAgainSameBoard = $state(false);
  let startingPlayAgain  = $state(false);

  function maybeComputerMove() {
    const s = gameState;
    if (!s || s.gameEnded || isSpectator || !playerToken || s.computerLevel == null || biddingState || s.biddingInProgress) return;
    const computerToMove = s.computerIsWhite ? s.white.isActive : s.black.isActive;
    if (!computerToMove) return;
    const ply = s.moves.length;
    if (computerSearchPly === ply) return;
    computerSearchPly = ply;
    computerThinking = true;
    computerEngine ??= new EngineClient();
    // $state.snapshot: reactive proxies can't be posted to a Web Worker
    const position = $state.snapshot({ squares: s.squares, whiteGold: s.white.gold, blackGold: s.black.gold, whiteIsActive: s.white.isActive });
    const started = performance.now();
    // Weak levels ask for the two best moves and sometimes play the second (if not too costly).
    const makesMistakes = (s.computerMistakeRate ?? 0) > 0;
    const request = makesMistakes
      ? { cmd: 'search', position, nodes: s.computerNodes, multiPv: 2 }
      : { cmd: 'search', position, nodes: s.computerNodes };
    computerEngine.call(request)
      .then(async (res) => {
        const wait = COMPUTER_MIN_THINK_MS - (performance.now() - started);
        if (wait > 0) await new Promise(r => setTimeout(r, wait));
        const b = makesMistakes ? chooseWithMistakes(res.lines ?? [], s) : res.best;
        // Skip if the game moved on meanwhile (e.g. resign, reconnect)
        if (!gameState || gameState.gameEnded || gameState.moves.length !== ply || !b) return;
        if (b.type === 'm') {
          await hub?.invoke('SubmitComputerMove', playerToken, 'move', b.fromPos.x, b.fromPos.y, b.toPos.x, b.toPos.y, null);
        } else {
          await hub?.invoke('SubmitComputerMove', playerToken, 'place', -1, -1, b.toPos.x, b.toPos.y, PIECE_CODES[b.pieceType]);
        }
      })
      .catch((e) => {
        if (e?.message !== 'cancelled') {
          console.warn('[Computer]', e);
          statusMsg = `Computer error: ${e?.message ?? e}`;
        }
        computerSearchPly = -1;   // allow a retry on the next state update
      })
      .finally(() => { if (computerSearchPly === ply || computerSearchPly === -1) computerThinking = false; });
  }

  // lines are sorted best-first for the side to move; scores are gold from White's point of view.
  function chooseWithMistakes(lines, s) {
    if (!lines.length) return null;
    if (lines.length < 2) return lines[0].move;
    const sign = s.computerIsWhite ? 1 : -1;
    const loss = (lines[0].score - lines[1].score) * sign;
    const mistake = Math.random() < s.computerMistakeRate && loss <= s.computerMistakeMaxLoss;
    return (mistake ? lines[1] : lines[0]).move;
  }

  $effect(() => {
    void gameState;
    maybeComputerMove();
  });

  // ── Computer bid ── (three analysis passes, see lib/engine/computerBid.js)
  let computerBidding = $state(false);

  function maybeComputerBid(b) {
    const s = gameState;
    if (!b || !s || s.computerLevel == null || isSpectator || !playerToken || computerBidding) return;
    if (b.revealedCreatorBid != null) return;
    // Before colours are resolved the computer holds the white token iff it's the "creator"
    const placed = s.computerIsWhite ? b.creatorBidPlaced : b.joinerBidPlaced;
    if (placed) return;
    computerBidding = true;
    computerEngine ??= new EngineClient();
    const squares = $state.snapshot(s.squares);
    const search = (position, nodes) => computerEngine.call({ cmd: 'search', position, nodes });
    computeComputerBid(squares, s.computerNodes * BID_BUDGET_FACTOR, search, (s.mapSeed ?? '').startsWith('r'))
      .then(({ bid }) => hub?.invoke('SubmitComputerBid', playerToken, bid))
      .catch((e) => {
        if (e?.message !== 'cancelled') {
          console.warn('[Computer bid]', e);
          statusMsg = `Computer error: ${e?.message ?? e}`;
        }
      })
      .finally(() => { computerBidding = false; });
  }

  $effect(() => {
    void biddingState;
    maybeComputerBid(biddingState);
  });

  onDestroy(() => computerEngine?.cancel());

  async function playAgainVsComputer() {
    if (!gameState || startingPlayAgain) return;
    startingPlayAgain = true;
    try {
      const seed = gameState.mapSeed ?? '';
      const { gameId: newId, playerToken: token, isWhite: white } = await createComputerGame({
        boardSize: mapSize,
        mapMode: seed.startsWith('r') ? 'r' : 'm',
        mapSeed: playAgainSameBoard ? seed : null,
        level: gameState.computerLevel,
        color: isWhite ? 'white' : 'black',
        biddingEnabled: gameState.biddingEnabled,
      });
      localStorage.setItem(`token_${newId}`, token);
      localStorage.setItem(`color_${newId}`, white ? 'white' : 'black');
      navigateTo(`/game/${newId}`);
    } catch (e) {
      statusMsg = String(e.message ?? e);
    } finally {
      startingPlayAgain = false;
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

  /**
   * premove = null | { type:'move', fromX, fromY, toX, toY }
   *                | { type:'place', toX, toY, code }
   */
  let premove = $state(null);

  // ── Replay & notation hover ──────────────────────────────────────────────

  let stateHistory  = [];          // GameStateDto snapshots; index = move count
  let replayIndex   = $state(null); // null = live; 0..n = replay
  let hoverSquares  = $state([]);   // squares to highlight from notation hover

  // ── Variation analysis tree (built when game ends) ───────────────────────
  let moveTree     = $state(null);  // MoveNode root; non-null when game is over
  let currentNode  = $state(null);  // active node in tree; null = use replayIndex
  let treeRevision = $state(0);     // bumped after each analysis move to force MoveList re-derive

  const displayedState = $derived(
    currentNode?.boardState
    ?? (replayIndex !== null ? stateHistory[replayIndex] : undefined)
    ?? gameState
  );

  // Navigation sounds: only when stepping exactly one move forward (not back, not jumps)
  function playNavSound(san) {
    if (san) playSound(/x/.test(san) ? 'capture' : 'move');
  }

  // Tree mode (finished games)
  function goToNode(node) {
    if (!node || node === currentNode) return;
    if (node.parent === currentNode) playNavSound(node.san);
    currentNode = node;
  }

  function navigate(dir) {
    const before   = replayIndex;
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

    const shown = replayIndex ?? max;
    if (shown === (before ?? max) + 1) playNavSound(gameState?.moves?.[shown - 1]);
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
    'draw': 'Draw by agreement',
    'abandoned': 'Game abandoned',
  };
  function formatResult(r) { return RESULT_LABELS[r] ?? 'Game over'; }

  function shopImgSrc(code) {
    return pieceSrc(SHOP.find(s => s.code === code)?.name ?? 'Pawn', isWhite);
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

  // Treasure is sent as a (black) piece, but it is never selectable.
  const isOwnPiece = (sq, white) => !!sq?.piece && sq.piece.type !== 'Treasure' && sq.piece.isWhite === white;

  async function onPieceGrabbed(x, y, clientX, clientY) {
    if (gameState?.gameEnded || biddingState) return;
    const sq = gameState.squares.find(s => s.x === x && s.y === y);
    if (!isOwnPiece(sq, isWhite)) return;

    selectedSquare = { x, y };

    // Premove mode: don't fetch legal moves on opponent's turn
    if (!isMyTurn) {
      legalDests = [];
    } else {
      legalDests = await fetchLegalMoves(x, y);
    }

    const imgSrc = pieceSrc(sq.piece.type, isWhite);
    dragging = { type: 'move', fromX: x, fromY: y, imgSrc, ghostX: clientX, ghostY: clientY };
  }

  function onDropOnBoard(toX, toY) {
    if (!dragging) return;

    if (isMyTurn) {
      // Clear premove if making manual move
      if (premove) {
        premove = null;
        hub?.invoke('ClearPremove', playerToken);
      }

      // Normal move execution
      if (dragging.type === 'move') {
        const isSameSquare = toX === dragging.fromX && toY === dragging.fromY;
        const destSq = gameState.squares.find(s => s.x === toX && s.y === toY);
        const isCapture = destSq?.piece != null && !isSameSquare;
        hub?.invoke('MakeMove', playerToken, dragging.fromX, dragging.fromY, toX, toY);
        playSound(isCapture ? 'capture' : 'move');
      } else if (dragging.type === 'place') {
        hub?.invoke('PlacePiece', playerToken, toX, toY, dragging.code);
        playSound('move');
      }
    } else {
      // Premove mode: send to server
      if (dragging.type === 'move') {
        premove = { type: 'move', fromX: dragging.fromX, fromY: dragging.fromY, toX, toY };
        hub?.invoke('SetPremove', playerToken, 'move', dragging.fromX, dragging.fromY, toX, toY, null);
      } else if (dragging.type === 'place') {
        premove = { type: 'place', toX, toY, code: dragging.code };
        hub?.invoke('SetPremove', playerToken, 'place', -1, -1, toX, toY, dragging.code);
      }
    }

    clearDrag();
  }

  // ── Analysis mode board callbacks (game over, variation entry) ───────────

  function onPieceGrabbedAnalysis(x, y, clientX, clientY) {
    const state = displayedState;
    if (!state) return;
    const sq = state.squares.find(s => s.x === x && s.y === y);
    if (!isOwnPiece(sq, state.white.isActive)) return; // empty, treasure, or wrong side's turn

    selectedSquare = { x, y };
    legalDests = computeLegalMoves(sq, state.squares, null);

    const imgSrc = pieceSrc(sq.piece.type, sq.piece.isWhite);
    dragging = { type: 'move', fromX: x, fromY: y, imgSrc, ghostX: clientX, ghostY: clientY };
  }

  function onDropOnBoardAnalysis(toX, toY) {
    if (!dragging) { clearDrag(); return; }
    const state = displayedState;
    if (!state) { clearDrag(); return; }

    if (dragging.type === 'move') {
      const isLegal = legalDests.some(d => d.x === toX && d.y === toY);
      if (!isLegal || (toX === dragging.fromX && toY === dragging.fromY)) { clearDrag(); return; }
      const toSq = state.squares.find(s => s.x === toX && s.y === toY);
      const isCapture = !!toSq?.piece;
      const san = moveToNotation(dragging.fromX, dragging.fromY, toX, toY, isCapture);
      const newState = applyMoveToState(state, dragging.fromX, dragging.fromY, toX, toY);
      currentNode = addChild(currentNode, san, newState);
      treeRevision++;
      playSound(isCapture ? 'capture' : 'move');
    } else if (dragging.type === 'place') {
      const isLegal = legalDests.some(d => d.x === toX && d.y === toY);
      if (!isLegal) { clearDrag(); return; }
      const item = SHOP.find(s => s.code === dragging.code);
      if (!item) { clearDrag(); return; }
      const san = placementToNotation(toX, toY, item.name);
      const newState = applyPlacementToState(state, toX, toY, item.name, item.cost);
      currentNode = addChild(currentNode, san, newState);
      treeRevision++;
      playSound('move');
    }
    clearDrag();
  }

  function shopPointerDownAnalysis(e, code) {
    const state = displayedState;
    if (!state) return;
    const item = SHOP.find(s => s.code === code);
    if (!item) return;
    const activeGold = state.white.isActive ? state.white.gold : state.black.gold;
    if (activeGold < item.cost) return;
    e.preventDefault();
    const internalState = createBoardState(state.squares, state.white.gold, state.black.gold, state.white.isActive);
    const placements = getAllLegalNewPiecePlacements(internalState);
    legalDests = placements.filter(p => p.pieceType === item.name).map(p => p.toPos);
    const placingWhite = state.white.isActive;
    dragging = {
      type: 'place', code,
      imgSrc: pieceSrc(item.name, placingWhite),
      ghostX: e.clientX, ghostY: e.clientY,
    };
  }

  function onSquareClickAnalysis(x, y) {
    const state = displayedState;
    if (!state) return;
    const sq = state.squares.find(s => s.x === x && s.y === y);

    if (selectedSquare) {
      if (selectedSquare.x === x && selectedSquare.y === y) {
        selectedSquare = null; legalDests = []; return;
      }
      if (isOwnPiece(sq, state.white.isActive)) {
        // Reselect another piece (same side only)
        selectedSquare = { x, y };
        legalDests = computeLegalMoves(sq, state.squares, null);
        return;
      }
      // Click to move
      const isLegal = legalDests.some(d => d.x === x && d.y === y);
      if (isLegal) {
        const fromSq = state.squares.find(s => s.x === selectedSquare.x && s.y === selectedSquare.y);
        const isCapture = !!sq?.piece;
        const san = moveToNotation(selectedSquare.x, selectedSquare.y, x, y, isCapture);
        const newState = applyMoveToState(state, selectedSquare.x, selectedSquare.y, x, y);
        currentNode = addChild(currentNode, san, newState);
        treeRevision++;
        playSound(isCapture ? 'capture' : 'move');
      }
      selectedSquare = null; legalDests = [];
      return;
    }

    if (isOwnPiece(sq, state.white.isActive)) {
      selectedSquare = { x, y };
      legalDests = computeLegalMoves(sq, state.squares, null);
    }
  }

  function onSquareClick(x, y) {
    if (gameState?.gameEnded || biddingState) return;
    const sq = gameState.squares.find(s => s.x === x && s.y === y);

    if (selectedSquare) {
      if (selectedSquare.x === x && selectedSquare.y === y) {
        selectedSquare = null; legalDests = [];
        return;
      }
      if (isOwnPiece(sq, isWhite)) {
        selectedSquare = { x, y };
        if (!isMyTurn) {
          legalDests = [];
        } else {
          fetchLegalMoves(x, y).then(d => { legalDests = d; });
        }
        return;
      }

      // Clicking to move selected piece to different square
      if (isMyTurn) {
        // Clear premove if making manual move
        if (premove) {
          premove = null;
          hub?.invoke('ClearPremove', playerToken);
        }

        const isCapture = sq?.piece != null;
        hub?.invoke('MakeMove', playerToken, selectedSquare.x, selectedSquare.y, x, y);
        playSound(isCapture ? 'capture' : 'move');
        selectedSquare = null; legalDests = [];
      } else {
        // Premove mode: set premove
        premove = { type: 'move', fromX: selectedSquare.x, fromY: selectedSquare.y, toX: x, toY: y };
        hub?.invoke('SetPremove', playerToken, 'move', selectedSquare.x, selectedSquare.y, x, y, null);
        selectedSquare = null; legalDests = [];
      }
    } else {
      if (isOwnPiece(sq, isWhite)) {
        selectedSquare = { x, y };
        if (!isMyTurn) {
          legalDests = [];
        } else {
          fetchLegalMoves(x, y).then(d => { legalDests = d; });
        }
      }
    }
  }

  function onHoverSquare(sq) {
    if (dragging && !sameSquare(sq, dragOverSquare)) dragOverSquare = sq;
  }

  /**
   * After a state update (usually the opponent's move), keep a drag or a click-selection that is
   * in progress, so a premove being entered simply becomes a normal move when it's our turn.
   * Dropped only if the selected piece is gone (e.g. captured). Legal targets are refreshed.
   */
  function keepInteractionAfterUpdate() {
    const from = dragging?.type === 'move' ? { x: dragging.fromX, y: dragging.fromY }
               : !dragging ? selectedSquare : null;
    if (from) {
      if (!isOwnPiece(gameState.squares.find(s => s.x === from.x && s.y === from.y), isWhite)) { clearDrag(); return; }
      legalDests = [];
      if (isMyTurn) {
        fetchLegalMoves(from.x, from.y).then(d => {
          if (selectedSquare?.x === from.x && selectedSquare?.y === from.y) legalDests = d;
        });
      }
    } else if (dragging?.type === 'place') {
      const code = dragging.code;
      legalDests = [];
      if (isMyTurn) fetchLegalPlacements(code).then(d => { if (dragging?.code === code) legalDests = d; });
    } else {
      clearDrag();
    }
  }

  function clearDrag() {
    stopAutoScroll();
    dragging = null;
    dragOverSquare = null;
    selectedSquare = null;
    legalDests = [];
  }

  // ── Shop drag ─────────────────────────────────────────────────────────────

  async function shopPointerDown(e, code) {
    if (gameState?.gameEnded || biddingState) return;
    const item = SHOP.find(s => s.code === code);
    if (!item) return;

    // On our turn, check if we can afford it; on opponent's turn, allow premove even without gold
    if (isMyTurn && (me?.gold ?? 0) < item.cost) return;

    e.preventDefault();

    // Premove mode: don't fetch legal placements on opponent's turn
    if (!isMyTurn) {
      legalDests = [];
    } else {
      legalDests = await fetchLegalPlacements(code);
    }

    dragging = { type: 'place', code, imgSrc: shopImgSrc(code), ghostX: e.clientX, ghostY: e.clientY };
  }

  // ── Global pointer tracking ───────────────────────────────────────────────

  let autoScrollDirection = $state(0); // -1 = up, 0 = none, +1 = down
  let autoScrollRafId = null;

  function startAutoScroll() {
    if (autoScrollRafId !== null) return;

    function scroll() {
      if (autoScrollDirection !== 0 && dragging) {
        const SCROLL_SPEED = 8;
        window.scrollBy(0, autoScrollDirection * SCROLL_SPEED);
        autoScrollRafId = requestAnimationFrame(scroll);
      } else {
        autoScrollRafId = null;
      }
    }
    autoScrollRafId = requestAnimationFrame(scroll);
  }

  function stopAutoScroll() {
    if (autoScrollRafId !== null) {
      cancelAnimationFrame(autoScrollRafId);
      autoScrollRafId = null;
    }
    autoScrollDirection = 0;
  }

  function globalPointerMove(e) {
    if (!dragging) return;
    dragging = { ...dragging, ghostX: e.clientX, ghostY: e.clientY };
    // Only on a new square: each change redraws the board
    const over = globalCursorToBoard(e.clientX, e.clientY);
    if (!sameSquare(over, dragOverSquare)) dragOverSquare = over;

    // Auto-scroll when dragging near viewport edges (for mobile piece placement)
    const EDGE_THRESHOLD = 80;
    const distFromTop = e.clientY;
    const distFromBottom = window.innerHeight - e.clientY;

    const prevDirection = autoScrollDirection;
    if (distFromTop < EDGE_THRESHOLD) {
      autoScrollDirection = -1;
    } else if (distFromBottom < EDGE_THRESHOLD) {
      autoScrollDirection = 1;
    } else {
      autoScrollDirection = 0;
    }

    if (autoScrollDirection !== 0 && prevDirection === 0) {
      startAutoScroll();
    } else if (autoScrollDirection === 0 && prevDirection !== 0) {
      stopAutoScroll();
    }
  }

  function globalPointerUp(e) {
    if (!dragging) return;
    stopAutoScroll();
    if (dragging.type === 'place') {
      const sq = globalCursorToBoard(e.clientX, e.clientY);
      if (sq) {
        if (moveTree) {
          // Analysis mode — route through analysis handler (handles legality check)
          onDropOnBoardAnalysis(sq.x, sq.y);
          return;
        } else if (isMyTurn) {
          // Clear premove if making manual move
          if (premove) {
            premove = null;
            hub?.invoke('ClearPremove', playerToken);
          }
          hub?.invoke('PlacePiece', playerToken, sq.x, sq.y, dragging.code);
          playSound('move');
        } else {
          // Premove mode: set premove
          premove = { type: 'place', toX: sq.x, toY: sq.y, code: dragging.code };
          hub?.invoke('SetPremove', playerToken, 'place', -1, -1, sq.x, sq.y, dragging.code);
        }
      }
    }
    // Board piece drops are handled by onDropOnBoard; this catches drops outside the canvas
    clearDrag();
  }

  // ── SignalR ───────────────────────────────────────────────────────────────

  onMount(async () => {
    preloadClockSound();
    // Global event listeners and clock — needed for both replay and live paths.
    window.addEventListener('pointermove', globalPointerMove);
    window.addEventListener('pointerup',   globalPointerUp);
    const clockInterval = setInterval(tickClocks, 250);
    function teardown() {
      stopAutoScroll();
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
        moveTree = buildFromMainline(gameState.moves, stateHistory);
        currentNode = lastMainlineNode(moveTree);
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

    hub.on('GameStarted', async (state) => {
      computerSearchPly = -1;
      if (biddingState == null) {
        playSound('game_started')
      }
      // If bidding just resolved and we're the winner, capture the opponent's bid for acknowledgement
      if (biddingState?.revealedCreatorBid != null && isWhite) {
        const opponentBid = wasCreator
          ? biddingState.revealedJoinerBid
          : biddingState.revealedCreatorBid;
        pendingBidReveal = { opponentBid };
      }

      // If rejoining a game with moves, fetch full history for replay
      if (state.moves?.length > 0) {
        try {
          const r = await fetch(`/api/game/${gameId}/replay`);
          if (r.ok) {
            const { snapshots } = await r.json();
            if (snapshots?.length) {
              stateHistory = snapshots;
              gameState = state; biddingState = null; syncLocalTimes(state); statusMsg = ''; inviteUrl = '';
              drawOfferBy = state.drawOfferBy ?? null;
              return;
            }
          }
        } catch (e) {
          console.warn('Failed to fetch replay history:', e);
        }
      }

      stateHistory = [state];
      gameState = state; biddingState = null; syncLocalTimes(state); statusMsg = ''; inviteUrl = '';
      drawOfferBy = state.drawOfferBy ?? null;
    });
    hub.on('StateUpdated', (state) => {
      // One state per move (stateHistory[n] = after n moves); an update without a new move
      // (e.g. a clock sync) replaces the last one.
      stateHistory = state.moves.length > (gameState?.moves.length ?? -1)
        ? [...stateHistory, state]
        : [...stateHistory.slice(0, -1), state];

      // Clear premove on any state update (either executed or became invalid)
      premove = null;

      if (gameState.moves.length < state.moves.length && !isMyTurn) {
        // Opponent played a move - check if it was a capture by comparing piece counts
        const oldPieceCount = gameState.squares.filter(s => s.piece).length;
        const newPieceCount = state.squares.filter(s => s.piece).length;
        const isCapture = newPieceCount < oldPieceCount;
        playSound(isCapture ? 'capture' : 'move');
      }
      const myMove = gameState.moves.length < state.moves.length && isMyTurn;
      const lapsedOffer = drawOfferBy && !state.drawOfferBy;   // an offer lapses once its receiver moves
      gameState = state; syncLocalTimes(state); resignPending = false;
      if (myMove) clearDrawMsg(DRAW_MSG.declined, DRAW_MSG.computerDeclined);
      if (lapsedOffer && !isSpectator) {
        if (drawOfferBy === myColor) statusMsg = DRAW_MSG.declined;
        else clearDrawMsg(DRAW_MSG.offered);
      }
      drawOfferBy = state.drawOfferBy ?? null;
      // Return to live mode so the player can interact (their turn may have come up)
      if (replayIndex !== null) replayIndex = null;
      keepInteractionAfterUpdate();
    });
    hub.on('GameEnded', async (state) => {
      // Same bookkeeping as StateUpdated (a resign/timeout adds no move: replace the last state)
      let history = state.moves.length > (gameState?.moves.length ?? -1)
        ? [...stateHistory, state]
        : [...stateHistory.slice(0, -1), state];
      // History incomplete (joined mid-game, missed updates): rebuild it from the server's replay.
      // Fetched before any state is applied, so the switch to analysis mode happens in one render
      // (otherwise the shop disappears while awaiting and the board briefly resizes).
      if (history.length !== state.moves.length + 1) {
        const snapshots = await fetchFullHistory(state.moves.length + 1);
        if (snapshots) history = snapshots;
      }
      stateHistory = history;
      gameState = state; syncLocalTimes(state); drawOfferBy = null; resignPending = false;
      statusMsg = formatResult(state.result); clearDrag();
      localStorage.removeItem(`inviteUrl_${gameId}`);
      replayIndex = stateHistory.length - 1;  // enable replayer
      // Build variation tree from mainline
      moveTree = buildFromMainline(state.moves, stateHistory);
      currentNode = lastMainlineNode(moveTree);
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
    hub.on('RematchRequested', (requestedByWhite, wantsSameSeed) => {
      if (requestedByWhite === isWhite) {
        rematchRequested = true;
      } else {
        opponentRematchRequested = true;
        opponentWantsSameSeed = wantsSameSeed;
        sameSeedRematch = wantsSameSeed; // Pre-check the box if opponent wants same board
      }
    });
    hub.on('DrawOfferChanged', (by, declined) => {
      if (!isSpectator) {
        if (declined && drawOfferBy === myColor) statusMsg = DRAW_MSG.declined;
        else if (by && by !== myColor) statusMsg = DRAW_MSG.offered;
        else clearDrawMsg(DRAW_MSG.offered);
      }
      drawOfferBy = by;
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

  const myColor      = $derived(isWhite ? 'w' : 'b');
  const showGameControls = $derived(!!gameState && !gameState.gameEnded && !isSpectator && !biddingState);
  const canOfferDraw = $derived(
    !!gameState && !gameState.gameEnded && drawOfferBy == null && !computerDrawThinking
    && gameState.moves.length >= drawOfferedAtPly + 2);

  /** Offers a draw. Vs the computer, the engine decides right here: it accepts unless it's more
   *  than DRAW_DECLINE_ADVANTAGE gold ahead. */
  async function offerDraw() {
    if (!hub || !canOfferDraw) return;
    const s = gameState;
    drawOfferedAtPly = s.moves.length;
    if (!isComputerGame) {
      hub.invoke('OfferDraw', playerToken);
      return;
    }
    computerDrawThinking = true;
    try {
      computerEngine ??= new EngineClient();
      const position = $state.snapshot({ squares: s.squares, whiteGold: s.white.gold, blackGold: s.black.gold, whiteIsActive: s.white.isActive });
      const res = await computerEngine.call({ cmd: 'search', position, nodes: s.computerNodes });
      if (!gameState || gameState.gameEnded) return;
      const whiteAdvantage = res.mate != null ? Math.sign(res.mate) * 100 : res.score;   // gold, White's view
      const computerAdvantage = s.computerIsWhite ? whiteAdvantage : -whiteAdvantage;
      if (computerAdvantage > DRAW_DECLINE_ADVANTAGE) statusMsg = DRAW_MSG.computerDeclined;
      else await hub.invoke('AgreeComputerDraw', playerToken);
    } catch (e) {
      if (e?.message !== 'cancelled') statusMsg = `Computer error: ${e?.message ?? e}`;
    } finally {
      computerDrawThinking = false;
    }
  }

  function answerDraw(accept) {
    if (!hub || drawOfferBy == null || drawOfferBy === myColor) return;
    clearDrawMsg(DRAW_MSG.offered);
    hub.invoke('AnswerDraw', playerToken, accept);
  }

  function requestRematch() {
    if (!hub || rematchRequested) return;
    rematchRequested = true;
    hub.invoke('RequestRematch', playerToken, sameSeedRematch);
  }

  /** Full state history from the server (one state per move); retries briefly because the result
   *  is stored just after the game-end broadcast. Returns null if it can't be loaded. */
  async function fetchFullHistory(expectedLength) {
    for (let attempt = 0; attempt < 5; attempt++) {
      try {
        const r = await fetch(`/api/game/${gameId}/replay`);
        if (r.ok) {
          const { snapshots } = await r.json();
          if (snapshots?.length === expectedLength) return snapshots;
        }
      } catch { /* retry */ }
      await new Promise(res => setTimeout(res, 400));
    }
    return null;
  }

  // ── My library: save the finished game with its variations (again = update the same entry) ──
  let libraryId     = $state(null);
  let librarySaving = $state(false);
  async function saveToLibrary() {
    if (!moveTree || librarySaving) return;
    librarySaving = true;
    const s = gameState;
    const entry = {
      title:    `${s.white.name} vs ${s.black.name}`,
      gameId,
      mapSize,
      mapSeed:  s.mapSeed ?? null,
      plyCount: mainlineLength(moveTree),
      result:   s.result ?? null,
      data:     packGameTree(moveTree),
    };
    try {
      if (libraryId) await updateLibraryGame(libraryId, entry);
      else libraryId = (await addLibraryGame(entry)).id;
      statusMsg = 'Saved to your library.';
    } catch (e) {
      statusMsg = `Could not save to library: ${e.message}`;
    } finally {
      librarySaving = false;
    }
  }

  function openAnalyzePage() {
    if (!moveTree) return;
    const json = JSON.stringify({
      format:      GAME_TRANSFER_FORMAT,
      mapSize,
      mapSeed:     gameState?.mapSeed ?? '',
      currentPath: currentNode ? getNodePath(moveTree, currentNode) : [],
      title:       gameState ? `${gameState.white.name} vs ${gameState.black.name}` : '',
      result:      gameState?.result ?? null,
      gameId,
      ...packGameTree(moveTree),
    });
    try {
      localStorage.setItem(GAME_TRANSFER_KEY, json);
    } catch {
      // Storage full: drop the analysis page's previous session and try once more
      try {
        localStorage.removeItem('analyze_board_state');
        localStorage.setItem(GAME_TRANSFER_KEY, json);
      } catch (e) {
        console.warn('[Analyze] could not store game for the analysis page', e);
        statusMsg = 'Could not open the analysis board: browser storage is full.';
        return;
      }
    }
    window.open('/analyze', '_blank');
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
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    if (e.target.closest?.('input, textarea, select, [contenteditable]')) return;   // chat, bid
    if (e.key === 'ArrowLeft')  { e.preventDefault(); stepMove(-1); return; }
    if (e.key === 'ArrowRight') { e.preventDefault(); stepMove(1); return; }
    if (currentNode !== null || !stateHistory.length) return;
    if (e.key === 'Home')       { e.preventDefault(); navigate('start'); }
    if (e.key === 'End')        { e.preventDefault(); navigate('end'); }
  }

  /** One move back (-1) or forward (+1): arrow keys and the mouse wheel over the board. */
  function stepMove(dir) {
    if (currentNode !== null) {
      // Tree mode (after the game)
      const target = dir < 0 ? currentNode.parent : currentNode.children[0];
      if (target) goToNode(target);
      return;
    }
    // Live game: replayIndex null = the current position; stepping back enters the replay
    if (stateHistory.length) navigate(dir < 0 ? 'prev' : 'next');
  }
</script>

<svelte:window onkeydown={handleKeydown} />

<!-- ✓ / ✗ pair for confirming resign or answering a draw offer -->
{#snippet confirmButtons(onYes, onNo, yesTitle, noTitle, danger = false)}
  <button class="action-btn icon-only" class:danger onclick={onYes} title={yesTitle} aria-label={yesTitle}>
    <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="m5 12.5 4.5 4.5L19 7.5"/></svg>
  </button>
  <button class="action-btn icon-only" onclick={onNo} title={noTitle} aria-label={noTitle}>
    <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M6.5 6.5l11 11M17.5 6.5l-11 11"/></svg>
  </button>
{/snippet}

<!-- Resign / draw controls: in the side panel on desktop, icon-only in the player bar on phones -->
{#snippet gameControls(compact)}
  {#if drawOfferBy && drawOfferBy !== myColor}
    {#if !compact}<span class="confirm-label">Draw offered</span>{/if}
    {@render confirmButtons(() => answerDraw(true), () => answerDraw(false), 'Accept the draw', 'Decline the draw')}
  {:else if resignPending}
    {@render confirmButtons(resign, () => resignPending = false, 'Confirm resignation', 'Cancel', true)}
  {:else}
    <button class="action-btn" class:icon-only={compact} onclick={() => resignPending = true} title="Resign" aria-label="Resign">
      <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M5 21V4m0 0h11l-2 4 2 4H5"/></svg>
      {#if !compact}Resign{/if}
    </button>
    <button class="action-btn" class:icon-only={compact} onclick={offerDraw} disabled={!canOfferDraw} aria-label="Offer a draw"
            title={drawOfferBy === myColor ? 'Draw offered — waiting for your opponent'
                 : computerDrawThinking ? 'The computer is considering your offer…'
                 : canOfferDraw ? 'Offer a draw' : 'You can offer a draw again after your next move'}>
      <!-- Handshake: Lucide icon (ISC) -->
      <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="m11 17 2 2a1 1 0 1 0 3-3"/><path d="m14 14 2.5 2.5a1 1 0 1 0 3-3l-3.88-3.88a3 3 0 0 0-4.24 0l-.88.88a1 1 0 1 1-3-3l2.81-2.81a5.79 5.79 0 0 1 7.06-.87l.47.28a2 2 0 0 0 1.42.25L21 4"/><path d="m21 3 1 11h-2"/><path d="M3 3 2 14l6.5 6.5a1 1 0 1 0 3-3"/><path d="M3 4h8"/></svg>
      {#if !compact}{drawOfferBy === myColor || computerDrawThinking ? 'Offered' : 'Draw'}{/if}
    </button>
  {/if}
{/snippet}

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
<div class="game-layout" style={layoutWidth ? `max-width: ${layoutWidth}px` : ''}>

  <!-- Opponent panel (top) -->
  <div class="player-panel" class:active={opponent?.isActive && !biddingState}>
    <span class="name">
      {opponent?.name ?? 'Waiting for opponent...'}
      {#if opponent?.elo != null}<span class="elo-label">({opponent.elo})</span>{/if}
      {#if opponent && !biddingState}<span class="color-label">{isWhite ? 'Black' : 'White'}</span>{/if}
      {#if isComputerGame && computerThinking && !gameState?.gameEnded}<span class="thinking-label">thinking…</span>{/if}
      {#if isComputerGame && computerBidding}<span class="thinking-label"><span class="spinner"></span>deciding on a bid…</span>{/if}
    </span>
    {#if opponent}
      <span class="gold">{opponent.gold} gold</span>
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
    {/if}
  </div>

  <!-- Board + move list side by side -->
  <div class="game-body" bind:this={gameBodyEl}>
    <div class="board-col">
      <div class="board-main" bind:clientHeight={boardColHeight}>
      <div class="board-wrap" bind:this={boardWrapEl} use:wheelNav={stepMove}>
        <!-- Status / invite banner: floats over the top of the board so it doesn't change the layout -->
        {#if (statusMsg && statusMsg !== statusDismissed) || inviteUrl}
          <div class="status-bar">
            {#if statusMsg}<span>{statusMsg}</span>{/if}
            {#if inviteUrl}
              <button class="copy-btn" onclick={copyInvite}>Copy invite link</button>
            {:else}
              <button class="status-close" onclick={() => statusDismissed = statusMsg} title="Close">✕</button>
            {/if}
          </div>
        {/if}
        {#if displayedState}
          <Board
            squares={displayedState.squares}
            selectedSquare={(!moveTree && replayIndex !== null) ? null : selectedSquare}
            legalDests={(!moveTree && replayIndex !== null) ? [] : legalDests}
            dragOverSquare={(!moveTree && replayIndex !== null) ? null : dragOverSquare}
            {hoverSquares}
            premoveSquares={replayIndex === null && premove
              ? (premove.type === 'move'
                  ? [{x: premove.fromX, y: premove.fromY}, {x: premove.toX, y: premove.toY}]
                  : [{x: premove.toX, y: premove.toY}])
              : []}
            {lastMoveSquares}
            showCoords={settings.showCoordinates}
            {mapSize}
            {isWhite}
            onPieceGrabbed={moveTree ? onPieceGrabbedAnalysis : (replayIndex !== null ? null : onPieceGrabbed)}
            onDropOnBoard={moveTree ? onDropOnBoardAnalysis  : (replayIndex !== null ? null : onDropOnBoard)}
            onHoverSquare={moveTree ? null                   : (replayIndex !== null ? null : onHoverSquare)}
            onSquareClick={moveTree ? onSquareClickAnalysis  : (replayIndex !== null ? null : onSquareClick)}
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
        {#if isComputerGame && computerBidding}
          <span class="bid-waiting"><span class="spinner"></span>Bid submitted — the computer is deciding on its bid…</span>
        {:else}
          <span class="bid-waiting">Bid submitted — waiting for opponent…</span>
        {/if}
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
            disabled={isMyTurn && !canAfford}
            title="{item.name} — {item.cost}g"
            onpointerdown={(e) => shopPointerDown(e, item.code)}
          >
            <img src={shopImgSrc(item.code)} alt={item.name} width="44" height="44" draggable="false" />
            <span class="cost" class:dim={!canAfford}>{item.cost}</span>
          </button>
        {/each}
      </div>
    </div>
  {:else if moveTree && displayedState}
    {@const analysisWhite = displayedState.white.isActive}
    {@const analysisGold  = analysisWhite ? displayedState.white.gold : displayedState.black.gold}
    <div class="controls">
      <div class="shop">
        {#each SHOP as item}
          {@const canAfford = analysisGold >= item.cost}
          <button
            class="shop-piece"
            class:active={dragging?.type === 'place' && dragging.code === item.code}
            disabled={!canAfford}
            title="{item.name} — {item.cost}g"
            onpointerdown={(e) => shopPointerDownAnalysis(e, item.code)}
          >
            <img src={pieceSrc(item.name, analysisWhite)} alt={item.name} width="44" height="44" draggable="false" />
            <span class="cost" class:dim={!canAfford}>{item.cost}</span>
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
          {#if !isComputerGame}
            <button class="tab" class:active={activeTab === 'chat'} onclick={() => switchTab('chat')}>
              Chat
              {#if unreadChat > 0}<span class="badge">{unreadChat}</span>{/if}
            </button>
          {/if}
        </div>
        <div class="panel-main">
        {#if activeTab === 'moves'}
          {#if moveTree}
            <MoveList
              rootNode={moveTree}
              activeNode={currentNode}
              {treeRevision}
              replayMode={true}
              onHoverMove={handleHoverMove}
              onNavigate={goToNode}
            />
          {:else}
            <MoveList
              moves={gameState?.moves ?? []}
              activeIndex={replayIndex !== null ? replayIndex - 1 : (gameState?.moves?.length ?? 0) - 1}
              replayMode={true}
              onHoverMove={handleHoverMove}
              onNavigate={navigate}
            />
          {/if}
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
        </div><!-- /.panel-main -->

        <!-- Game actions: resign during the game, rematch after it; analyze / library at the bottom -->
        <div class="panel-actions">
          <div class="actions-top">
            {#if showGameControls}
              <!-- On phones these move into the player bar (see .bar-controls) -->
              <div class="game-controls">{@render gameControls(false)}</div>
            {:else if gameState?.gameEnded && !isSpectator && isComputerGame}
              <div class="rematch-bar">
                <label class="rematch-option">
                  <input type="checkbox" bind:checked={playAgainSameBoard} />
                  <span>Same board</span>
                </label>
                <button class="action-btn rematch-btn" onclick={playAgainVsComputer} disabled={startingPlayAgain}>
                  {startingPlayAgain ? 'Starting…' : 'Rematch'}
                </button>
              </div>
            {:else if gameState?.gameEnded && !isSpectator}
              <div class="rematch-bar">
                {#if opponentRematchRequested && !rematchRequested}
                  <label class="rematch-option">
                    <input type="checkbox" bind:checked={sameSeedRematch} />
                    <span>Same board</span>
                  </label>
                  <button class="action-btn rematch-btn accept" onclick={requestRematch}>Accept Rematch</button>
                {:else if rematchRequested}
                  <span class="rematch-waiting">Rematch requested — waiting for opponent…</span>
                {:else}
                  <label class="rematch-option">
                    <input type="checkbox" bind:checked={sameSeedRematch} />
                    <span>Same board</span>
                  </label>
                  <button class="action-btn rematch-btn" onclick={requestRematch}>Rematch</button>
                {/if}
              </div>
            {/if}
          </div>
          <!-- Available once the game is over (the analysis tree exists) -->
          <div class="actions-bottom">
            <button class="action-btn" onclick={openAnalyzePage} disabled={!moveTree}
                    title={moveTree ? 'Open in the analysis board' : 'Available after the game'}>
              <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><circle cx="10.5" cy="10.5" r="6"/><path d="m15 15 5.5 5.5"/></svg>
              Analyze
            </button>
            {#if authState.token}
              <button class="action-btn" onclick={saveToLibrary} disabled={!moveTree || librarySaving}
                      title={!moveTree ? 'Available after the game'
                           : libraryId ? 'Save variations to your library' : 'Add to your library'}>
                {#if libraryId}
                  <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="m5 12.5 4.5 4.5L19 7.5"/></svg>
                  Saved
                {:else}
                  <svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M6.5 3.5h11v17l-5.5-4-5.5 4z"/></svg>
                  Save
                {/if}
              </button>
            {/if}
          </div>
        </div>
      </div>
    {:else}
      <!-- Empty until the game starts, so the board placeholder gets the same size as the board -->
      <div class="move-list-col"></div>
    {/if}

  <!-- My panel — inside game-body so CSS order can place it above the move list on mobile -->
  <div class="player-panel me" class:active={me?.isActive && !biddingState}>
    {#if me}
      <span class="name">
        {me.name}
        {#if me.elo != null}<span class="elo-label">({me.elo})</span>{/if}
        {#if !biddingState}
          <span class="color-label">{isWhite ? 'White' : 'Black'}</span>
        {/if}
      </span>
      {#if showGameControls}
        <span class="bar-controls">{@render gameControls(true)}</span>
      {/if}
      <span class="gold">{me.gold} gold</span>
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

</div><!-- /.game-layout -->
</div><!-- /.page -->

<style>
  .thinking-label { font-size: 0.8rem; color: var(--muted); font-style: italic; margin-left: 0.4rem; display: inline-flex; align-items: center; gap: 0.35rem; }
  .spinner {
    display: inline-block; width: 0.85em; height: 0.85em; flex-shrink: 0;
    border: 2px solid var(--line-strong); border-top-color: var(--accent); border-radius: 50%;
    animation: spin 0.8s linear infinite; vertical-align: middle; margin-right: 0.35rem;
  }
  @keyframes spin { to { transform: rotate(360deg); } }
  /* ── Drag ghost ── */
  .drag-ghost {
    position: fixed;
    width: 64px; height: 64px;
    transform: translate(-50%, -50%);
    pointer-events: none;
    z-index: 1000;
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
    flex: 1 1 0;   /* not auto: the canvas's pixel width must not push the move list onto a new row */
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
    background: var(--surface);
    border: 1px solid var(--line);
    border-radius: 6px;
    overflow: hidden;
    flex: 0 0 280px;
  }

  /* ── Tabs ── */
  .tab-bar {
    display: flex;
    border-bottom: 1px solid var(--line);
    flex-shrink: 0;
  }
  .tab {
    flex: 1;
    padding: 0.45rem 0.5rem;
    background: none;
    border: none;
    border-bottom: 2px solid transparent;
    color: var(--muted);
    font-size: 0.82rem;
    font-weight: 600;
    letter-spacing: 0.04em;
    cursor: pointer;
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.35rem;
  }
  .tab:hover { color: var(--fg-2); }
  .tab.active { color: #fff; border-bottom-color: var(--accent); }
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
    font-size: 0.76rem;
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
    color: var(--accent);
    font-weight: 600;
    white-space: nowrap;
    flex-shrink: 0;
  }
  .chat-text { color: var(--fg); word-break: break-word; }
  .chat-empty { color: var(--faint); font-style: italic; padding: 0.25rem 0; }
  .chat-input-row {
    display: flex;
    gap: 0.4rem;
    padding: 0.5rem 0.6rem;
    border-top: 1px solid var(--line);
    flex-shrink: 0;
  }
  .chat-input {
    flex: 1;
    min-width: 0;
    padding: 0.3rem 0.5rem;
    background: var(--surface-2);
    border: 1px solid var(--line-strong);
    border-radius: 4px;
    color: var(--fg);
    font-size: 0.85rem;
  }
  .chat-input:focus { outline: none; border-color: var(--accent); }
  .chat-send-btn {
    padding: 0.3rem 0.7rem;
    background: var(--button);
    color: #fff;
    border: none;
    border-radius: 4px;
    font-size: 0.82rem;
    white-space: nowrap;
  }
  .chat-send-btn:hover { background: var(--button-hover); }

  /* ── Player panels ── */
  .player-panel {
    display: flex;
    align-items: center;
    gap: 0.7rem;
    width: 100%;
    height: 3rem;
    padding: 0 1rem;
    background: var(--surface);
    border-radius: 6px;
    font-size: 1rem;
    border: 2px solid transparent;
    transition: border-color 0.2s, box-shadow 0.2s, background 0.2s;
  }
  .player-panel.me { flex: 0 0 100%; }
  .player-panel.active {
    background: var(--surface-3);
    border-color: var(--accent);
    box-shadow: 0 0 12px rgba(216, 166, 74, 0.3);
  }

  .dot { width: 10px; height: 10px; border-radius: 50%; background: var(--line-strong); flex-shrink: 0; }
  .dot.connected { background: #7cfc00; }
  .name { font-weight: 600; flex: 1; min-width: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .muted { opacity: 0.45; }
  .elo-label   { font-weight: 400; font-size: 0.82rem; color: var(--muted); margin-left: 0.2rem; }
  .color-label { font-weight: 400; font-size: 0.8rem; color: var(--muted); margin-left: 0.3rem; }
  .gold { color: #f5c518; font-size: 0.9rem; }

  .clock {
    font-variant-numeric: tabular-nums;
    font-size: 1rem;
    background: var(--line);
    padding: 0.15rem 0.5rem;
    border-radius: 4px;
  }
  .clock.ticking { background: var(--button); color: #fff; }

  /* ── Status bar ── */
  .status-bar {
    position: absolute; top: 0.75rem; left: 50%; transform: translateX(-50%);
    z-index: 5;
    display: flex; align-items: center; gap: 0.75rem;
    max-width: calc(100% - 1.5rem);
    padding: 0.55rem 0.6rem 0.55rem 1rem;
    background: rgba(28, 24, 18, 0.94); border: 1px solid var(--accent); border-radius: 6px;
    box-shadow: 0 4px 16px rgba(0, 0, 0, 0.5);
    font-size: 0.95rem;
  }
  .status-close {
    background: none; border: none; color: var(--muted);
    font-size: 0.85rem; padding: 0.1rem 0.3rem; line-height: 1; cursor: pointer;
  }
  .status-close:hover { color: var(--fg); }
  .copy-btn {
    padding: 0.2rem 0.7rem; background: var(--button); color: #fff;
    border: none; border-radius: 4px; font-size: 0.82rem; white-space: nowrap;
  }

  /* ── Board ── */
  .board-wrap {
    position: relative;
    width: 100%;
    display: flex; justify-content: center;
  }
  .board-placeholder {
    width: 100%; aspect-ratio: 1;
    background: var(--surface); border-radius: 6px;
    display: flex; align-items: center; justify-content: center; color: var(--faint);
  }

  /* ── Controls ── */
  .controls {
    display: flex; align-items: center; gap: 0.6rem;
    width: 100%;
    padding: 0.6rem 0.85rem;
    background: var(--surface); border-radius: 6px;
  }
  .shop { display: flex; gap: 0.35rem; flex: 1; justify-content: center; }

  .shop-piece {
    display: flex; flex-direction: column; align-items: center;
    padding: 0.3rem 0.4rem;
    background: var(--surface-2); border: 2px solid transparent; border-radius: 8px;
    color: var(--fg); gap: 2px;
    transition: border-color 0.1s, background 0.1s;
    touch-action: none; user-select: none;
  }
  .shop-piece:not(:disabled):hover { border-color: var(--accent); background: var(--surface-3); }
  .shop-piece.active { border-color: #f5c518; background: #3a3a20; }
  .shop-piece:disabled { opacity: 0.3; cursor: default; }


  .cost { font-size: 0.85rem; font-weight: 700; color: #f5c518; font-variant-numeric: tabular-nums; }
  .cost.dim { color: var(--faint); }

  /* ── Side panel: move list / chat (~80%) above the game actions (~20%) ── */
  .panel-main {
    display: flex;
    flex-direction: column;
    flex: 4 1 0;
    min-height: 0;
  }
  .panel-actions {
    display: flex;
    flex-direction: column;
    justify-content: space-between;
    gap: 0.6rem;
    flex: 1 1 0;
    min-height: 7.5rem;
    padding: 0.75rem 0.6rem;
    border-top: 1px solid var(--line);
  }
  .actions-top, .actions-bottom {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    min-height: 2.2rem;   /* fixed: confirm / rematch controls must not change the layout */
  }
  .game-controls { display: flex; align-items: center; gap: 0.5rem; }
  .confirm-label { font-size: 0.9rem; line-height: 1; color: var(--fg-2); margin-right: 0.5rem; }
  .action-btn {
    display: inline-flex;
    align-items: center;
    gap: 0.45rem;
    padding: 0.4rem 0.9rem;
    background: var(--surface-2);
    border: 1px solid var(--line-strong);
    border-radius: 6px;
    color: var(--fg-2);
    font-size: 0.88rem;
    font-weight: 600;
    white-space: nowrap;
    cursor: pointer;
    transition: border-color 0.15s, background 0.15s, color 0.15s;
  }
  .action-btn:not(:disabled):hover { background: var(--surface-3); border-color: var(--accent); color: var(--fg); }
  .action-btn:disabled { opacity: 0.4; cursor: default; }
  .action-btn.danger:not(:disabled):hover { border-color: #b04040; }
  .action-btn.icon-only { padding: 0.4rem 1.1rem; }
  .action-btn.icon-only .icon { width: 1.25em; height: 1.25em; }
  .icon {
    width: 1.1em; height: 1.1em; flex-shrink: 0;
    fill: none; stroke: currentColor; stroke-width: 2;
    stroke-linecap: round; stroke-linejoin: round;
  }

  /* ── Bidding help tooltip ── */
  .bid-help {
    position: relative;
    display: inline-flex;
    align-items: center;
  }
  .bid-help-btn {
    width: 1.35em; height: 1.35em;
    border-radius: 50%;
    background: var(--surface-2);
    border: 1px solid var(--faint);
    color: var(--accent);
    font-size: 0.79rem;
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
    background: var(--bg);
    border: 1px solid var(--line-strong);
    border-radius: 6px;
    padding: 0.6rem 0.75rem;
    font-size: 0.8rem;
    color: var(--muted);
    line-height: 1.55;
    z-index: 100;
    pointer-events: none;
    text-align: left;
  }
  .bid-help-tip strong { color: var(--fg-2); }
  .bid-help:hover .bid-help-tip,
  .bid-help:focus-within .bid-help-tip { display: block; }

  /* ── Bidding controls bar ── */
  .bid-controls { justify-content: center; gap: 0.6rem; flex-wrap: wrap; }
  .bid-label { font-size: 0.9rem; color: var(--fg-2); }
  .bid-label strong { color: #f5c518; }
  .bid-input {
    width: 90px; padding: 0.35rem 0.5rem;
    background: var(--surface-2); border: 2px solid var(--faint); border-radius: 6px;
    color: var(--fg); font-size: 1rem; text-align: center;
  }
  .bid-input:focus { outline: none; border-color: var(--accent); }
  .bid-submit-btn {
    padding: 0.35rem 1rem; background: var(--button); color: #fff;
    border: none; border-radius: 6px; font-size: 0.9rem;
  }
  .bid-submit-btn:hover { background: var(--button-hover); }
  .bid-waiting { font-size: 0.85rem; color: var(--muted); font-style: italic; }

  /* ── Bid check / clock ── */
  .bid-check { font-size: 0.9rem; color: #7cfc00; flex-shrink: 0; }

  /* ── Rematch bar ── */
  .rematch-bar {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex: 1;
    min-width: 0;
    padding: 0.2rem 0.3rem;
  }
  .rematch-option {
    display: flex;
    align-items: center;
    gap: 0.35rem;
    font-size: 0.85rem;
    color: var(--muted);
    cursor: pointer;
    user-select: none;
    white-space: nowrap;
  }
  /* Compact switch: the footer is a fixed-width column and must stay on one row */
  .rematch-option input[type="checkbox"] { font-size: 0.8em; }
  .rematch-btn { margin-left: auto; }   /* looks: .action-btn */
  .rematch-btn.accept {
    background: var(--button);
    border-color: var(--button);
    color: #fff;
  }
  .rematch-btn.accept:hover { background: var(--button-hover); border-color: var(--button-hover); color: #fff; }
  .rematch-waiting { font-size: 0.85rem; color: var(--muted); font-style: italic; text-align: center; }

  /* Resign / draw in the player bar: phones only */
  .bar-controls { display: none; align-items: center; gap: 0.35rem; flex-shrink: 0; }
  .bar-controls .action-btn.icon-only { padding: 0.3rem 0.6rem; }

  /* ── Phones ── (last, so these override the rules above) */
  @media (max-width: 700px) {
    .game-body { flex-direction: column; flex-wrap: nowrap; }
    .board-col    { order: 1; flex: none; }
    .player-panel.me { order: 2; width: 100%; flex: none; }
    .move-list-col { order: 3; flex: none; max-height: 360px; width: 100%; }
    .panel-main    { flex: 1 1 auto; }
    .panel-actions { flex: none; min-height: 0; }
    /* Resign / draw live in the player bar; the side panel keeps rematch + analyze / save */
    .bar-controls { display: flex; }
    .actions-top:has(.game-controls) { display: none; }
    .player-panel { gap: 0.5rem; padding: 0 0.75rem; }
    /* Shop straight on the page background, smaller pieces */
    .controls { padding: 0; background: none; }
    .shop { gap: 0.3rem; }
    .shop-piece { flex: 1; padding: 0.15rem 0.2rem; }
    .shop-piece img { width: 36px; height: 36px; }
  }
</style>
