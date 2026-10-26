// Shared reactive settings – imported by any component that needs them.
// Using a .svelte.js module so $state is valid at the top level.
import { BOARD_THEMES, DEFAULT_BOARD_THEME } from './boardTheme.js';

function storedBoardTheme() {
  const id = localStorage.getItem('aoc_boardTheme');
  return BOARD_THEMES.some(t => t.id === id) ? id : DEFAULT_BOARD_THEME;
}

export const settings = $state({
  showCoordinates: JSON.parse(localStorage.getItem('aoc_showCoords') ?? 'true'),
  volume: JSON.parse(localStorage.getItem('aoc_volume') ?? '100'),
  analyzeMapSize: JSON.parse(localStorage.getItem('aoc_analyzeMapSize') ?? '10'),
  analyzeFullRandom: JSON.parse(localStorage.getItem('aoc_analyzeFullRandom') ?? 'false'),
  boardTheme: storedBoardTheme(),
});

export function persistSettings() {
  localStorage.setItem('aoc_showCoords', JSON.stringify(settings.showCoordinates));
  localStorage.setItem('aoc_volume', JSON.stringify(settings.volume));
  localStorage.setItem('aoc_analyzeMapSize', JSON.stringify(settings.analyzeMapSize));
  localStorage.setItem('aoc_analyzeFullRandom', JSON.stringify(settings.analyzeFullRandom));
  localStorage.setItem('aoc_boardTheme', settings.boardTheme);
}
