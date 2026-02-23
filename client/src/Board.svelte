<script>
  import { onMount } from 'svelte';

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
   */
  let {
    squares       = [],
    mapSize       = 12,
    isWhite       = true,
    selectedSquare  = null,
    legalDests      = [],
    dragOverSquare  = null,
    hoverSquares    = [],   // {x,y}[] highlighted from move-list hover
    lastMoveSquares = [],   // {x,y}[] from/to of the last move played
    showCoords      = true,
    onPieceGrabbed  = null,
    onDropOnBoard   = null,
    onHoverSquare   = null,
    onSquareClick   = null,
    onCanvasReady   = null,   // passes canvas element to parent
  } = $props();

  let canvas = $state(null);
  const images = new Map();

  // ── Sprite helpers ──────────────────────────────────────────────────────

  function getPieceSprite(piece) {
    if (piece.type === 'Treasure') return '/assets/objects/treasure.png';
    const c = piece.isWhite ? 'w' : 'b';
    const n = piece.type.replace(/^(White|Black)/, '').toLowerCase();
    return `/assets/objects/${c}_${n}.png`;
  }

  function getSquareSprite(type) {
    return `/assets/squares/${type.replace(/([A-Z])/g, '_$1').toLowerCase().replace(/^_/, '')}.png`;
  }

  // ── Image cache ─────────────────────────────────────────────────────────

  function loadImage(src) {
    if (images.has(src)) return Promise.resolve(images.get(src));
    return new Promise(resolve => {
      const img = new Image();
      img.onload  = () => { images.set(src, img);  resolve(img);  };
      img.onerror = () => { images.set(src, null); resolve(null); };
      img.src = src;
    });
  }

  async function preloadAll() {
    const srcs = new Set();
    for (const sq of squares) {
      srcs.add(getSquareSprite(sq.type));
      if (sq.piece) srcs.add(getPieceSprite(sq.piece));
    }
    await Promise.all([...srcs].map(loadImage));
  }

  // ── Highlights ──────────────────────────────────────────────────────────

  const SERVER_HIGHLIGHTS = {
    Red:    'rgba(220,  50,  50, 0.60)',
    Green:  'rgba( 50, 200,  50, 0.50)',
    Blue:   'rgba( 50, 100, 255, 0.45)',
    Purple: 'rgba(160,  50, 220, 0.50)',
    Orange: 'rgba(230, 130,  20, 0.55)',
  };

  const legalSet    = $derived(new Set(legalDests.map(d => `${d.x},${d.y}`)));
  const hoverSet    = $derived(new Set(hoverSquares.map(d => `${d.x},${d.y}`)));
  const lastMoveSet = $derived(new Set(lastMoveSquares.map(d => `${d.x},${d.y}`)));

  // ── Draw ────────────────────────────────────────────────────────────────

  function draw() {
    if (!canvas || !squares.length) return;
    const n = mapSize;

    // Size canvas to device pixels so no CSS scaling occurs (avoids blur on HiDPI)
    const dpr    = window.devicePixelRatio || 1;
    const cssW   = canvas.clientWidth;
    if (!cssW) return;
    canvas.width  = Math.round(cssW * dpr);
    canvas.height = Math.round(cssW * dpr);

    const ctx  = canvas.getContext('2d');
    ctx.imageSmoothingEnabled = false;

    // Integer pixel rect for grid cell — compute each edge independently so
    // adjacent cells share exactly one pixel boundary (no gaps, no overlaps).
    const ex = (d) => Math.round(d * canvas.width  / n);  // left edge of column d
    const ey = (d) => Math.round(d * canvas.height / n);  // top edge of row d

    for (const sq of squares) {
      // White: Y=0 is their home rank → flip Y so it sits at canvas bottom.
      //        X is left-to-right as-is.
      // Black: board is rotated 180°, so flip X; Y=0 (white home) ends up at canvas top.
      const dx = isWhite ? sq.x         : n - 1 - sq.x;
      const dy = isWhite ? n - 1 - sq.y : sq.y;
      const px = ex(dx), py = ey(dy);
      const cw = ex(dx + 1) - px;   // this cell's pixel width
      const ch = ey(dy + 1) - py;   // this cell's pixel height (may differ by 1px from cw)

      // Background tile
      const img = images.get(getSquareSprite(sq.type));
      if (img) ctx.drawImage(img, px, py, cw, ch);
      else { ctx.fillStyle = '#556677'; ctx.fillRect(px, py, cw, ch); }

      // Last-move highlight (subtle warm yellow, drawn early so other overlays sit on top)
      if (lastMoveSet.has(`${sq.x},${sq.y}`)) {
        ctx.fillStyle = 'rgba(200, 175, 0, 0.30)';
        ctx.fillRect(px, py, cw, ch);
      }

      // Server highlight (check, etc.)
      const sc = SERVER_HIGHLIGHTS[sq.highlight];
      if (sc) { ctx.fillStyle = sc; ctx.fillRect(px, py, cw, ch); }

      // Legal destination hint (green dot)
      if (legalSet.has(`${sq.x},${sq.y}`)) {
        ctx.fillStyle = 'rgba(50, 220, 50, 0.45)';
        ctx.fillRect(px, py, cw, ch);
        if (!sq.piece) {
          ctx.fillStyle = 'rgba(30, 180, 30, 0.7)';
          ctx.beginPath();
          ctx.arc(px + cw / 2, py + ch / 2, Math.min(cw, ch) * 0.18, 0, Math.PI * 2);
          ctx.fill();
        }
      }

      // Selected square (yellow)
      if (selectedSquare && sq.x === selectedSquare.x && sq.y === selectedSquare.y) {
        ctx.fillStyle = 'rgba(255, 220, 0, 0.55)';
        ctx.fillRect(px, py, cw, ch);
      }

      // Drag-over target (bright yellow outline)
      if (dragOverSquare && sq.x === dragOverSquare.x && sq.y === dragOverSquare.y) {
        ctx.fillStyle = 'rgba(255, 255, 80, 0.45)';
        ctx.fillRect(px, py, cw, ch);
        ctx.strokeStyle = 'rgba(255, 230, 0, 0.9)';
        ctx.lineWidth = Math.round(3 * dpr);
        ctx.strokeRect(px + 1, py + 1, cw - 2, ch - 2);
      }

      // Move-list hover highlight (blue tint + outline)
      if (hoverSet.has(`${sq.x},${sq.y}`)) {
        ctx.fillStyle = 'rgba(60, 140, 255, 0.35)';
        ctx.fillRect(px, py, cw, ch);
        ctx.strokeStyle = 'rgba(80, 160, 255, 0.75)';
        ctx.lineWidth = Math.round(2 * dpr);
        ctx.strokeRect(px + 1, py + 1, cw - 2, ch - 2);
      }

      // Piece sprite with inset so pieces don't fill the entire cell
      const isGrabbed = selectedSquare && sq.x === selectedSquare.x && sq.y === selectedSquare.y && _dragging;
      if (sq.piece) {
        const pi = images.get(getPieceSprite(sq.piece));
        if (pi) {
          const pad = Math.round(Math.min(cw, ch) * 0.1);
          if (isGrabbed) ctx.globalAlpha = 0.3;
          ctx.drawImage(pi, px + pad, py + pad, cw - pad * 2, ch - pad * 2);
          if (isGrabbed) ctx.globalAlpha = 1.0;
        }
      }

      // Mine income: +5 in top-right corner (colored by owner)
      if (sq.type.endsWith('Mine') && sq.mineOwner) {
        const sz = Math.max(7 * dpr, Math.round(Math.min(cw, ch) * 0.18));
        ctx.font = `bold ${sz}px sans-serif`;
        ctx.textAlign = 'right';
        ctx.textBaseline = 'top';
        ctx.fillStyle = sq.mineOwner === 'white' ? '#fff' : '#000';
        ctx.fillText('+5', px + cw - Math.round(3 * dpr), py + Math.round(3 * dpr));
      }
    }

    // ── Coordinate labels ─────────────────────────────────────────────────
    if (showCoords) {
      const cellPx = canvas.width / n;
      const fontSize = Math.max(9 * dpr, Math.round(cellPx * 0.26));
      ctx.font = `bold ${fontSize}px sans-serif`;

      // File labels along the bottom row (right-aligned inside each cell)
      ctx.textAlign    = 'right';
      ctx.textBaseline = 'bottom';
      for (let dx = 0; dx < n; dx++) {
        // x=0 → 'a', x=1 → 'b', … matches Move.cs XToLetter convention.
        // For white, canvas col dx == board x. For black, board x == n-1-dx.
        const bx   = isWhite ? dx : n - 1 - dx;
        const file = String.fromCharCode(bx + 97);  // 'a'=x=0
        const fx   = ex(dx + 1) - Math.round(2 * dpr);
        const fy   = ey(n)      - Math.round(2 * dpr);
        ctx.fillStyle = 'rgba(0,0,0,0.55)';
        ctx.fillText(file, fx + 1, fy + 1);
        ctx.fillStyle = 'rgba(255,255,255,0.75)';
        ctx.fillText(file, fx, fy);
      }

      // Rank labels along the left column (top-aligned inside each cell)
      ctx.textAlign    = 'left';
      ctx.textBaseline = 'top';
      for (let dy = 0; dy < n; dy++) {
        // Rank = board y + 1 (1-indexed). For white, board y = n-1-dy; for black, board y = dy.
        const by   = isWhite ? n - 1 - dy : dy;
        const rank = String(by + 1);
        const rx   = ex(0) + Math.round(2 * dpr);
        const ry   = ey(dy) + Math.round(2 * dpr);
        ctx.fillStyle = 'rgba(0,0,0,0.55)';
        ctx.fillText(rank, rx + 1, ry + 1);
        ctx.fillStyle = 'rgba(255,255,255,0.75)';
        ctx.fillText(rank, rx, ry);
      }
    }
  }

  $effect(() => {
    if (squares.length && canvas) preloadAll().then(draw);
  });

  // Re-draw when highlights change without squares changing
  $effect(() => {
    void legalSet; void selectedSquare; void dragOverSquare; void hoverSet; void showCoords; void lastMoveSet;
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
    image-rendering: pixelated;
    image-rendering: crisp-edges;
    touch-action: none;
  }
  canvas:active { cursor: grabbing; }
</style>
