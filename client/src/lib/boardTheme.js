// Board look: square themes, piece and terrain artwork, and how terrain is drawn.
//
// Art sources (credited on the About page):
//   pieces      cburnett (Colin M.L. Burnett, via Lichess)    GPLv2+
//   mountain, pine tree  Twemoji (26f0, 1f332; pine greens darkened, trunk lightened)  CC BY 4.0
//   mine                 own drawing in Twemoji style (rock mound, timber frame, gold nuggets)
//   chest                game-icons.net (Lorc)                      CC BY 3.0
//   wood textures        ambientCG (Wood068/066/048/030)            CC0
// Every board element is vector or a 512 px texture, so it stays sharp at any board size.

import { MINE_INCOME } from './constants.js';

/**
 * Square themes. `tex` = [light, dark] textures, blended at `alpha` over their own average
 * colour (lower = calmer), then scaled by `brightness` = [light, dark]; `flat` = [light, dark] colours.
 */
export const BOARD_THEMES = [
  { id: 'soft-maple-walnut', name: 'Maple & walnut', tex: ['wood_light', 'wood_dark'],   alpha: 0.35, brightness: [1.12, 1.25] },
  { id: 'soft-ash-teak',     name: 'Ash & teak',     tex: ['wood_light2', 'wood_dark2'], alpha: 0.35, brightness: [1.12, 1.15] },
  { id: 'classic-brown',     name: 'Classic brown',       flat: ['#f0d9b5', '#b58863'] },
  { id: 'parchment',         name: 'Parchment',           flat: ['#ecdcb9', '#a98758'] },
  { id: 'green',             name: 'Green',               flat: ['#eeeed2', '#769656'] },
  { id: 'slate',             name: 'Slate',               flat: ['#dee3e6', '#8ca2ad'] },
];
export const DEFAULT_BOARD_THEME = 'soft-maple-walnut';

export function getBoardTheme(id) {
  return BOARD_THEMES.find(t => t.id === id) ?? BOARD_THEMES[0];
}

const PIECE_LETTERS = { King: 'K', Queen: 'Q', Rook: 'R', Bishop: 'B', Knight: 'N', Pawn: 'P' };

/** Image path for a piece. `type` is "WhiteKnight"/"BlackPawn"/… or "Knight" with `isWhite`; "Treasure" gives the chest. */
export function pieceSrc(type, isWhite) {
  if (type === 'Treasure') return TERRAIN.chestGold;
  const base = type.replace(/^(White|Black)/, '');
  return `/assets/pieces/cburnett/${isWhite ? 'w' : 'b'}${PIECE_LETTERS[base] ?? 'P'}.svg`;
}

export const TERRAIN = {
  mountain:  '/assets/terrain/mountain.svg',
  pine:      '/assets/terrain/pine.svg',
  mine:      '/assets/terrain/mine.svg',
  chestGold: '/assets/terrain/chest-gold.svg',
  chestEdge: '/assets/terrain/chest-edge.svg',
};

export function textureSrc(name) {
  return `/assets/board/${name}.jpg`;
}

/** Every image a theme needs (squares + terrain), for preloading. */
export function themeImageSrcs(theme) {
  return [...(theme.tex ?? []).map(textureSrc), ...Object.values(TERRAIN)];
}

// ── Drawing ─────────────────────────────────────────────────────────────────
// All functions draw one square whose top-left is (px, py) with size (cw, ch) in device pixels.
// `img(src)` returns a loaded Image or null.

const ICON_SIZE = 0.9;   // "Large"

const avgColors = new Map();
function averageColor(image) {
  if (!avgColors.has(image)) {
    const c = document.createElement('canvas');
    c.width = c.height = 1;
    const g = c.getContext('2d');
    g.drawImage(image, 0, 0, 1, 1);
    const [r, gr, b] = g.getImageData(0, 0, 1, 1).data;
    avgColors.set(image, `rgb(${r},${gr},${b})`);
  }
  return avgColors.get(image);
}

// Deterministic per-square crop so neighbouring squares don't repeat the same grain.
const hash = (x, y) => ((x * 73856093) ^ (y * 19349663)) >>> 0;

