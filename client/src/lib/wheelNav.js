/**
 * Svelte action: the mouse wheel over the element steps through the moves of a game.
 * `onStep(dir)` gets +1 (wheel down: forward) or -1 (wheel up: back).
 *
 * A mouse wheel notch (large delta) is one step. Touchpads send many small deltas; those are
 * summed until they add up to a step, so a swipe doesn't race through the whole game.
 */
const STEP_PX = 50;
const RESET_MS = 200;   // a pause this long starts a new gesture

export function wheelNav(node, onStep) {
  let step = onStep;
  let acc = 0;
  let lastAt = 0;

  function onWheel(e) {
    if (e.ctrlKey || e.deltaY === 0) return;   // pinch zoom, sideways scroll
    e.preventDefault();
    const now = performance.now();
    if (now - lastAt > RESET_MS) acc = 0;
    lastAt = now;

    const dy = e.deltaMode === 1 ? e.deltaY * 40 : e.deltaMode === 2 ? e.deltaY * 800 : e.deltaY;
    if (Math.abs(dy) >= STEP_PX) {
      acc = 0;
      step(Math.sign(dy));
      return;
    }
    acc += dy;
    if (Math.abs(acc) >= STEP_PX) {
      step(Math.sign(acc));
      acc = 0;
    }
  }

  // Not passive: the page must not scroll while the wheel steps through moves
  node.addEventListener('wheel', onWheel, { passive: false });
  return {
    update(fn) { step = fn; },
    destroy() { node.removeEventListener('wheel', onWheel); },
  };
}
