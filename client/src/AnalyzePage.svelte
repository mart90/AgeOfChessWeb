<script>
  import { onMount, onDestroy } from 'svelte';
  import Board from './Board.svelte';
  import { currentGame } from './lib/currentGame.svelte.js';
  import { generateSandboxBoard } from './lib/api.js';
  import { playSound } from './lib/sound.js';
  import { computeLegalMoves, isRealPiece, basePieceType, isRocks } from './lib/pathFinder.js';
  import MoveList from './MoveList.svelte';
  import { MoveNode, addChild } from './lib/moveTree.js';
  import { wheelNav } from './lib/wheelNav.js';
  import { serializeTree, deserializeTree, getNodePath, nodeAtPath } from './lib/treeStorage.js';
  import { createBoardState, getAllLegalNewPiecePlacements } from './lib/analysis.js'
  import { engineSearch, cancelSearch } from './lib/engine/engine.js';
  import { SHOP, MINE_INCOME } from './lib/constants.js'
  import { EngineAnalysis as AnalysisWorker } from './lib/engine/engineAnalysis.js';
  import { settings, persistSettings } from './lib/settings.svelte.js';
  import { pieceSrc } from './lib/boardTheme.js';
  import { unpackGameTree, packAnalyzeTree, mainlineLength, GAME_TRANSFER_KEY, GAME_TRANSFER_FORMAT } from './lib/gameTransfer.js';
  import { addLibraryGame, updateLibraryGame, getSharedLibraryGame, setLibraryGamePublic } from './lib/api.js';
  import { navigate } from './lib/navigate.js';
  import { pendingLibraryOpen } from './lib/library.js';
  import { authState } from './lib/auth.svelte.js';

  const pieceImgSrc = pieceSrc;

  // ── Board state ─────────────────────────────────────────────────────────────
  let squares       = $state([]);
  let mapSize       = $state(0);
  let mapSeed       = $state('');
  let whiteGold     = $state(0);
  let blackGold     = $state(6);
  let whiteIsActive = $state(true);
  let viewAsWhite   = $state(true);

  // ── My library: the entry this analysis was opened from / saved to (null = not saved yet) ──
  // An entry someone else shared has a shareId but no id: saving it makes a copy in our own library.
  let libraryId      = $state(null);
  let libraryShareId = $state(null);
  let libraryPublic  = $state(false);
  let libraryOwner   = $state(null);    // { name, username } when the entry is someone else's
  let libraryTitle   = $state('');
  let libraryResult  = null;
  let libraryMsg     = $state('');
  let librarySaving  = $state(false);
  let linkCopied     = $state(false);
  const libraryOwned = $derived(libraryId != null);

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

  // ── Move tree (for move list display + navigation) ───────────────────────
  let moveTree      = $state(null);
  let currentNode   = $state(null);
  let treeRevision  = $state(0);

  // ── State persistence ────────────────────────────────────────────────────
  let _persistenceReady = $state(false);

  $effect(() => {
    if (!_persistenceReady || !moveTree || !mapSize) return;
    treeRevision; // track tree mutations
    const data = {
      mapSize,
      mapSeed,
      viewAsWhite,
      library:     { id: libraryId, shareId: libraryShareId, isPublic: libraryPublic, owner: libraryOwner,
                     title: libraryTitle, result: libraryResult },
      treeData:    serializeTree(moveTree),
      currentPath: currentNode ? getNodePath(moveTree, currentNode) : [],
    };
    try { localStorage.setItem('analyze_board_state', JSON.stringify(data)); } catch {}
  });

  // The URL carries the library entry's share id, so the address bar is the share link
  $effect(() => {
    if (!_persistenceReady) return;
    const want = libraryShareId ? `/analyze/${libraryShareId}` : '/analyze';
    const here = window.location.pathname;
    if (here.startsWith('/analyze') && here !== want) history.replaceState(history.state, '', want);
  });

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
  let analysisWorker = $state(null);
  let analysisResults = $state([]);
  let analysisDepth = $state(0);

  function startWorker() {
    if (!analysisWorker) {
      analysisWorker = new AnalysisWorker();

      analysisWorker.onmessage = (event) => {
        const { type, depth, topMoves } = event.data;

        if (type === 'depth-result') {
          analysisDepth = depth;
          analysisResults = topMoves || [];
        }
      };
    }

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

  // ── Engine move (Rust engine via WebAssembly, see lib/engine/) ──────────────
  const ENGINE_MOVE_NODES = 600_000;
  let engineThinking = $state(false);
  let engineError    = $state('');
  let searchToken    = 0;              // invalidates results of aborted searches

  // Top engine move while analyzing, shown on the board
  const suggestion = $derived(analyzing && analysisResults.length > 0 ? { ...analysisResults[0].move, isWhite: whiteIsActive } : null);

  function abortEngine() {
    if (engineThinking) {
      cancelSearch();
      engineThinking = false;
    }
    searchToken++;
  }

  function applyEngineMove(best) {
    if (best.type === 'm') applyMove(best.fromPos, best.toPos);
    else placePieceAt(best.toPos, best.pieceType, whiteIsActive);
  }

  // While analyzing: play the current top move. Otherwise: think for 600k nodes and play.
  async function engineMove() {
    if (engineThinking || !squares.length) return;
    engineError = '';
    if (suggestion) {
      applyEngineMove(suggestion);
      return;
    }
    const position = getCurrentPosition();
    const before = JSON.stringify(position);
    const token = ++searchToken;
    engineThinking = true;
    try {
      const res = await engineSearch(position, { nodes: ENGINE_MOVE_NODES });
      // Ignore the result if the position changed meanwhile
      if (token !== searchToken || JSON.stringify(getCurrentPosition()) !== before) return;
      engineThinking = false;
      if (res.best) applyEngineMove(res.best);
    } catch (e) {
      if (token === searchToken) engineError = e.message ?? String(e);
    } finally {
      if (token === searchToken) engineThinking = false;
    }
  }

  function formatEval(info) {
    if (info.mate != null) return info.mate > 0 ? `#${info.mate}` : `#-${-info.mate}`;
    return `${info.score > 0 ? '+' : ''}${info.score.toFixed(1)} g`;   // non-breaking: never split from the number
  }

  onDestroy(() => cancelSearch());

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

  // ── Import / restore helpers ─────────────────────────────────────────────

  function parseSanNotation(san) {
    if (!san) return null;
    const lx = c => c.charCodeAt(0) - 97;
    const mv = san.match(/^([a-z])(\d+)[x-]([a-z])(\d+)$/);
    if (mv) return {
      from: { x: lx(mv[1]), y: parseInt(mv[2]) - 1 },
      to:   { x: lx(mv[3]), y: parseInt(mv[4]) - 1 },
    };
    const pl = san.match(/^([a-z])(\d+)=/);
    if (pl) return { from: null, to: { x: lx(pl[1]), y: parseInt(pl[2]) - 1 } };
    return null;
  }

  // Convert a GameStateDto-shaped boardState to an AnalyzePage snapshot
  function gameDtoToSnapshot(dto, san) {
    const parsed = parseSanNotation(san);
    return {
      squares:      dto.squares,
      whiteGold:    dto.white.gold,
      blackGold:    dto.black.gold,
      whiteIsActive: dto.white.isActive,
      lastMoveFrom: parsed?.from ?? null,
      lastMoveTo:   parsed?.to   ?? null,
    };
  }

  function applyLoadedTree(rawTree, path, size, seed, flip) {
    mapSize       = size;
    mapSeed       = seed ?? '';
    viewAsWhite   = flip ?? true;
    undoStack     = [];
    redoStack     = [];
    dragging      = null;
    clearSelection();
    moveTree      = rawTree;
    currentNode   = nodeAtPath(rawTree, path ?? []);
    if (currentNode?.boardState) restoreSnapshot(currentNode.boardState);
  }

  function loadImportedGameTree(data) {
    const rawTree = deserializeTree(data.treeData);
    // Convert every node's boardState from GameStateDto to AnalyzePage snapshot
    function walk(node) {
      if (node.boardState) node.boardState = gameDtoToSnapshot(node.boardState, node.san);
      for (const child of node.children) walk(child);
    }
    walk(rawTree);
    applyLoadedTree(rawTree, data.currentPath, data.mapSize, data.mapSeed, true);
  }

  /** entry: { id, shareId, isPublic, owner, ownerUsername, title, result } (server), or the saved session's. */
  function setLibrary(entry = {}) {
    libraryId      = entry.id ?? null;
    libraryShareId = entry.shareId ?? null;
    libraryPublic  = !!entry.isPublic;
    libraryOwner   = libraryId == null && entry.owner
      ? (typeof entry.owner === 'string' ? { name: entry.owner, username: entry.ownerUsername } : entry.owner)
      : null;
    libraryTitle   = entry.title ?? '';
    libraryResult  = entry.result ?? null;
    libraryMsg     = '';
  }

  async function saveToLibrary() {
    if (!moveTree || librarySaving) return;
    librarySaving = true;
    libraryMsg = '';
    const title = libraryTitle.trim() || `Analysis ${mapSize}×${mapSize}, ${new Date().toLocaleDateString()}`;
    const entry = {
      title, gameId: null, mapSize, mapSeed: mapSeed || null,
      plyCount: mainlineLength(moveTree), result: libraryResult, data: packAnalyzeTree(moveTree),
    };
    try {
      if (libraryId) await updateLibraryGame(libraryId, entry);
      else {
        // A new entry, or a copy of someone else's (theirs stays as it is)
        const res = await addLibraryGame(entry);
        libraryId      = res.id;
        libraryShareId = res.shareId;
        libraryPublic  = false;
        libraryOwner   = null;
      }
      libraryTitle = title;
      libraryMsg = 'Saved ✓';
    } catch (e) {
      libraryMsg = e.message;
    } finally {
      librarySaving = false;
    }
  }

  async function togglePublic(e) {
    const box = e.currentTarget, want = box.checked;
    libraryMsg = '';
    try {
      await setLibraryGamePublic(libraryId, want);
      libraryPublic = want;
    } catch (err) {
      box.checked = libraryPublic;
      libraryMsg = err.message;
    }
  }

  let copiedTimer = null;
  async function copyShareLink() {
    try {
      await navigator.clipboard.writeText(`${window.location.origin}/analyze/${libraryShareId}`);
      linkCopied = true;
      clearTimeout(copiedTimer);
      copiedTimer = setTimeout(() => linkCopied = false, 2000);
    } catch {
      libraryMsg = 'Could not copy the link.';
    }
  }

  function loadSavedAnalyzeState(data) {
    const rawTree = deserializeTree(data.treeData);
    setLibrary(data.library ?? {});
    applyLoadedTree(rawTree, data.currentPath, data.mapSize, data.mapSeed, data.viewAsWhite);
  }

  function navigateToNode(node) {
    if (!node?.boardState) return;
    // Sound only when stepping exactly one move forward (not back, not jumps)
    if (node.parent === currentNode && node.san) playSound(/x/.test(node.san) ? 'capture' : 'move');
    abortEngine();
    restoreSnapshot(node.boardState);
    currentNode = node;

    if (analyzing && analysisWorker) {
      analysisResults = [];
      analysisWorker.postMessage({ type: 'update', payload: getCurrentPosition() });
    }
  }

  /** Removes the current move and everything after it from the tree, then goes to the previous
   *  move. Asks first when later moves would be lost too. */
  function deleteCurrentNode() {
    const node = currentNode;
    if (!node?.parent) return;
    if (node.children.length && !confirm('Delete this move and all moves after it?')) return;
    const parent = node.parent;
    parent.children = parent.children.filter(c => c !== node);
    navigateToNode(parent);
    treeRevision++;
  }

  /** Mouse wheel over the board: one move back (-1) or forward (+1). */
  function stepMove(dir) {
    const target = dir < 0 ? currentNode?.parent : currentNode?.children[0];
    if (target) navigateToNode(target);
  }

  function handleKeydown(e) {
    if (!currentNode || e.ctrlKey || e.metaKey || e.altKey) return;
    if (e.target.closest?.('input, textarea, select, [contenteditable]')) return;
    let target = null;
    if (e.key === 'ArrowLeft')  target = currentNode.parent;
    else if (e.key === 'ArrowRight') target = currentNode.children[0];
    else if (e.key === 'Home')  target = moveTree;
    else if (e.key === 'End')   { target = currentNode; while (target.children[0]) target = target.children[0]; }
    else if (e.key === 'Delete') { e.preventDefault(); deleteCurrentNode(); return; }
    else return;
    e.preventDefault();
    if (target && target !== currentNode) navigateToNode(target);
  }

  function undoOnce() {
    redoStack = [...redoStack, snapshot()];
    restoreSnapshot(undoStack[undoStack.length - 1]);
    undoStack = undoStack.slice(0, -1);
  }

  function undo() {
    if (!undoStack.length) return;
    abortEngine();
    undoOnce();

    if (analyzing && analysisWorker) {
      analysisResults = [];
      analysisWorker.postMessage({
        type: 'update',
        payload: getCurrentPosition()
      });
    }
  }

  function redo() {
    if (!redoStack.length) return;
    abortEngine();
    undoStack = [...undoStack, snapshot()];
    restoreSnapshot(redoStack[redoStack.length - 1]);
    redoStack = redoStack.slice(0, -1);


    if (analyzing && analysisWorker) {
      analysisResults = [];
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
      blackGold     = 6;
      whiteIsActive = true;
      lastMoveFrom  = null;
      lastMoveTo    = null;
      dragging      = null;
      undoStack     = [];
      redoStack     = [];
      clearSelection();
      moveTree    = new MoveNode(null, snapshot(), null);
      currentNode = moveTree;
      setLibrary();

      abortEngine();

      if (analyzing && analysisWorker) {
        analysisResults = [];
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
      analysisResults = [];
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
      .length * MINE_INCOME;
    if (nextWhite) whiteGold = Math.max(0, whiteGold + income);
    else           blackGold = Math.max(0, blackGold + income);
    whiteIsActive = nextWhite;
    clearSelection();

    if (analyzing && analysisWorker) {
      analysisResults = [];
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

    // Compute notation before mutations (capture detection needs current board)
    const isSameSquare = fromPos.x === toPos.x && fromPos.y === toPos.y;
    const isCapture = toSq?.piece != null && !isSameSquare;
    const _col = x => String.fromCharCode(97 + x);
    const moveSan = `${_col(fromPos.x)}${fromPos.y+1}${isCapture ? 'x' : '-'}${_col(toPos.x)}${toPos.y+1}`;

    pushHistory();
    const piece = fromSq.piece;
    let wg = whiteGold, bg = blackGold;

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
      .length * MINE_INCOME;
    if (nextWhite) wg += income; else bg += income;

    if (nextWhite) wg++; else bg++;

    whiteGold     = wg;
    blackGold     = bg;
    whiteIsActive = nextWhite;
    lastMoveFrom  = fromPos;
    lastMoveTo    = toPos;

    if (analyzing && analysisWorker) {
      analysisResults = [];
      analysisWorker.postMessage({
        type: 'update',
        payload: getCurrentPosition()
      });
    }

    // Tree tracking
    if (moveTree && currentNode) {
      currentNode = addChild(currentNode, moveSan, snapshot());
      treeRevision++;
    }

    return true;
  }

  function placePieceAt(pos, pieceType, isWhitePiece) {
    const sq = sqAt(pos);
    if (!sq || isRocks(sq.type)) return;
    if (isWhitePiece !== whiteIsActive) return; // wrong side's turn
    const cost = SHOP.find(s => s.type === pieceType)?.cost ?? 0;
    if (isWhitePiece && whiteGold < cost) return;
    if (!isWhitePiece && blackGold < cost) return;

    // Compute placement notation (before mutations)
    const pieceCodeMap = { Pawn: 'p', Queen: 'Q', Rook: 'R', Bishop: 'B', Knight: 'N' };
    const placeSan = `${String.fromCharCode(97 + pos.x)}${pos.y + 1}=${pieceCodeMap[pieceType] ?? pieceType[0].toUpperCase()}`;

    pushHistory();
    if (isWhitePiece) whiteGold -= cost;
    else              blackGold -= cost;

    squares = squares.map(s => {
      if (s.x === pos.x && s.y === pos.y) {
        // Capture mine if placing on one
        const capturedMine = s.type?.includes('Mine');
        return {
          ...s,
          piece: { type: (isWhitePiece ? 'White' : 'Black') + pieceType, isWhite: isWhitePiece },
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
      .length * MINE_INCOME;
    if (nextWhite) whiteGold += income; else blackGold += income;

    if (nextWhite) whiteGold++; else blackGold++;

    whiteIsActive = nextWhite;
    lastMoveFrom  = null;
    lastMoveTo    = pos;

    if (analyzing && analysisWorker) {
      analysisResults = [];
      analysisWorker.postMessage({
        type: 'update',
        payload: getCurrentPosition()
      });
    }

    // Tree tracking
    if (moveTree && currentNode) {
      currentNode = addChild(currentNode, placeSan, snapshot());
      treeRevision++;
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
      analysisResults = [];
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

    // Click on selected square → deselect
    if (selectedSqPos && x === selectedSqPos.x && y === selectedSqPos.y) {
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

    // Select a piece (only the active side's pieces)
    const sq = sqAt({ x, y });
    if (sq?.piece && isRealPiece(sq.piece) && sq.piece.isWhite === whiteIsActive) {
      selectedSqPos = { x, y };
      legalDests    = computeLegalMoves(sq, squares);
    } else {
      clearSelection();
    }
  }

  function handleGrab(x, y, clientX, clientY) {
    const sq = sqAt({ x, y });
    if (!sq?.piece || !isRealPiece(sq.piece)) return;
    if (sq.piece.isWhite !== whiteIsActive) return; // wrong side's turn
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
      if (legalDests.some(d => d.x === x && d.y === y))
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
    if (!squares.length || isWhitePiece !== whiteIsActive) return;
    const cost = SHOP.find(s => s.type === pieceType)?.cost ?? 0;
    if (isWhitePiece && whiteGold < cost) return;
    if (!isWhitePiece && blackGold < cost) return;
    e.preventDefault();
    clearSelection();
    const internalState = createBoardState(squares, whiteGold, blackGold, isWhitePiece);
    const placements = getAllLegalNewPiecePlacements(internalState);
    legalDests = placements.filter(p => p.pieceType === pieceType).map(p => p.toPos);
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
      if (sq && legalDests.some(d => d.x === sq.x && d.y === sq.y))
        placePieceAt(sq, dragging.pieceType, dragging.isWhitePiece);
    }
    dragging = null;
    clearSelection();
  }

  function openLibraryEntry(entry) {
    try {
      applyLoadedTree(unpackGameTree(entry.data), [], entry.mapSize, entry.mapSeed, true);
      setLibrary(entry);
    } catch (e) {
      console.error('Failed to open library game', e);
      genError = 'Could not open this library game.';
    }
  }

  function restoreSession(saved) {
    if (!saved) return;
    try { loadSavedAnalyzeState(saved); }
    catch (e) { console.error('Failed to restore analyze state', e); }
  }

  function cleanup() {
    window.removeEventListener('pointermove', globalPointerMove);
    window.removeEventListener('pointerup',   globalPointerUp);
    clearTimeout(copiedTimer);
  }

  onMount(() => {
    window.addEventListener('pointermove', globalPointerMove);
    window.addEventListener('pointerup',   globalPointerUp);

    // Load a game opened from the library, imported from GamePage or followed through a share link
    // (/analyze/{shareId}), or restore the saved session
    const pendingRaw = localStorage.getItem(GAME_TRANSFER_KEY);
    const libraryEntry = pendingLibraryOpen.entry;
    pendingLibraryOpen.entry = null;
    const urlShareId = window.location.pathname.match(/^\/analyze\/([^/]+)/)?.[1] ?? null;
    let saved = null;
    try { saved = JSON.parse(localStorage.getItem('analyze_board_state')); } catch { /* none */ }

    if (libraryEntry) {
      openLibraryEntry(libraryEntry);
    } else if (pendingRaw) {
      localStorage.removeItem(GAME_TRANSFER_KEY);
      try {
        const data = JSON.parse(pendingRaw);
        setLibrary({ title: data.title, result: data.result });
        if (data.format === GAME_TRANSFER_FORMAT) {
          applyLoadedTree(unpackGameTree(data), data.currentPath, data.mapSize, data.mapSeed, true);
        } else if (data.format === 'gamepage') {
          loadImportedGameTree(data);   // older full-snapshot format
        }
      } catch (e) {
        console.error('Failed to load game import', e);
        genError = 'Could not load the game into the analysis board.';
      }
    } else if (urlShareId && saved?.library?.shareId !== urlShareId) {
      // A share link (someone else's entry, or our own): fetch it. When the saved session is
      // already this entry, it's restored instead, with any unsaved moves.
      getSharedLibraryGame(urlShareId)
        .then(openLibraryEntry)
        .catch(e => { restoreSession(saved); genError = e.message; })
        .finally(() => _persistenceReady = true);
      return cleanup;
    } else {
      restoreSession(saved);
    }
    _persistenceReady = true;
    return cleanup;
  });
</script>

<svelte:window onkeydown={handleKeydown} />

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

<!-- Mobile only (between the move nav buttons and the move list): best line + engine buttons -->
{#snippet mobileEngineBar()}
  <div class="mobile-only mobile-engine">
    <span class="mobile-engine-info">
      {#if analysisResults.length > 0}
        <span class="move-score">{formatEval(analysisResults[0])}</span>
        <span class="move-notation">{toNotation(analysisResults[0])}</span>
      {:else}
        <span class="mobile-engine-idle">{analyzing ? 'Analyzing…' : 'Engine'}</span>
      {/if}
    </span>
    <button class="ctrl-btn" onclick={toggleAnalysis} disabled={!mapSize}>{analyzing ? 'Stop' : 'Analyze'}</button>
    <button class="ctrl-btn" onclick={engineMove} disabled={!mapSize || engineThinking}>{engineThinking ? '…' : 'Move'}</button>
  </div>
{/snippet}

<div class="sandbox-wrap">
  <!-- Board column: centers the board+movelist unit -->
  <div class="board-col">
    {#if squares.length > 0}
      <!-- board-and-list keeps board and move list flush together as one unit -->
      <div class="board-and-list">
        <div class="board-wrap" use:wheelNav={stepMove}>
          <Board
            squares={squares}
            mapSize={mapSize}
            isWhite={viewAsWhite}
            selectedSquare={selectedSquare}
            {legalDests}
            dragOverSquare={dragOverSq}
            {hoverSquares}
            lastMoveSquares={lastMoveSquares}
            {suggestion}
            onPieceGrabbed={handleGrab}
            onDropOnBoard={handleDrop}
            onHoverSquare={handleHover}
            onSquareClick={handleSquareClick}
            onCanvasReady={(el) => { boardCanvas = el; }}
          />
        </div>
        <!-- Mobile only: gold counts and the shop of the side to move, right under the board -->
        <div class="mobile-only mobile-gold">
          <div class="mobile-gold-side" class:active={whiteIsActive}>
            <span class="player-name">White</span>
            <input class="gold-input" type="number" min="0" max="9999" bind:value={whiteGold} />
          </div>
          <div class="mobile-gold-side" class:active={!whiteIsActive}>
            <span class="player-name">Black</span>
            <input class="gold-input" type="number" min="0" max="9999" bind:value={blackGold} />
          </div>
        </div>
        <div class="mobile-only shop-row mobile-shop">
          {#each SHOP as item}
            {@const gold = whiteIsActive ? whiteGold : blackGold}
            <button
              class="shop-piece"
              class:active={dragging?.type === 'place' && dragging.pieceType === item.type}
              disabled={gold < item.cost}
              title="{item.type} — {item.cost}g"
              onpointerdown={(e) => shopPointerDown(e, item.type, whiteIsActive)}
            >
              <img src={pieceImgSrc(item.type, whiteIsActive)} alt={item.type} width="32" height="32" draggable="false" />
              <span class="cost" class:dim={gold < item.cost}>{item.cost}</span>
            </button>
          {/each}
        </div>
        {#if moveTree}
          <div class="movelist-col">
            <MoveList
              rootNode={moveTree}
              activeNode={currentNode}
              {treeRevision}
              replayMode={true}
              onNavigate={navigateToNode}
              belowNav={mobileEngineBar}
            />
          </div>
        {/if}
      </div>
    {:else}
      <div class="empty-board">
        <p>Generate a board to start</p>
      </div>
    {/if}
  </div>

  <!-- Right: controls -->
  <aside class="side-panel">

    <!-- Player bars (desktop; mobile has the compact gold bar under the board) -->
    <div class="players desktop-only">
      <div class="player-bar" class:active={whiteIsActive}>
        <span class="player-name">White</span>
        <div class="gold-wrap">
          <input class="gold-input" type="number" min="0" max="9999"
                 bind:value={whiteGold} />
          <span class="gold-unit">gold</span>
        </div>
      </div>
      <div class="player-bar" class:active={!whiteIsActive}>
        <span class="player-name">Black</span>
        <div class="gold-wrap">
          <input class="gold-input" type="number" min="0" max="9999"
                 bind:value={blackGold} />
          <span class="gold-unit">gold</span>
        </div>
      </div>
    </div>

    <!-- Turn + board controls -->
    <div class="ctrl-row">
      <button class="ctrl-btn" onclick={passTurn} disabled={!squares.length}>Pass turn</button>
      <button class="ctrl-btn" onclick={() => viewAsWhite = !viewAsWhite} disabled={!squares.length}>Flip</button>
    </div>
    <!-- Shop: place pieces (desktop; mobile has the shop under the board) -->
    <div class="section desktop-only">
      <div class="section-label">Place piece</div>
      <div class="shop-label">White</div>
      <div class="shop-row">
        {#each SHOP as item}
          {@const canAfford = whiteGold >= item.cost}
          <button
            class="shop-piece"
            class:active={dragging?.type === 'place' && dragging.pieceType === item.type && dragging.isWhitePiece}
            disabled={!squares.length || !canAfford || !whiteIsActive}
            title="{item.type} — {item.cost}g"
            onpointerdown={(e) => shopPointerDown(e, item.type, true)}
          >
            <img src={pieceImgSrc(item.type, true)} alt={item.type} width="32" height="32" draggable="false" />
            <span class="cost" class:dim={!canAfford}>{item.cost}</span>
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
            disabled={!squares.length || !canAfford || whiteIsActive}
            title="{item.type} — {item.cost}g"
            onpointerdown={(e) => shopPointerDown(e, item.type, false)}
          >
            <img src={pieceImgSrc(item.type, false)} alt={item.type} width="32" height="32" draggable="false" />
            <span class="cost" class:dim={!canAfford}>{item.cost}</span>
          </button>
        {/each}
      </div>
      <p class="shop-hint">Drag a piece onto the board to place it.</p>
    </div>

    <!-- My library -->
    {#if moveTree && (authState.token || libraryShareId)}
      <div class="section">
        <div class="section-label">Library</div>
        {#if libraryOwner}
          <p class="library-owner">
            Shared by <a href="/profile/{encodeURIComponent(libraryOwner.username)}"
              onclick={(e) => { e.preventDefault(); navigate(`/profile/${encodeURIComponent(libraryOwner.username)}`); }}
            >{libraryOwner.name}</a>
          </p>
        {/if}
        <input class="title-input" type="text" bind:value={libraryTitle} maxlength="200"
               placeholder="Title (optional)" spellcheck="false" />
        {#if authState.token}
          <button class="action-btn" onclick={saveToLibrary} disabled={librarySaving}>
            {librarySaving ? 'Saving…'
              : libraryOwned ? 'Save changes to library'
              : libraryShareId ? 'Save to my library' : 'Save to library'}
          </button>
        {:else}
          <button class="action-btn" onclick={() => navigate('/login')}>Log in to save to your library</button>
        {/if}
        <div class="share-row">
          <label class="switch" class:disabled={!libraryOwned}
                 title={libraryOwned ? 'Anyone with the link can open this game' : 'Save to your library first'}>
            <input type="checkbox" role="switch" checked={libraryPublic} disabled={!libraryOwned} onchange={togglePublic} />
            <span class="track"><span class="thumb"></span></span>
            Publish
          </label>
          <button class="ctrl-btn copy-btn" onclick={copyShareLink} disabled={!libraryShareId}>
            {linkCopied ? 'Copied' : 'Copy link'}
          </button>
        </div>
        {#if libraryMsg}<p class:error={libraryMsg !== 'Saved ✓'} class="library-msg">{libraryMsg}</p>{/if}
      </div>
    {/if}

    <!-- Map generation -->
    <div class="section map-section">
      <div class="section-label">Map</div>
      <div class="option-row">
        <label>Size: <strong>{genSize}×{genSize}</strong></label>
        <input type="range" min="6" max="16" step="2" bind:value={genSize} />
      </div>
      <label class="checkbox-label">
        Full random
        <input type="checkbox" bind:checked={genRandom} />
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

    <!-- On mobile only the line list is shown here (buttons and best line sit under the board) -->
    <div class="section" class:desktop-only={!analysisResults.length && !engineError}>
      <div class="section-label">Engine</div>
      <div class="ctrl-row desktop-only">
        <button class="action-btn grow" onclick={toggleAnalysis} disabled={!mapSize}>
          {analyzing ? "Stop" : "Analyze"}
        </button>
        <button class="ctrl-btn" onclick={engineMove} disabled={!mapSize || engineThinking}
                title={analyzing ? 'Play the top move' : 'Think (600k nodes) and play a move'}>
          {engineThinking ? 'Thinking…' : 'Engine move'}
        </button>
      </div>
      {#if engineError}<p class="error">{engineError}</p>{/if}
      {#if analysisResults.length > 0}
        <div class="analysis-info">
          <div class="analysis-depth">Depth: {analysisDepth}</div>
          <div class="analysis-moves">
            {#each analysisResults as move}
              <button
                class="analysis-move"
                onmouseenter={() => handleAnalysisMoveHover(move)}
                onmouseleave={() => handleAnalysisMoveHover(null)}
                title={move.pv?.join(' ')}
              >
                <span class="move-notation">{toNotation(move)}</span>
                <span class="move-score">{formatEval(move)}</span>
              </button>
            {/each}
          </div>
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
    filter: drop-shadow(0 4px 10px rgba(0,0,0,0.7));
  }

  .sandbox-wrap {
    display: flex;
    flex: 1;
    overflow: hidden;
    height: calc(100vh - 46px);
  }

  /* ── Board column: centers the board+movelist unit ── */
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

  /* board-and-list: board and move list as one flush unit, centered in board-col */
  .board-and-list {
    display: flex;
    align-items: center;
    height: 100%;       /* fill board-col inner height */
    max-width: 100%;    /* don't overflow board-col */
  }

  /* Board: height-based square. flex-shrink lets it compress when
     board + movelist would exceed the available column width. */
  .board-wrap {
    height: 100%;
    aspect-ratio: 1;
    flex-shrink: 1;
    min-width: 0;
  }

  .empty-board {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 100%;
    max-width: 600px;
    aspect-ratio: 1;
    border: 2px dashed var(--line);
    border-radius: 8px;
    color: var(--faint);
    font-size: 0.95rem;
  }

  /* ── Move list column ── */
  .movelist-col {
    width: 280px;
    flex-shrink: 0;
    align-self: stretch;
    display: flex;
    flex-direction: column;
    background: var(--surface);
    overflow: hidden;
  }

  /* ── Side panel ── */
  .side-panel {
    width: 270px;
    flex-shrink: 0;
    display: flex;
    flex-direction: column;
    gap: 0.9rem;
    padding: 1rem;
    border-left: 1px solid var(--line);
    background: var(--surface);
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
    border: 1px solid var(--line);
    background: var(--surface);
    transition: border-color 0.15s;
  }
  .player-bar.active { border-color: var(--accent); background: var(--surface-3); }
  .player-name { font-size: 0.9rem; font-weight: 600; color: var(--fg-2); }
  .gold-wrap { display: flex; align-items: center; gap: 0.25rem; }
  .title-input {
    width: 100%; box-sizing: border-box; padding: 0.35rem 0.5rem; font: inherit; font-size: 0.85rem;
    background: var(--surface-2, var(--surface)); color: var(--fg); border: 1px solid var(--line); border-radius: 4px;
  }
  .library-msg { margin: 0; font-size: 0.82rem; color: var(--muted); }
  .library-owner { margin: 0; font-size: 0.85rem; color: var(--muted); }
  .library-owner a { color: var(--accent); }
  .share-row { display: flex; align-items: center; justify-content: space-between; gap: 0.5rem; }
  .copy-btn { flex: 0 0 auto; }
  /* Toggle switch (a checkbox underneath) */
  .switch {
    display: flex; align-items: center; gap: 0.5rem; cursor: pointer;
    font-size: 0.88rem; color: var(--fg-2); user-select: none;
  }
  .switch.disabled { opacity: 0.45; cursor: default; }
  .switch input { position: absolute; opacity: 0; width: 0; height: 0; }
  .track {
    position: relative; width: 2.1rem; height: 1.2rem; border-radius: 0.6rem; flex: none;
    background: var(--surface-2); border: 1px solid var(--line-strong);
    transition: background 0.15s, border-color 0.15s;
  }
  .thumb {
    position: absolute; top: 50%; left: 0.15rem; width: 0.8rem; height: 0.8rem; border-radius: 50%;
    background: var(--muted); transform: translateY(-50%); transition: left 0.15s, background 0.15s;
  }
  .switch input:checked + .track { background: var(--button); border-color: var(--button); }
  .switch input:checked + .track .thumb { left: calc(100% - 0.95rem); background: #fff; }
  .switch input:focus-visible + .track { outline: 2px solid var(--accent); outline-offset: 2px; }
  .gold-input {
    width: 62px;
    background: var(--surface-2); border: 1px solid var(--line-strong); border-radius: 4px;
    color: #f0c040; font-size: 0.9rem; padding: 0.2rem 0.4rem; text-align: right;
  }
  .gold-unit { color: var(--muted); font-size: 0.85rem; }

  /* ── Control buttons ── */
  .ctrl-row { display: flex; gap: 0.4rem; }
  .ctrl-btn {
    flex: 1; padding: 0.4rem 0.6rem; font-size: 0.82rem; white-space: nowrap;
    background: var(--surface-2); border: 1px solid var(--line-strong); color: var(--fg-2); border-radius: 5px;
  }
  .ctrl-btn:hover:not(:disabled) { background: var(--surface-3); color: var(--fg); }
  .ctrl-btn:disabled { opacity: 0.35; cursor: default; }
  .ctrl-btn.danger { color: #e07070; border-color: #5a3a3a; }
  .ctrl-btn.danger:hover:not(:disabled) { background: #3a2a2a; }

  /* ── Sections ── */
  .section {
    display: flex; flex-direction: column; gap: 0.5rem;
    padding-top: 0.6rem; border-top: 1px solid var(--line);
  }
  .map-section { gap: 0.9rem; }
  .section-label {
    font-size: 0.76rem; text-transform: uppercase; letter-spacing: 0.07em; color: var(--faint);
  }

  /* ── Shop ── */
  .shop-label { font-size: 0.8rem; color: var(--muted); margin-bottom: -0.2rem; }
  .shop-row { display: flex; gap: 0.2rem; }
  .shop-piece {
    display: flex; flex-direction: column; align-items: center;
    padding: 0.2rem 0.15rem;
    background: var(--surface-2); border: 2px solid transparent; border-radius: 7px;
    gap: 2px; touch-action: none; user-select: none; flex: 1; min-width: 0;
    transition: border-color 0.1s, background 0.1s;
  }

  .shop-piece:not(:disabled):hover { border-color: var(--accent); background: var(--surface-3); }
  .shop-piece.active { border-color: #f5c518; background: #3a3a20; }
  .shop-piece:disabled { opacity: 0.3; cursor: default; }
  .cost { font-size: 0.8rem; font-weight: 700; color: #f5c518; font-variant-numeric: tabular-nums; }
  .cost.dim { color: var(--faint); }
  .shop-hint { font-size: 0.8rem; color: var(--faint); margin: 0; line-height: 1.4; }

  /* ── Map generation ── */
  .option-row {
    display: flex; align-items: center; justify-content: space-between;
    gap: 0.5rem; font-size: 0.88rem; color: var(--fg-2);
  }
  .option-row input[type="range"] { flex: 1; accent-color: var(--accent); }
  .checkbox-label {
    display: flex; align-items: center; justify-content: space-between; gap: 0.5rem;
    font-size: 0.88rem; color: var(--fg-2); cursor: pointer;
  }
  .seed-row { display: flex; }
  .seed-input {
    width: 100%; background: var(--surface-2); border: 1px solid var(--line-strong); border-radius: 5px;
    color: var(--fg); font-size: 0.8rem; font-family: monospace; padding: 0.35rem 0.6rem;
  }
  .seed-input.seed-invalid { border-color: #c05050; }
  .seed-input.seed-ok      { border-color: #4a9a4a; }

  .action-btn {
    padding: 0.65rem; font-size: 0.95rem;
    background: var(--button); color: #fff; border: none; border-radius: 6px;
  }
  .action-btn:disabled { opacity: 0.45; cursor: default; }

  .error { color: #e07070; font-size: 0.8rem; margin: 0; }

  /* ── Engine ── */
  .grow { flex: 2; }

  /* ── Analysis ── */
  .analysis-info {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
    margin-top: 0.3rem;
  }
  .analysis-depth {
    font-size: 0.82rem;
    color: var(--muted);
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
    background: var(--surface-2);
    border: 1px solid transparent;
    border-radius: 4px;
    color: var(--fg-2);
    font-size: 0.85rem;
    font-family: var(--font-body);
    font-variant-numeric: tabular-nums;
    cursor: default;
    transition: background 0.1s, border-color 0.1s;
    width: 100%;
    text-align: left;
  }
  .analysis-move:hover {
    background: rgba(216, 166, 74, 0.25);
    border-color: var(--accent);
    color: #fff;
  }
  .move-notation {
    flex: 1;
  }
  .move-score {
    color: #f5c518;
    font-weight: 600;
  }

  /* ── Mobile-only bars (gold, shop, engine) ── */
  .mobile-only { display: none; }
  .mobile-gold { gap: 0.4rem; padding: 0.4rem 0; }
  .mobile-gold-side {
    flex: 1; display: flex; align-items: center; justify-content: space-between; gap: 0.4rem;
    padding: 0.3rem 0.6rem; border: 1px solid var(--line); border-radius: 6px; background: var(--surface);
  }
  .mobile-gold-side.active { border-color: var(--accent); background: var(--surface-3); }
  .mobile-shop { padding-bottom: 0.4rem; }
  .mobile-engine {
    align-items: center; gap: 0.4rem;
    padding: 0.35rem 0.5rem; border-bottom: 1px solid var(--line);
  }
  .mobile-engine-info {
    flex: 1; min-width: 0; display: flex; gap: 0.5rem; align-items: baseline;
    font-size: 0.9rem; font-variant-numeric: tabular-nums; white-space: nowrap; overflow: hidden;
  }
  .mobile-engine-idle { color: var(--faint); }
  .mobile-engine .ctrl-btn { flex: 0 0 auto; padding: 0.35rem 0.75rem; }

  /* ── Mobile: stack vertically ── */
  @media (max-width: 640px) {
    .mobile-only { display: flex; }
    .desktop-only { display: none; }
    .sandbox-wrap {
      flex-direction: column;
      height: auto;
      overflow: visible;
    }
    .board-col {
      min-height: unset;
      padding: 0.5rem;
    }
    .board-and-list {
      flex-direction: column;
      height: auto;
      align-items: stretch;
    }
    /* On mobile the board is constrained by width, not height */
    .board-wrap { height: auto; width: 100%; }
    .movelist-col {
      width: 100%;
      max-height: 240px;
      border-top: 1px solid var(--line);
    }
    .side-panel {
      width: 100%;
      border-left: none;
      border-top: 1px solid var(--line);
      overflow: visible;
    }
  }
</style>
