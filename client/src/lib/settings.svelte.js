// Shared reactive settings – imported by any component that needs them.
// Using a .svelte.js module so $state is valid at the top level.

export const settings = $state({
  showCoordinates: JSON.parse(localStorage.getItem('aoc_showCoords') ?? 'true'),
  muteSounds: JSON.parse(localStorage.getItem('aoc_muteSounds') ?? 'false'),
});

export function persistSettings() {
  localStorage.setItem('aoc_showCoords', JSON.stringify(settings.showCoordinates));
  localStorage.setItem('aoc_muteSounds', JSON.stringify(settings.muteSounds));
}