export function drawSquare(ctx, theme, img, isDark, x, y, px, py, cw, ch) {
  if (theme.flat) {
    ctx.fillStyle = theme.flat[isDark ? 1 : 0];
    ctx.fillRect(px, py, cw, ch);
    return;
  }
  const tex = img(textureSrc(theme.tex[isDark ? 1 : 0]));
  if (!tex) { ctx.fillStyle = isDark ? '#7a5a3a' : '#d8c09a'; ctx.fillRect(px, py, cw, ch); return; }
  ctx.save();
  const b = theme.brightness?.[isDark ? 1 : 0];
  if (b) ctx.filter = `brightness(${b})`;
  ctx.fillStyle = averageColor(tex);
  ctx.fillRect(px, py, cw, ch);
  const h = hash(x, y), s = tex.width * 0.42;
  const sx = (h % 1000) / 1000 * (tex.width - s), sy = ((h >> 10) % 1000) / 1000 * (tex.height - s);
  ctx.globalAlpha = theme.alpha;
  ctx.beginPath(); ctx.rect(px, py, cw, ch); ctx.clip();
  ctx.translate(px + cw / 2, py + ch / 2);
  if (x % 2 === 1) ctx.rotate(Math.PI / 2);   // alternate grain direction per file
  ctx.drawImage(tex, sx, sy, s, s, -cw / 2, -ch / 2, cw, ch);
  ctx.restore();
}

function place(ctx, image, px, py, cell, scale, dx = 0, dy = 0) {
  if (!image) return;
  const s = cell * scale;
  ctx.drawImage(image, px + (cell - s) / 2 + dx * cell, py + (cell - s) / 2 + dy * cell, s, s);
}

// Colour artwork gets a soft shadow so it lifts off the wood.
function placeWithShadow(ctx, image, px, py, cell, scale, dx = 0, dy = 0) {
  ctx.save();
  ctx.shadowColor = 'rgba(0,0,0,0.5)';
  ctx.shadowBlur = cell * 0.06;
  ctx.shadowOffsetY = cell * 0.02;
  place(ctx, image, px, py, cell, scale, dx, dy);
  ctx.restore();
}

// Gold glyphs get a dark outline by stamping the dark version around the gold one.
function placeOutlined(ctx, fill, edge, px, py, cell, scale) {
  const w = cell * 0.028;
  for (let a = 0; a < 16; a++) {
    const ang = (a / 16) * Math.PI * 2;
    place(ctx, edge, px, py, cell, scale, Math.cos(ang) * w / cell, Math.sin(ang) * w / cell);
  }
  place(ctx, fill, px, py, cell, scale);
}

/** Terrain icon for a square type ("GrassRocks", "DirtTrees", "GrassMine", …); nothing for plain squares. */
export function drawTerrain(ctx, img, type, px, py, cell) {
  const k = ICON_SIZE;
  if (type.includes('Rocks')) {
    placeWithShadow(ctx, img(TERRAIN.mountain), px, py, cell, 0.92 * k, 0, -0.035 * k);
  } else if (type.includes('Trees')) {
    const pine = img(TERRAIN.pine);
    placeWithShadow(ctx, pine, px, py, cell, 0.70 * k, -0.22 * k, -0.06 * k);
    placeWithShadow(ctx, pine, px, py, cell, 0.78 * k,  0.21 * k,  0.06 * k);
  } else if (type.includes('Mine')) {
    // Same size and offset as the mountain: both SVGs end in the same grass strip, so it lines up.
    placeWithShadow(ctx, img(TERRAIN.mine), px, py, cell, 0.92 * k, 0, -0.035 * k);
  }
}

export function drawTreasure(ctx, img, px, py, cell) {
  placeOutlined(ctx, img(TERRAIN.chestGold), img(TERRAIN.chestEdge), px, py, cell, 0.85 * ICON_SIZE);
}

/** Owner badge in the top-right corner of an owned mine: disc in the owner's colour with the income. */
export function drawMineBadge(ctx, owner, px, py, cell) {
  if (owner !== 'white' && owner !== 'black') return;
  const r = cell * 0.15, cx = px + cell - r - cell * 0.05, cy = py + r + cell * 0.05;
  ctx.beginPath();
  ctx.arc(cx, cy, r, 0, Math.PI * 2);
  ctx.fillStyle = owner === 'white' ? '#f5f1e8' : '#1e1b18';
  ctx.fill();
  ctx.lineWidth = cell * 0.025;
  ctx.strokeStyle = '#c79a2c';
  ctx.stroke();
  // Centre on the measured glyph box (textBaseline "middle" sits low for digits), then lift by 2.5%
  // of the radius: exact centring looks slightly low, 5% looked slightly high.
  ctx.fillStyle = owner === 'white' ? '#1e1b18' : '#f5f1e8';
  ctx.font = `700 ${Math.round(cell * 0.15)}px sans-serif`;
  ctx.textAlign = 'center';
  ctx.textBaseline = 'alphabetic';
  const label = `+${MINE_INCOME}`;
  const m = ctx.measureText(label);
  ctx.fillText(label, cx, cy + (m.actualBoundingBoxAscent - m.actualBoundingBoxDescent) / 2 - r * 0.025);
}
