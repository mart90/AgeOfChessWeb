<script>
  import { onMount } from 'svelte';

  /** @type {{ squares: any[], selectedSquare: {x:number,y:number}|null, mapSize: number, isWhite: boolean, onSquareClick: (x:number,y:number)=>void }} */
  let { squares, selectedSquare, mapSize, isWhite, onSquareClick } = $props();

  const CELL = 64; // canvas pixels per square

  let canvas = $state(null);
  const images = new Map();

  // ── Sprite URL helpers ──────────────────────────────────────────────────

  function getPieceSprite(piece) {
    if (piece.type === 'WhiteFlag' || piece.type === 'BlackFlag') {
      return '/assets/objects/white_flag.png';
    }
    if (piece.type === 'Treasure') {
      return '/assets/objects/treasure.png';
    }
    const color = piece.isWhite ? 'w' : 'b';
    const name  = piece.type.replace(/^(White|Black)/, '').toLowerCase();
    return `/assets/objects/${color}_${name}.png`;
  }

  function getSquareSprite(type) {
    // "DirtMine" → "dirt_mine", "GrassRocks" → "grass_rocks"
    const snake = type.replace(/([A-Z])/g, '_$1').toLowerCase().replace(/^_/, '');
    return `/assets/squares/${snake}.png`;
  }

  // ── Image loading ───────────────────────────────────────────────────────

  function loadImage(src) {
    if (images.has(src)) return Promise.resolve(images.get(src));
    return new Promise((resolve) => {
      const img = new Image();
      img.onload  = () => { images.set(src, img);   resolve(img);  };
      img.onerror = () => { images.set(src, null);  resolve(null); };
      img.src = src;
    });
  }

  async function preloadAll() {
    if (!squares) return;
    const srcs = new Set();
    for (const sq of squares) {
      srcs.add(getSquareSprite(sq.type));
      if (sq.piece) srcs.add(getPieceSprite(sq.piece));
    }
    await Promise.all([...srcs].map(loadImage));
  }

  // ── Highlight colours ───────────────────────────────────────────────────

  const HIGHLIGHTS = {
    Red:    'rgba(220,  50,  50, 0.55)',
    Green:  'rgba( 50, 200,  50, 0.50)',
    Blue:   'rgba( 50, 100, 255, 0.45)',
    Purple: 'rgba(160,  50, 220, 0.50)',
    Orange: 'rgba(230, 130,  20, 0.55)',
  };

  // ── Drawing ─────────────────────────────────────────────────────────────

  function draw() {
    if (!canvas || !squares) return;
    const n = mapSize || 12;
    canvas.width  = n * CELL;
    canvas.height = n * CELL;
    const ctx = canvas.getContext('2d');

    for (const sq of squares) {
      // Flip board for black player (rotate 180°)
      const px = (isWhite ? sq.x : n - 1 - sq.x) * CELL;
      const py = (isWhite ? sq.y : n - 1 - sq.y) * CELL;

      // Square background
      const sqImg = images.get(getSquareSprite(sq.type));
      if (sqImg) {
        ctx.drawImage(sqImg, px, py, CELL, CELL);
      } else {
        ctx.fillStyle = '#556677';
        ctx.fillRect(px, py, CELL, CELL);
      }

      // Highlight overlay
      const hColor = HIGHLIGHTS[sq.highlight];
      if (hColor) {
        ctx.fillStyle = hColor;
        ctx.fillRect(px, py, CELL, CELL);
      }

      // Selection highlight
      if (selectedSquare && sq.x === selectedSquare.x && sq.y === selectedSquare.y) {
        ctx.fillStyle = 'rgba(255, 230, 0, 0.55)';
        ctx.fillRect(px, py, CELL, CELL);
      }

      // Piece / object
      if (sq.piece) {
        const img = images.get(getPieceSprite(sq.piece));
        if (img) {
          const pad = CELL * 0.04;
          ctx.drawImage(img, px + pad, py + pad, CELL - pad * 2, CELL - pad * 2);
        }
      }
    }
  }

  // Redraw whenever squares or selection changes
  $effect(() => {
    if (squares && canvas) {
      preloadAll().then(draw);
    }
  });

  // ── Click handling ──────────────────────────────────────────────────────

  function handleClick(e) {
    if (!canvas || !onSquareClick) return;
    const rect   = canvas.getBoundingClientRect();
    const scaleX = canvas.width  / rect.width;
    const scaleY = canvas.height / rect.height;
    const n = mapSize || 12;

    let cx = Math.floor(((e.clientX - rect.left) * scaleX) / CELL);
    let cy = Math.floor(((e.clientY - rect.top)  * scaleY) / CELL);

    // Un-flip for black
    if (!isWhite) {
      cx = n - 1 - cx;
      cy = n - 1 - cy;
    }

    if (cx >= 0 && cx < n && cy >= 0 && cy < n) {
      onSquareClick(cx, cy);
    }
  }

  onMount(draw);
</script>

<canvas bind:this={canvas} onclick={handleClick}></canvas>

<style>
  canvas {
    display: block;
    width: 100%;
    max-width: min(90vw, 90vh, 640px);
    height: auto;
    cursor: pointer;
    image-rendering: pixelated;
    image-rendering: crisp-edges;
  }
</style>
