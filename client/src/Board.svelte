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
    onPieceGrabbed  = null,
    onDropOnBoard   = null,
    onHoverSquare   = null,
    onSquareClick   = null,
    onCanvasReady   = null,   // passes canvas element to parent
  } = $props();

  const CELL = 64;

  let canvas = $state(null);
  const images = new Map();

  // ── Sprite helpers ──────────────────────────────────────────────────────

  function getPieceSprite(piece) {
    if (piece.type === 'WhiteFlag' || piece.type === 'BlackFlag') return '/assets/objects/white_flag.png';
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

  const legalSet   = $derived(new Set(legalDests.map(d => `${d.x},${d.y}`)));

  // ── Draw ────────────────────────────────────────────────────────────────

  function draw() {
    if (!canvas || !squares.length) return;
    const n    = mapSize;
    canvas.width  = n * CELL;
    canvas.height = n * CELL;
    const ctx = canvas.getContext('2d');

    for (const sq of squares) {
      const dx = isWhite ? sq.x : n - 1 - sq.x;
      const dy = isWhite ? sq.y : n - 1 - sq.y;
      const px = dx * CELL, py = dy * CELL;

      // Background tile
      const img = images.get(getSquareSprite(sq.type));
      if (img) ctx.drawImage(img, px, py, CELL, CELL);
      else { ctx.fillStyle = '#556677'; ctx.fillRect(px, py, CELL, CELL); }

      // Server highlight (check, etc.)
      const sc = SERVER_HIGHLIGHTS[sq.highlight];
      if (sc) { ctx.fillStyle = sc; ctx.fillRect(px, py, CELL, CELL); }

      // Legal destination hint (green dot)
      if (legalSet.has(`${sq.x},${sq.y}`)) {
        ctx.fillStyle = 'rgba(50, 220, 50, 0.45)';
        ctx.fillRect(px, py, CELL, CELL);
        // draw a dot in the center for empty squares
        if (!sq.piece) {
          ctx.fillStyle = 'rgba(30, 180, 30, 0.7)';
          ctx.beginPath();
          ctx.arc(px + CELL / 2, py + CELL / 2, CELL * 0.18, 0, Math.PI * 2);
          ctx.fill();
        }
      }

      // Selected square (yellow)
      if (selectedSquare && sq.x === selectedSquare.x && sq.y === selectedSquare.y) {
        ctx.fillStyle = 'rgba(255, 220, 0, 0.55)';
        ctx.fillRect(px, py, CELL, CELL);
      }

      // Drag-over target (bright yellow outline)
      if (dragOverSquare && sq.x === dragOverSquare.x && sq.y === dragOverSquare.y) {
        ctx.fillStyle = 'rgba(255, 255, 80, 0.45)';
        ctx.fillRect(px, py, CELL, CELL);
        ctx.strokeStyle = 'rgba(255, 230, 0, 0.9)';
        ctx.lineWidth = 3;
        ctx.strokeRect(px + 1.5, py + 1.5, CELL - 3, CELL - 3);
      }

      // Piece sprite (skip if this square is the drag source — piece is shown as ghost)
      const isGrabbed = selectedSquare && sq.x === selectedSquare.x && sq.y === selectedSquare.y && _dragging;
      if (sq.piece && !isGrabbed) {
        const pi = images.get(getPieceSprite(sq.piece));
        if (pi) {
          const pad = CELL * 0.04;
          ctx.drawImage(pi, px + pad, py + pad, CELL - pad * 2, CELL - pad * 2);
        }
      } else if (sq.piece && isGrabbed) {
        // faint ghost on source square
        ctx.globalAlpha = 0.3;
        const pi = images.get(getPieceSprite(sq.piece));
        if (pi) ctx.drawImage(pi, px, py, CELL, CELL);
        ctx.globalAlpha = 1.0;
      }
    }
  }

  $effect(() => {
    if (squares.length && canvas) preloadAll().then(draw);
  });

  // Re-draw when highlights change without squares changing
  $effect(() => {
    void legalSet; void selectedSquare; void dragOverSquare;
    draw();
  });

  // ── Coordinate helpers ──────────────────────────────────────────────────

  function clientToBoard(clientX, clientY) {
    if (!canvas) return null;
    const rect  = canvas.getBoundingClientRect();
    const scale = canvas.width / rect.width;
    let cx = Math.floor(((clientX - rect.left) * scale) / CELL);
    let cy = Math.floor(((clientY - rect.top)  * scale) / CELL);
    if (!isWhite) { cx = mapSize - 1 - cx; cy = mapSize - 1 - cy; }
    if (cx < 0 || cx >= mapSize || cy < 0 || cy >= mapSize) return null;
    return { x: cx, y: cy };
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
    max-width: min(90vw, 90vh, 640px);
    height: auto;
    cursor: grab;
    image-rendering: pixelated;
    image-rendering: crisp-edges;
    touch-action: none;
  }
  canvas:active { cursor: grabbing; }
</style>
