<script>
  import { onMount } from 'svelte';
  import { settings } from './lib/settings.svelte.js';
  import {
    getBoardTheme, pieceSrc, themeImageSrcs,
    drawSquare, drawTerrain, drawTreasure, drawMineBadge,
  } from './lib/boardTheme.js';

  /**
   * Props:
   *   squares        – SquareDto[]
   *   mapSize        – number
   *   isWhite        – bool  (flips board for black)
   *   selectedSquare – {x,y}|null  (yellow)
   *   legalDests     – {x,y}[]     (green – legal move/place hints)
   *   dragOverSquare – {x,y}|null  (bright yellow – hover target during drag)
   *
   * Callbacks:
   *   onPieceGrabbed(x, y, clientX, clientY)  – pointer-down on own piece
   *   onDropOnBoard(x, y)                     – pointer-up anywhere on canvas
   *   onHoverSquare(x, y | null)              – pointer-move (null = left canvas)
   *   onSquareClick(x, y)                     – tap/click without drag
   *
   * The look (square theme, artwork) comes from lib/boardTheme.js and settings.boardTheme.
   */
  let {
    squares       = [],
    mapSize       = 12,
    isWhite       = true,
    selectedSquare  = null,
    legalDests      = [],
    dragOverSquare  = null,
    hoverSquares    = [],   // {x,y}[] from move-list hover: [from, to] → black arrow, [to] (placement) → black ring
    premoveSquares  = [],   // premove: [from, to] → yellow arrow, [to] (placement) → yellow ring
    lastMoveSquares = [],   // {x,y}[] from/to of the last move played
    suggestion      = null, // engine move to show: { type:'m', fromPos, toPos } | { type:'p', pieceType, toPos, isWhite }
    showCoords      = true,
    onPieceGrabbed  = null,
    onDropOnBoard   = null,
    onHoverSquare   = null,
    onSquareClick   = null,
    onCanvasReady   = null,   // passes canvas element to parent
  } = $props();

  let canvas = $state(null);
  const theme = $derived(getBoardTheme(settings.boardTheme));

  // ── Image cache ─────────────────────────────────────────────────────────

  const images = new Map();   // src → Image | null (failed)
  const img = (src) => images.get(src) ?? null;

  function loadImage(src) {
    if (images.has(src)) return Promise.resolve(images.get(src));
    return new Promise(resolve => {
      const im = new Image();
      im.onload  = () => { images.set(src, im);   resolve(im);   };
      im.onerror = () => { images.set(src, null); resolve(null); };
      im.src = src;
    });
  }

  async function preloadAll() {
    const srcs = new Set(themeImageSrcs(theme));
    for (const sq of squares) {
      if (sq.piece && sq.piece.type !== 'Treasure') srcs.add(pieceSrc(sq.piece.type, sq.piece.isWhite));
    }
    await Promise.all([...srcs].map(loadImage));
  }

  // ── Highlights ──────────────────────────────────────────────────────────

  const SERVER_HIGHLIGHTS = {
    Red:    'rgba(220,  50,  50, 0.75)',
    Green:  'rgba( 50, 200,  50, 0.50)',
    Blue:   'rgba( 50, 100, 255, 0.45)',
    Purple: 'rgba(160,  50, 220, 0.50)',
    Orange: 'rgba(230, 130,  20, 0.55)',
  };

  const legalSet    = $derived(new Set(legalDests.map(d => `${d.x},${d.y}`)));
  const lastMoveSet = $derived(new Set(lastMoveSquares.map(d => `${d.x},${d.y}`)));

  // ── Draw ────────────────────────────────────────────────────────────────
  // Squares and terrain don't change during a game but are the expensive part (textures, shadows,
  // vector artwork), so they're rendered once into two offscreen layers and copied in on each
  // redraw. Pieces and the chest are cached as bitmaps per size. All of it is rebuilt when the
  // board size, orientation, theme, terrain or loaded images change.

  let layers = null;          // { key, squares: canvas, terrain: canvas }
  const sprites = new Map();  // `${src}|${size}` → canvas
  let spriteSize = 0;

  function newCanvas(w, h) {
    const c = document.createElement('canvas');
    c.width = w; c.height = h;
    const g = c.getContext('2d');
    g.imageSmoothingEnabled = true;
    g.imageSmoothingQuality = 'high';
    return c;
  }

  /** A bitmap of `size`×`size` drawn once by `paint(ctx)`. */
  function sprite(key, size, paint) {
    const k = `${key}|${size}`;
    let c = sprites.get(k);
    if (!c) { c = newCanvas(size, size); paint(c.getContext('2d')); sprites.set(k, c); }
    return c;
  }

  function draw() {
    if (!canvas || !squares.length) return;
    const n = mapSize;

    // Size canvas to device pixels so no CSS scaling occurs (avoids blur on HiDPI).
    // Only on a change: setting the size reallocates the canvas.
    const dpr    = window.devicePixelRatio || 1;
    const cssW   = canvas.clientWidth;
    if (!cssW) return;
    const size = Math.round(cssW * dpr);
    if (canvas.width !== size || canvas.height !== size) { canvas.width = size; canvas.height = size; }

    const ctx  = canvas.getContext('2d');
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = 'high';

    // Integer pixel rect for grid cell — compute each edge independently so
    // adjacent cells share exactly one pixel boundary (no gaps, no overlaps).
    const ex = (d) => Math.round(d * canvas.width  / n);  // left edge of column d
    const ey = (d) => Math.round(d * canvas.height / n);  // top edge of row d

    // White: Y=0 is their home rank → flip Y so it sits at canvas bottom.
    //        X is left-to-right as-is.
    // Black: board is rotated 180°, so flip X; Y=0 (white home) ends up at canvas top.
    const cellRect = (sq) => {
      const dx = isWhite ? sq.x         : n - 1 - sq.x;
      const dy = isWhite ? n - 1 - sq.y : sq.y;
      const px = ex(dx), py = ey(dy);
      return [px, py, ex(dx + 1) - px, ey(dy + 1) - py];   // cells may differ by 1px in size
    };

    const layerKey = `${size}|${n}|${isWhite}|${theme.id}|${images.size}|${squares.map(sq => sq.type).join()}`;
    if (layers?.key !== layerKey) {
      const sqLayer = newCanvas(size, size), terrainLayer = newCanvas(size, size);
      const sg = sqLayer.getContext('2d'), tg = terrainLayer.getContext('2d');
      for (const sq of squares) {
        const [px, py, cw, ch] = cellRect(sq);
        drawSquare(sg, theme, img, (sq.x + sq.y) % 2 === 0, sq.x, sq.y, px, py, cw, ch);   // a1 is dark
        drawTerrain(tg, img, sq.type, px, py, cw);   // mountains, forests, mines
      }
      layers = { key: layerKey, squares: sqLayer, terrain: terrainLayer };
    }
    if (spriteSize !== size) { sprites.clear(); spriteSize = size; }

    ctx.drawImage(layers.squares, 0, 0);

    for (const sq of squares) {
      const [px, py, cw, ch] = cellRect(sq);
      const key = `${sq.x},${sq.y}`;

      // Last-move highlight (subtle warm yellow, drawn early so other overlays sit on top)
      if (lastMoveSet.has(key)) {
        ctx.fillStyle = 'rgba(220, 190, 40, 0.42)';
        ctx.fillRect(px, py, cw, ch);
      }

      // Server highlight (check, etc.)
      const sc = SERVER_HIGHLIGHTS[sq.highlight];
      if (sc) { ctx.fillStyle = sc; ctx.fillRect(px, py, cw, ch); }

      // Legal destination tint (the dot is drawn above the terrain icon, below)
      if (legalSet.has(key)) {
        ctx.fillStyle = 'rgba(50, 220, 50, 0.35)';
        ctx.fillRect(px, py, cw, ch);
      }

      // Selected square (yellow)
      if (selectedSquare && sq.x === selectedSquare.x && sq.y === selectedSquare.y) {
        ctx.fillStyle = 'rgba(255, 220, 0, 0.5)';
        ctx.fillRect(px, py, cw, ch);
      }

      // Drag-over target (bright yellow outline)
      if (dragOverSquare && sq.x === dragOverSquare.x && sq.y === dragOverSquare.y) {
        ctx.fillStyle = 'rgba(255, 255, 80, 0.4)';
        ctx.fillRect(px, py, cw, ch);
        ctx.strokeStyle = 'rgba(255, 230, 0, 0.9)';
        ctx.lineWidth = Math.round(3 * dpr);
        ctx.strokeRect(px + 1, py + 1, cw - 2, ch - 2);
      }
    }

    // Terrain above the highlights
    ctx.drawImage(layers.terrain, 0, 0);

    for (const sq of squares) {
      const [px, py, cw, ch] = cellRect(sq);
      const key = `${sq.x},${sq.y}`;

      // Legal destination dot on empty squares
      if (legalSet.has(key) && !sq.piece) {
        ctx.fillStyle = 'rgba(30, 150, 30, 0.75)';
        ctx.beginPath();
        ctx.arc(px + cw / 2, py + ch / 2, Math.min(cw, ch) * 0.15, 0, Math.PI * 2);
        ctx.fill();
      }

      // Treasure or piece
      const isGrabbed = selectedSquare && sq.x === selectedSquare.x && sq.y === selectedSquare.y && _dragging;
      if (sq.piece?.type === 'Treasure') {
        ctx.drawImage(sprite(`treasure|${images.size}`, cw, g => drawTreasure(g, img, 0, 0, cw)), px, py);   // rebuilt once its images load
      } else if (sq.piece) {
        const src = pieceSrc(sq.piece.type, sq.piece.isWhite);
        const pi = img(src);
        if (pi) {
          const s = Math.round(cw * 0.86);
          if (isGrabbed) ctx.globalAlpha = 0.3;
          ctx.drawImage(sprite(src, s, g => g.drawImage(pi, 0, 0, s, s)), px + Math.round((cw - s) / 2), py + Math.round((ch - s) / 2));
          if (isGrabbed) ctx.globalAlpha = 1.0;
        }
      }

      // Mine owner badge (top-right), above the piece so it stays visible
      if (sq.type.endsWith('Mine')) drawMineBadge(ctx, sq.mineOwner, px, py, cw);
    }

    // ── Coordinate labels ─────────────────────────────────────────────────
    if (showCoords) {
      const cellPx = canvas.width / n;
      const fontSize = Math.max(9 * dpr, Math.round(cellPx * 0.22));
      ctx.font = `bold ${fontSize}px "Alegreya Sans", sans-serif`;
      // Text contrasts with the square it sits on (a1 is dark)
      const labelColor = (bx, by) => ((bx + by) % 2 === 0) ? 'rgba(255,248,235,0.88)' : 'rgba(55,38,22,0.8)';

      // File labels along the bottom row (right-aligned inside each cell)
      ctx.textAlign    = 'right';
      ctx.textBaseline = 'bottom';
      for (let dx = 0; dx < n; dx++) {
        // x=0 → 'a', x=1 → 'b', … matches Move.cs XToLetter convention.
        // For white, canvas col dx == board x. For black, board x == n-1-dx.
        const bx = isWhite ? dx : n - 1 - dx;
        const by = isWhite ? 0 : n - 1;
        ctx.fillStyle = labelColor(bx, by);
        ctx.fillText(String.fromCharCode(bx + 97), ex(dx + 1) - Math.round(2 * dpr), ey(n) - Math.round(2 * dpr));
      }

      // Rank labels along the left column (top-aligned inside each cell)
      ctx.textAlign    = 'left';
      ctx.textBaseline = 'top';
      for (let dy = 0; dy < n; dy++) {
        // Rank = board y + 1 (1-indexed). For white, board y = n-1-dy; for black, board y = dy.
        const by = isWhite ? n - 1 - dy : dy;
        const bx = isWhite ? 0 : n - 1;
        ctx.fillStyle = labelColor(bx, by);
        ctx.fillText(String(by + 1), ex(0) + Math.round(2 * dpr), ey(dy) + Math.round(2 * dpr));
      }
    }

    drawSuggestion(ctx, n, ex, ey);
    drawMoveMarker(ctx, n, ex, ey, premoveSquares, PREMOVE_COLOR);
    drawMoveMarker(ctx, n, ex, ey, hoverSquares, HOVER_COLOR);
  }

  function squareCenter(p, n, ex, ey) {
    const dx = isWhite ? p.x : n - 1 - p.x;
    const dy = isWhite ? n - 1 - p.y : p.y;
    return [(ex(dx) + ex(dx + 1)) / 2, (ey(dy) + ey(dy + 1)) / 2];
  }

  function drawArrow(ctx, [x1, y1], [x2, y2], cell, color) {
    const ang  = Math.atan2(y2 - y1, x2 - x1);
    const head = cell * 0.38;
    ctx.save();
    ctx.strokeStyle = color;
    ctx.fillStyle   = color;
    ctx.lineWidth = cell * 0.13;
    ctx.lineCap = 'round';
    ctx.beginPath();
    ctx.moveTo(x1, y1);
    ctx.lineTo(x2 - Math.cos(ang) * head * 0.7, y2 - Math.sin(ang) * head * 0.7);
    ctx.stroke();
    ctx.beginPath();
    ctx.moveTo(x2, y2);
    ctx.lineTo(x2 - head * Math.cos(ang - 0.5), y2 - head * Math.sin(ang - 0.5));
    ctx.lineTo(x2 - head * Math.cos(ang + 0.5), y2 - head * Math.sin(ang + 0.5));
    ctx.closePath();
    ctx.fill();
    ctx.restore();
  }

  function drawRing(ctx, [x, y], cell, color) {
    ctx.save();
    ctx.strokeStyle = color;
    ctx.lineWidth = cell * 0.07;
    ctx.beginPath();
    ctx.arc(x, y, cell * 0.44, 0, Math.PI * 2);
    ctx.stroke();
    ctx.restore();
  }

  // Arrow for a move ([from, to]) or ring for a placement ([to]):
  // black for the move hovered in the move list, yellow for a premove.
  const HOVER_COLOR   = 'rgba(20, 16, 12, 0.72)';
  const PREMOVE_COLOR = 'rgba(240, 196, 30, 0.88)';
  function drawMoveMarker(ctx, n, ex, ey, squaresList, color) {
    const sqs = (squaresList ?? []).filter(Boolean);
    if (!sqs.length) return;
    const cell = canvas.width / n;
    if (sqs.length >= 2) drawArrow(ctx, squareCenter(sqs[0], n, ex, ey), squareCenter(sqs[1], n, ex, ey), cell, color);
    else drawRing(ctx, squareCenter(sqs[0], n, ex, ey), cell, color);
  }

  // Engine suggestion: arrow for a move, faded piece + ring for a placement
  function drawSuggestion(ctx, n, ex, ey) {
    if (!suggestion?.toPos) return;
    const center = (p) => {
      const dx = isWhite ? p.x : n - 1 - p.x;
      const dy = isWhite ? n - 1 - p.y : p.y;
      return [(ex(dx) + ex(dx + 1)) / 2, (ey(dy) + ey(dy + 1)) / 2];
    };
    const cell = canvas.width / n;
    const [x2, y2] = center(suggestion.toPos);
    ctx.save();
    ctx.strokeStyle = 'rgba(40, 160, 255, 0.8)';
    ctx.fillStyle   = 'rgba(40, 160, 255, 0.8)';

    if (suggestion.type === 'm' && suggestion.fromPos) {
      const [x1, y1] = center(suggestion.fromPos);
      const ang  = Math.atan2(y2 - y1, x2 - x1);
      const head = cell * 0.38;
      ctx.lineWidth = cell * 0.13;
      ctx.lineCap = 'round';
      ctx.beginPath();
      ctx.moveTo(x1, y1);
      ctx.lineTo(x2 - Math.cos(ang) * head * 0.7, y2 - Math.sin(ang) * head * 0.7);
      ctx.stroke();
      ctx.beginPath();
      ctx.moveTo(x2, y2);
      ctx.lineTo(x2 - head * Math.cos(ang - 0.5), y2 - head * Math.sin(ang - 0.5));
      ctx.lineTo(x2 - head * Math.cos(ang + 0.5), y2 - head * Math.sin(ang + 0.5));
      ctx.closePath();
      ctx.fill();
    } else if (suggestion.type === 'p') {
      const src = pieceSrc(suggestion.pieceType, suggestion.isWhite);
      const pi = images.get(src);
      if (pi === undefined) loadImage(src).then(draw);
      if (pi) {
        const size = cell * 0.8;
        ctx.globalAlpha = 0.55;
        ctx.drawImage(pi, x2 - size / 2, y2 - size / 2, size, size);
        ctx.globalAlpha = 1;
      }
      ctx.lineWidth = cell * 0.07;
      ctx.beginPath();
      ctx.arc(x2, y2, cell * 0.44, 0, Math.PI * 2);
      ctx.stroke();
    }
    ctx.restore();
  }

  $effect(() => {
    void theme;
    if (squares.length && canvas) preloadAll().then(draw);
  });

  // Re-draw when highlights change without squares changing
  $effect(() => {
    void legalSet; void selectedSquare; void dragOverSquare; void hoverSquares; void showCoords; void premoveSquares; void lastMoveSet; void suggestion;
    draw();
  });

  // ── Coordinate helpers ──────────────────────────────────────────────────

  function clientToBoard(clientX, clientY) {
    if (!canvas) return null;
    const rect = canvas.getBoundingClientRect();
    const cssCell = rect.width / mapSize;
    let cx = Math.floor((clientX - rect.left) / cssCell);
    let cy = Math.floor((clientY - rect.top)  / cssCell);
    // Invert to match draw logic above
    const bx = isWhite ? cx : mapSize - 1 - cx;
    const by = isWhite ? mapSize - 1 - cy : cy;
    if (bx < 0 || bx >= mapSize || by < 0 || by >= mapSize) return null;
    return { x: bx, y: by };
  }

  // ── Pointer events ──────────────────────────────────────────────────────

  let _dragging   = $state(false);   // are WE tracking a drag that started on the canvas?
  let _downPos    = null;             // { x, y, clientX, clientY } at pointerdown
  const DRAG_THRESHOLD = 6;          // pixels before it's considered a drag

  function handlePointerDown(e) {
    if (e.button !== undefined && e.button !== 0) return; // left button only
    const sq = clientToBoard(e.clientX, e.clientY);
    if (!sq) return;
    _downPos = { ...sq, clientX: e.clientX, clientY: e.clientY };
  }

  function handlePointerMove(e) {
    const sq = clientToBoard(e.clientX, e.clientY);
    onHoverSquare?.(sq);

    if (!_downPos || _dragging) return;
    const dx = e.clientX - _downPos.clientX;
    const dy = e.clientY - _downPos.clientY;
    if (Math.hypot(dx, dy) > DRAG_THRESHOLD) {
      _dragging = true;
      canvas.setPointerCapture(e.pointerId);
      onPieceGrabbed?.(_downPos.x, _downPos.y, _downPos.clientX, _downPos.clientY);
    }
  }

  function handlePointerUp(e) {
    const sq = clientToBoard(e.clientX, e.clientY);

    if (_dragging) {
      _dragging = false;
      if (sq) onDropOnBoard?.(sq.x, sq.y);
    } else if (_downPos && sq && sq.x === _downPos.x && sq.y === _downPos.y) {
      onSquareClick?.(sq.x, sq.y);
    }

    _downPos = null;
  }

  function handlePointerLeave() {
    onHoverSquare?.(null);
  }

  onMount(() => {
    onCanvasReady?.(canvas);
    const ro = new ResizeObserver(() => draw());
    ro.observe(canvas);
    return () => ro.disconnect();
  });
</script>

<canvas
  bind:this={canvas}
  onpointerdown={handlePointerDown}
  onpointermove={handlePointerMove}
  onpointerup={handlePointerUp}
  onpointerleave={handlePointerLeave}
></canvas>

<style>
  canvas {
    display: block;
    width: 100%;
    height: auto;
    cursor: grab;
    touch-action: none;
  }
  canvas:active { cursor: grabbing; }
</style>
