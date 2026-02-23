<script>
  import { onMount, onDestroy } from 'svelte';
  import Board from './Board.svelte';
  import { currentGame } from './lib/currentGame.svelte.js';
  import { generateSandboxBoard } from './lib/api.js';
  import { playSound } from './lib/sound.js';
  import { computeLegalMoves, isRealPiece, basePieceType, isRocks } from './lib/pathFinder.js';
  import { getBestMove } from './lib/analysis.js'
  import { SHOP } from './lib/shop.js'
  import AnalysisWorker from './lib/analysisWorker.js?worker';
  import { settings, persistSettings } from './lib/settings.svelte.js';

  function pieceImgSrc(type, isWhite) {
    if (type === 'Treasure') return '/assets/objects/treasure.png';
    if (type === 'WhiteFlag' || type === 'BlackFlag') return '/assets/objects/white_flag.png';
    const n = type.replace(/^(White|Black)/, '').toLowerCase();
    return `/assets/objects/${isWhite ? 'w' : 'b'}_${n}.png`;
  }

  // ── Board state ─────────────────────────────────────────────────────────────
  let squares       = $state([]);
  let mapSize       = $state(0);
  let mapSeed       = $state('');
  let whiteGold     = $state(0);
  let blackGold     = $state(5);
  let whiteIsActive = $state(true);
  let viewAsWhite   = $state(true);

  // ── Selection / drag ────────────────────────────────────────────────────────
  let boardCanvas   = null;
  let selectedSqPos = $state(null);
  let legalDests    = $state([]);
  let dragOverSq    = $state(null);
  let lastMoveFrom  = $state(null);
  let lastMoveTo    = $state(null);
  let hoverSquares  = $state([]);

  /**
   * dragging = null
   *          | { type: 'move',  fromX, fromY, imgSrc, ghostX, ghostY }
   *          | { type: 'place', pieceType, isWhitePiece, imgSrc, ghostX, ghostY }
   */
  let dragging = $state(null);

  // ── Undo / redo ─────────────────────────────────────────────────────────────
  let undoStack = $state([]);
  let redoStack = $state([]);
  const canUndo = $derived(undoStack.length > 0);
  const canRedo = $derived(redoStack.length > 0);

  // ── Map generation controls ─────────────────────────────────────────────────
  let genSize    = $state(settings.analyzeMapSize);
  let genRandom  = $state(settings.analyzeFullRandom);
  let seedInput  = $state('');
  let generating = $state(false);
  let genError   = $state('');

  // Persist map settings when changed
  $effect(() => {
    settings.analyzeMapSize = genSize;
    settings.analyzeFullRandom = genRandom;
    persistSettings();
  });

  const seedInputError = $derived.by(() => {
  const s = seedInput.trim();
    if (!s) return null;
    if (!s.match(/^[mr]_\d+x\d+_[0-9kmrft]+$/)) return 'Invalid seed format';
    return null;
  });
  const seedInputValid = $derived(!seedInput.trim() || seedInputError === null);

  // ── Analysis ────────────────────────────────────────────────────────────────
  let analyzing      = $state(false);
  let thinking       = $state(false);
  let analysisWorker = $state(null);
  let analysisResults = $state([]);
  let analysisDepth = $state(0);
  // let depthTimings = $state([]);

  function startWorker() {
    if (!analysisWorker) {
      analysisWorker = new AnalysisWorker();

      analysisWorker.onmessage = (event) => {
        const { type, depth, topMoves /*, timeMs*/ } = event.data;

        if (type === 'depth-result') {
          analysisDepth = depth;
          analysisResults = topMoves;
          // depthTimings = [...depthTimings, { depth, timeMs }];
        }
      };
    }

    // depthTimings = [];
    analysisWorker.postMessage({
      type: 'start',
      payload: getCurrentPosition()
    });
  }

  function getCurrentPosition() {
    return {
      squares: squares.map(s => ({
        x: s.x,
        y: s.y,
        type: s.type,
        piece: s.piece
          ? { type: s.piece.type, isWhite: s.piece.isWhite }
          : null,
        mineOwner: s.mineOwner || null
      })),
      whiteGold,
      blackGold,
      whiteIsActive
    };
  }

  function toggleAnalysis() {
    analyzing = !analyzing;

    if (analyzing) {
      continuousAnalysis();
    } else {
      stopAnalysis();
    }
  }

  function continuousAnalysis() {
    startWorker();
  }

  function stopAnalysis() {
    analysisWorker?.postMessage({ type: 'stop' });
  }

  async function botMove() {
    thinking = true;
    try {
      let maxPlayouts = 3_000_000;
      maxPlayouts = maxPlayouts / (squares.length * 1/64); // Stays the same on 8x8 boards. 

      let bestMove = getBestMove(squares, whiteGold, blackGold, whiteIsActive, maxPlayouts);
      if (bestMove.type == "m") {
        applyMove(bestMove.fromPos, bestMove.toPos);
      }
      else {
        placePieceAt(bestMove.toPos, bestMove.pieceType, whiteIsActive);
      }
    } catch (e) {
      genError = e.message ?? String(e);
    } finally {
      thinking = false;
    }
  }

  function toNotation(rawMove) {
    const move = JSON.parse(JSON.stringify(rawMove)).move;

    function xToLetter(x) {
      return String.fromCharCode(97 + x); // 0 -> 'a'
    }

    // Piece placement
    if (move.type === "p") {
      const pieceMap = {
        Pawn: "p",
        Queen: "Q",
        Rook: "R",
        Bishop: "B",
        Knight: "N",
        // Add more if needed
      };

      const pieceDisplay = pieceMap[move.pieceType] || move.pieceType[0].toUpperCase();
      const file = xToLetter(move.toPos.x);
      const rank = move.toPos.y + 1;

      return `${file}${rank}=${pieceDisplay}`;
    }

    // Normal move
    if (move.type === "m") {
      const fromFile = xToLetter(move.fromPos.x);
      const fromRank = move.fromPos.y + 1;
      const toFile = xToLetter(move.toPos.x);
      const toRank = move.toPos.y + 1;

      return `${fromFile}${fromRank}-${toFile}${toRank}`;
    }

    throw new Error("Invalid move type");
  }

  function parseNotation(notation) {
    function letterToX(c) {
      return c.charCodeAt(0) - 97; // 'a' -> 0
    }

    // Placement: "a1=Q"
    const place = notation.match(/^([a-z])(\d+)=/);
    if (place) {
      return {
        to: { x: letterToX(place[1]), y: parseInt(place[2]) - 1 }
      };
    }

    // Move: "a1-b2"
    const move = notation.match(/^([a-z])(\d+)-([a-z])(\d+)$/);
    if (move) {
      return {
        from: { x: letterToX(move[1]), y: parseInt(move[2]) - 1 },
        to: { x: letterToX(move[3]), y: parseInt(move[4]) - 1 }
      };
    }

    return null;
  }

  function handleAnalysisMoveHover(move) {
    if (!move) {
      hoverSquares = [];
      return;
    }

    const notation = toNotation(move);
    const parsed = parseNotation(notation);
    if (!parsed) {
      hoverSquares = [];
      return;
    }

    const result = [];
    if (parsed.from) result.push(sqAt(parsed.from));
    if (parsed.to) result.push(sqAt(parsed.to));
    hoverSquares = result.filter(Boolean);
  }

  // ── Live square lookups ─────────────────────────────────────────────────────
  function sqAt(pos) {
    if (!pos) return null;
    return squares.find(s => s.x === pos.x && s.y === pos.y) ?? null;
  }

  const selectedSquare  = $derived(sqAt(selectedSqPos));
  const lastMoveSquares = $derived.by(() => {
    const res = [];
    const f = sqAt(lastMoveFrom); if (f) res.push(f);
    const t = sqAt(lastMoveTo);   if (t) res.push(t);
    return res;
  });

  // ── NavBar seed integration ─────────────────────────────────────────────────
  $effect(() => { currentGame.mapSeed = mapSeed || null; });
  onDestroy(() => { currentGame.mapSeed = null; });

  // ── Snapshot helpers ────────────────────────────────────────────────────────
  function snapshot() {
    return {
      squares: squares.map(s => ({ ...s })),
      whiteGold, blackGold, whiteIsActive,
      lastMoveFrom, lastMoveTo,
    };
  }

  function pushHistory() {
    undoStack = [...undoStack, snapshot()];
    redoStack = [];
  }

  function clearSelection() {
    selectedSqPos = null;
    legalDests    = [];
    dragOverSq    = null;
  }

  function restoreSnapshot(snap) {
    squares       = snap.squares;
    whiteGold     = snap.whiteGold;
    blackGold     = snap.blackGold;
    whiteIsActive = snap.whiteIsActive;
    lastMoveFrom  = snap.lastMoveFrom;
    lastMoveTo    = snap.lastMoveTo;
    clearSelection();
  }

  function undo() {
    if (!undoStack.length) return;
    redoStack = [...redoStack, snapshot()];
    restoreSnapshot(undoStack[undoStack.length - 1]);
    undoStack = undoStack.slice(0, -1);

    if (analyzing && analysisWorker) {
      analysisResults = [];  // Clear old results
      // depthTimings = [];
      analysisWorker.postMessage({
        type: 'update',
        payload: getCurrentPosition()
      });
    }
  }

  function redo() {
    if (!redoStack.length) return;
    undoStack = [...undoStack, snapshot()];
    restoreSnapshot(redoStack[redoStack.length - 1]);
    redoStack = redoStack.slice(0, -1);

    if (analyzing && analysisWorker) {
      analysisResults = [];  // Clear old results
      // depthTimings = [];
      analysisWorker.postMessage({
        type: 'update',
        payload: getCurrentPosition()
      });
    }
  }

  // ── Map generation ──────────────────────────────────────────────────────────
  async function generateMap() {
    if (!seedInputValid) return;
    genError = '';
    generating = true;
    try {
      const seed = seedInput.trim() || null;
      const data = await generateSandboxBoard(genSize, genRandom, seed);
      squares       = data.squares.map(s => ({ ...s, mineOwner: null }));
      mapSize       = data.mapSize;
      mapSeed       = data.mapSeed;
      whiteGold     = 0;
      blackGold     = 5;
      whiteIsActive = true;
      lastMoveFrom  = null;
      lastMoveTo    = null;
      dragging      = null;
      undoStack     = [];
      redoStack     = [];
      clearSelection();

      if (analyzing && analysisWorker) {
        analysisWorker.postMessage({
          type: 'update',
          payload: getCurrentPosition()
        });
      }
    } catch (e) {
      genError = e.message ?? String(e);
    } finally {
      generating = false;
    }
  }

  function clearPieces() {
    if (!squares.length) return;
    pushHistory();
    squares = squares.map(s => ({ ...s, piece: null }));
    clearSelection();

    if (analyzing && analysisWorker) {
      analysisWorker.postMessage({
        type: 'update',
        payload: getCurrentPosition()
      });
    }
  }

  // ── Turn management ─────────────────────────────────────────────────────────
  function passTurn() {
    pushHistory();
    const nextWhite = !whiteIsActive;
    const income = squares
      .filter(s => s.type?.includes('Mine') && s.mineOwner === (nextWhite ? 'white' : 'black'))
      .length * 5;
    if (nextWhite) whiteGold = Math.max(0, whiteGold + income);
    else           blackGold = Math.max(0, blackGold + income);
    whiteIsActive = nextWhite;
    clearSelection();

    if (analyzing && analysisWorker) {
      analysisWorker.postMessage({
        type: 'update',
        payload: getCurrentPosition()
      });
    }
  }

  // ── Move logic ───────────────────────────────────────────────────────────────
  function applyMove(fromPos, toPos) {
    const fromSq = sqAt(fromPos);
    const toSq   = sqAt(toPos);
    if (!fromSq?.piece || !isRealPiece(fromSq.piece)) return false;

    pushHistory();
    const piece = fromSq.piece;
    let wg = whiteGold, bg = blackGold;

    const isSameSquare = fromPos.x === toPos.x && fromPos.y === toPos.y;
    let isCapture = toSq?.piece != null && !isSameSquare;
    if (toSq?.piece) {
      if (toSq.piece.type == "Treasure") {
        if (piece.isWhite) wg += 20; else bg += 20;
      }
    }

    const newSquares = squares.map(s => {
      if (s.x === fromPos.x && s.y === fromPos.y) return { ...s, piece: null };
      if (s.x === toPos.x   && s.y === toPos.y) {
        // Capture mine if moving onto one
        const capturedMine = s.type?.includes('Mine');
        return {
          ...s,
          piece,
          mineOwner: capturedMine ? (piece.isWhite ? 'white' : 'black') : s.mineOwner
        };
      }
      return s;
    });
    squares = newSquares;

    playSound(isCapture ? 'capture' : 'move');

    // Mine income for newly active player (based on ownership)
    const nextWhite = !piece.isWhite;
    const income = newSquares
      .filter(s => s.type?.includes('Mine') && s.mineOwner === (nextWhite ? 'white' : 'black'))
      .length * 5;
    if (nextWhite) wg += income; else bg += income;

    if (nextWhite) wg++; else bg++;

    whiteGold     = wg;
    blackGold     = bg;
    whiteIsActive = nextWhite;
    lastMoveFrom  = fromPos;
    lastMoveTo    = toPos;

    if (analyzing && analysisWorker) {
      analysisResults = [];  // Clear old results
      // depthTimings = [];
      analysisWorker.postMessage({
        type: 'update',
        payload: getCurrentPosition()
      });
    }

    return true;
  }

  function placePieceAt(pos, pieceType, isWhitePiece) {
    const sq = sqAt(pos);
    if (!sq || isRocks(sq.type)) return;
    const cost = SHOP.find(s => s.type === pieceType)?.cost ?? 0;
    if (isWhitePiece && whiteGold < cost) return;
    if (!isWhitePiece && blackGold < cost) return;

    pushHistory();
    if (isWhitePiece) whiteGold -= cost;
    else              blackGold -= cost;

    squares = squares.map(s => {
      if (s.x === pos.x && s.y === pos.y) {
        // Capture mine if placing on one
        const capturedMine = s.type?.includes('Mine');
        return {
          ...s,
          piece: { type: pieceType, isWhite: isWhitePiece },
          mineOwner: capturedMine ? (isWhitePiece ? 'white' : 'black') : s.mineOwner
        };
      }
      return s;
    });

    playSound('move');

    // Mine income for newly active player (based on ownership)
    const nextWhite = !isWhitePiece;
    const income = squares
      .filter(s => s.type?.includes('Mine') && s.mineOwner === (nextWhite ? 'white' : 'black'))
      .length * 5;
    if (nextWhite) whiteGold += income; else blackGold += income;

    if (nextWhite) whiteGold++; else blackGold++;

    whiteIsActive = nextWhite;
    lastMoveFrom  = null;
    lastMoveTo    = pos;

    if (analyzing && analysisWorker) {
      analysisWorker.postMessage({
        type: 'update',
        payload: getCurrentPosition()
      });
    }
  }

  function removePieceAt(pos) {
    const sq = sqAt(pos);
    if (!sq?.piece || !isRealPiece(sq.piece)) return;
    pushHistory();
    squares = squares.map(s => {
      if (s.x === pos.x && s.y === pos.y) return { ...s, piece: null };
      return s;
    });

    if (analyzing && analysisWorker) {
      analysisWorker.postMessage({
        type: 'update',
        payload: getCurrentPosition()
      });
    }
  }

  // ── Board coordinate helper (for shop drag hit-testing) ──────────────────
  function globalCursorToBoard(clientX, clientY) {
    if (!boardCanvas || !mapSize) return null;
    const rect   = boardCanvas.getBoundingClientRect();
    const n      = mapSize;
    const cellPx = rect.width / n;
    let cx = Math.floor((clientX - rect.left) / cellPx);
    let cy = Math.floor((clientY - rect.top)  / cellPx);
    const bx = viewAsWhite ? cx : n - 1 - cx;
    const by = viewAsWhite ? n - 1 - cy : cy;
    if (bx < 0 || bx >= n || by < 0 || by >= n) return null;
    return { x: bx, y: by };
  }

  // ── Board callbacks (x, y are separate numbers, matching Board.svelte API) ─
  function handleSquareClick(x, y) {
    if (!squares.length) return;

    // Click on selected square → remove the piece
    if (selectedSqPos && x === selectedSqPos.x && y === selectedSqPos.y) {
      removePieceAt({ x, y });
      clearSelection();
      return;
    }

    // We have a selection → try to move there
    if (selectedSqPos) {
      const isLegal = legalDests.some(d => d.x === x && d.y === y);
      if (isLegal) {
        applyMove(selectedSqPos, { x, y });
        clearSelection();
        return;
      }
      // Not legal — fall through to reselect if there's a piece
    }

    // Select a piece
    const sq = sqAt({ x, y });
    if (sq?.piece && isRealPiece(sq.piece)) {
      selectedSqPos = { x, y };
      legalDests    = computeLegalMoves(sq, squares);
    } else {
      clearSelection();
    }
  }

  function handleGrab(x, y, clientX, clientY) {
    const sq = sqAt({ x, y });
    if (!sq?.piece || !isRealPiece(sq.piece)) return;
    selectedSqPos = { x, y };
    legalDests    = computeLegalMoves(sq, squares);
    const imgSrc  = pieceImgSrc(sq.piece.type, sq.piece.isWhite);
    dragging = { type: 'move', fromX: x, fromY: y, imgSrc, ghostX: clientX, ghostY: clientY };
  }

  function handleDrop(x, y) {
    if (!dragging) return;
    if (dragging.type === 'move') {
      const isLegal = legalDests.some(d => d.x === x && d.y === y);
      if (isLegal) applyMove({ x: dragging.fromX, y: dragging.fromY }, { x, y });
    } else if (dragging.type === 'place') {
      placePieceAt({ x, y }, dragging.pieceType, dragging.isWhitePiece);
    }
    dragging = null;
    clearSelection();
  }

  function handleHover(sq) {
    if (dragging) dragOverSq = sq;
  }

  // ── Shop drag ─────────────────────────────────────────────────────────────
  function shopPointerDown(e, pieceType, isWhitePiece) {
    if (!squares.length) return;
    const cost = SHOP.find(s => s.type === pieceType)?.cost ?? 0;
    if (isWhitePiece && whiteGold < cost) return;
    if (!isWhitePiece && blackGold < cost) return;
    e.preventDefault();
    clearSelection();
    dragging = {
      type: 'place', pieceType, isWhitePiece,
      imgSrc: pieceImgSrc(pieceType, isWhitePiece),
      ghostX: e.clientX, ghostY: e.clientY,
    };
  }

  // ── Global pointer tracking ───────────────────────────────────────────────
  function globalPointerMove(e) {
    if (!dragging) return;
    dragging = { ...dragging, ghostX: e.clientX, ghostY: e.clientY };
    dragOverSq = globalCursorToBoard(e.clientX, e.clientY);
  }

  function globalPointerUp(e) {
    if (!dragging) return;
    if (dragging.type === 'place') {
      const sq = globalCursorToBoard(e.clientX, e.clientY);
      if (sq) placePieceAt(sq, dragging.pieceType, dragging.isWhitePiece);
    }
    dragging = null;
    clearSelection();
  }

  onMount(() => {
    window.addEventListener('pointermove', globalPointerMove);
    window.addEventListener('pointerup',   globalPointerUp);
    return () => {
      window.removeEventListener('pointermove', globalPointerMove);
      window.removeEventListener('pointerup',   globalPointerUp);
    };
  });
</script>

<!-- Drag ghost -->
{#if dragging}
  <img
    class="drag-ghost"
    src={dragging.imgSrc}
    alt=""
    style="left:{dragging.ghostX}px; top:{dragging.ghostY}px;"
    draggable="false"
  />
{/if}

<div class="sandbox-wrap">
  <!-- Left: board -->
  <div class="board-col">
    {#if squares.length > 0}
      <div class="board-wrap">
        <Board
          squares={squares}
          mapSize={mapSize}
          isWhite={viewAsWhite}
          selectedSquare={selectedSquare}
          {legalDests}
          dragOverSquare={dragOverSq}
          {hoverSquares}
          lastMoveSquares={lastMoveSquares}
          onPieceGrabbed={handleGrab}
          onDropOnBoard={handleDrop}
          onHoverSquare={handleHover}
          onSquareClick={handleSquareClick}
          onCanvasReady={(el) => { boardCanvas = el; }}
        />
      </div>
    {:else}
      <div class="empty-board">
        <p>Generate a board to start</p>
      </div>
    {/if}
  </div>

  <!-- Right: controls -->
  <aside class="side-panel">

    <!-- Player bars -->
    <div class="players">
      <div class="player-bar" class:active={whiteIsActive}>
        <span class="player-name">White</span>
        <div class="gold-wrap">
          <input class="gold-input" type="number" min="0" max="9999"
                 bind:value={whiteGold} />
          <span class="gold-unit">g</span>
        </div>
      </div>
      <div class="player-bar" class:active={!whiteIsActive}>
        <span class="player-name">Black</span>
        <div class="gold-wrap">
          <input class="gold-input" type="number" min="0" max="9999"
                 bind:value={blackGold} />
          <span class="gold-unit">g</span>
        </div>
      </div>
    </div>

    <!-- Turn + board controls -->
    <div class="ctrl-row">
      <button class="ctrl-btn" onclick={passTurn} disabled={!squares.length}>Pass turn</button>
      <button class="ctrl-btn" onclick={() => viewAsWhite = !viewAsWhite} disabled={!squares.length}>Flip</button>
    </div>
    <div class="ctrl-row">
      <button class="ctrl-btn" onclick={undo} disabled={!canUndo}>← Undo</button>
      <button class="ctrl-btn" onclick={redo} disabled={!canRedo}>Redo →</button>
    </div>

    <!-- Shop: place pieces -->
    <div class="section">
      <div class="section-label">Place piece</div>
      <div class="shop-label">White</div>
      <div class="shop-row">
        {#each SHOP as item}
          {@const canAfford = whiteGold >= item.cost}
          <button
            class="shop-piece"
            class:active={dragging?.type === 'place' && dragging.pieceType === item.type && dragging.isWhitePiece}
            disabled={!squares.length || !canAfford}
            title="{item.type} — {item.cost}g"
            onpointerdown={(e) => shopPointerDown(e, item.type, true)}
          >
            <img src={pieceImgSrc(item.type, true)} alt={item.type} width="32" height="32" draggable="false" />
            <span class="cost" class:dim={!canAfford}>{item.cost}g</span>
          </button>
        {/each}
      </div>
      <div class="shop-label">Black</div>
      <div class="shop-row">
        {#each SHOP as item}
          {@const canAfford = blackGold >= item.cost}
          <button
            class="shop-piece"
            class:active={dragging?.type === 'place' && dragging.pieceType === item.type && !dragging.isWhitePiece}
            disabled={!squares.length || !canAfford}
            title="{item.type} — {item.cost}g"
            onpointerdown={(e) => shopPointerDown(e, item.type, false)}
          >
            <img src={pieceImgSrc(item.type, false)} alt={item.type} width="32" height="32" draggable="false" />
            <span class="cost" class:dim={!canAfford}>{item.cost}g</span>
          </button>
        {/each}
      </div>
      <p class="shop-hint">Drag a piece onto the board to place it. Click a piece to select it, then click its square again to remove it.</p>
    </div>

    <!-- Map generation -->
    <div class="section">
      <div class="section-label">Map</div>
      <div class="option-row">
        <label>Size: <strong>{genSize}×{genSize}</strong></label>
        <input type="range" min="6" max="16" step="2" bind:value={genSize} />
      </div>
      <label class="checkbox-label">
        <input type="checkbox" bind:checked={genRandom} />
        Full random
      </label>
      <div class="seed-row">
        <input
          class="seed-input"
          class:seed-invalid={seedInput.trim() && seedInputError}
          class:seed-ok={seedInput.trim() && !seedInputError}
          type="text"
          bind:value={seedInput}
          placeholder="Paste seed to load a specific board..."
          spellcheck="false"
          autocomplete="off"
        />
      </div>
      {#if seedInputError}<p class="error">{seedInputError}</p>{/if}
      {#if genError}<p class="error">{genError}</p>{/if}
      <button class="action-btn" onclick={generateMap}
              disabled={generating || !seedInputValid}>
        {generating ? 'Generating…' : 'Generate new board'}
      </button>
    </div>

    <div class="section">
      <div class="section-label">Analysis</div>
      <!-- <button class="action-btn" onclick={botMove}
              disabled={thinking || analyzing}>
        {thinking ? "Thinking..." : "Bot move"}
      </button> -->
      <button class="action-btn" onclick={toggleAnalysis} disabled={!mapSize}>
        {analyzing ? "Stop" : "Analyze"}
      </button>
      {#if analysisResults.length > 0}
        <div class="analysis-info">
          <div class="analysis-depth">Depth: {analysisDepth}</div>
          <div class="analysis-moves">
            {#each analysisResults as move}
              <button
                class="analysis-move"
                onmouseenter={() => handleAnalysisMoveHover(move)}
                onmouseleave={() => handleAnalysisMoveHover(null)}
              >
                <span class="move-notation">{toNotation(move)}</span>
                <span class="move-score">{move.score.toFixed(2)}</span>
              </button>
            {/each}
          </div>
          <!-- {#if depthTimings.length > 0}
            <div class="depth-timings">
              {#each depthTimings as { depth, timeMs }}
                <div>Depth {depth}: {timeMs.toFixed(0)}ms</div>
              {/each}
            </div>
          {/if} -->
        </div>
      {/if}
    </div>

  </aside>
</div>

<style>
  .drag-ghost {
    position: fixed;
    width: 56px; height: 56px;
    transform: translate(-50%, -50%);
    pointer-events: none;
    z-index: 1000;
    image-rendering: pixelated;
    filter: drop-shadow(0 4px 10px rgba(0,0,0,0.7));
  }

  .sandbox-wrap {
    display: flex;
    flex: 1;
    overflow: hidden;
    height: calc(100vh - 46px);
  }

  /* ── Board column ── */
  .board-col {
    flex: 1;
    min-width: 0;
    min-height: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    overflow: hidden;
    padding: 0.75rem;
  }

  /* Constrain board to fit within the column — square, never taller than the viewport */
  .board-wrap {
    width: min(100%, calc(100vh - 46px - 1.5rem));
  }

  .empty-board {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 100%;
    max-width: 600px;
    aspect-ratio: 1;
    border: 2px dashed #2a2a4a;
    border-radius: 8px;
    color: #555;
    font-size: 0.95rem;
  }

  /* ── Side panel ── */
  .side-panel {
    width: 270px;
    flex-shrink: 0;
    display: flex;
    flex-direction: column;
    gap: 0.9rem;
    padding: 1rem;
    border-left: 1px solid #2a2a4a;
    background: #14142a;
    overflow-y: auto;
    overflow-x: hidden;
  }

  /* ── Player bars ── */
  .players { display: flex; flex-direction: column; gap: 0.4rem; }
  .player-bar {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 0.5rem 0.75rem;
    border-radius: 6px;
    border: 1px solid #2a2a4a;
    background: #1e1e38;
    transition: border-color 0.15s;
  }
  .player-bar.active { border-color: #7cfc00; background: rgba(124,252,0,0.06); }
  .player-name { font-size: 0.9rem; font-weight: 600; color: #ccc; }
  .gold-wrap { display: flex; align-items: center; gap: 0.25rem; }
  .gold-input {
    width: 62px;
    background: #2d2d4a; border: 1px solid #3a3a5a; border-radius: 4px;
    color: #f0c040; font-size: 0.9rem; padding: 0.2rem 0.4rem; text-align: right;
  }
  .gold-unit { color: #888; font-size: 0.85rem; }

  /* ── Control buttons ── */
  .ctrl-row { display: flex; gap: 0.4rem; }
  .ctrl-btn {
    flex: 1; padding: 0.4rem 0.5rem; font-size: 0.82rem;
    background: #2d2d4a; border: 1px solid #3a3a5a; color: #ccc; border-radius: 5px;
  }
  .ctrl-btn:hover:not(:disabled) { background: #3a3a6a; color: #eee; }
  .ctrl-btn:disabled { opacity: 0.35; cursor: default; }
  .ctrl-btn.danger { color: #e07070; border-color: #5a3a3a; }
  .ctrl-btn.danger:hover:not(:disabled) { background: #3a2a2a; }

  /* ── Sections ── */
  .section {
    display: flex; flex-direction: column; gap: 0.5rem;
    padding-top: 0.6rem; border-top: 1px solid #2a2a4a;
  }
  .section-label {
    font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.07em; color: #555;
  }

  /* ── Shop ── */
  .shop-label { font-size: 0.78rem; color: #888; margin-bottom: -0.2rem; }
  .shop-row { display: flex; gap: 0.2rem; }
  .shop-piece {
    display: flex; flex-direction: column; align-items: center;
    padding: 0.2rem 0.15rem;
    background: #2d2d4a; border: 2px solid transparent; border-radius: 7px;
    gap: 2px; touch-action: none; user-select: none; flex: 1; min-width: 0;
    transition: border-color 0.1s, background 0.1s;
  }
  .shop-piece img { image-rendering: pixelated; }
  .shop-piece:not(:disabled):hover { border-color: #7b8cde; background: #3a3a60; }
  .shop-piece.active { border-color: #f5c518; background: #3a3a20; }
  .shop-piece:disabled { opacity: 0.3; cursor: default; }
  .cost { font-size: 0.65rem; color: #f5c518; }
  .cost.dim { color: #666; }
  .shop-hint { font-size: 0.74rem; color: #666; margin: 0; line-height: 1.4; }

  /* ── Map generation ── */
  .option-row {
    display: flex; align-items: center; justify-content: space-between;
    gap: 0.5rem; font-size: 0.88rem; color: #ccc;
  }
  .option-row input[type="range"] { flex: 1; accent-color: #7b8cde; }
  .checkbox-label {
    display: flex; align-items: center; gap: 0.5rem;
    font-size: 0.88rem; color: #ccc; cursor: pointer;
  }
  .seed-row { display: flex; }
  .seed-input {
    width: 100%; background: #2d2d4a; border: 1px solid #3a3a5a; border-radius: 5px;
    color: #eee; font-size: 0.8rem; font-family: monospace; padding: 0.35rem 0.6rem;
  }
  .seed-input.seed-invalid { border-color: #c05050; }
  .seed-input.seed-ok      { border-color: #4a9a4a; }

  .action-btn {
    padding: 0.65rem; font-size: 0.95rem;
    background: #4a6fa5; color: #fff; border: none; border-radius: 6px;
  }
  .action-btn:disabled { opacity: 0.45; cursor: default; }

  .error { color: #e07070; font-size: 0.8rem; margin: 0; }

  /* ── Analysis ── */
  .analysis-info {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
    margin-top: 0.3rem;
  }
  .analysis-depth {
    font-size: 0.82rem;
    color: #888;
    padding: 0 0.2rem;
  }
  .analysis-moves {
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
  }
  .analysis-move {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 0.3rem 0.5rem;
    background: #2d2d4a;
    border: 1px solid transparent;
    border-radius: 4px;
    color: #ccc;
    font-size: 0.85rem;
    font-family: 'Courier New', monospace;
    cursor: default;
    transition: background 0.1s, border-color 0.1s;
    width: 100%;
    text-align: left;
  }
  .analysis-move:hover {
    background: rgba(123, 140, 222, 0.25);
    border-color: #7b8cde;
    color: #fff;
  }
  .move-notation {
    flex: 1;
  }
  .move-score {
    color: #f5c518;
    font-weight: 600;
  }

  /* ── Mobile: stack vertically ── */
  @media (max-width: 640px) {
    .sandbox-wrap {
      flex-direction: column;
      height: auto;
      overflow: visible;
    }
    .board-col {
      min-height: unset;
      padding: 0.5rem;
    }
    /* On mobile the board is constrained by width, not height */
    .board-wrap { width: 100%; }
    .side-panel {
      width: 100%;
      border-left: none;
      border-top: 1px solid #2a2a4a;
      overflow: visible;
    }
  }
</style>
