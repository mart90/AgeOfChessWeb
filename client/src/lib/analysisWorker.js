import { getTopMoves, requestStop, resetStop, clearTranspositionTable } from '../lib/analysis.js';

let running = false;
let currentPosition = null;
let positionVersion = 0;

self.onmessage = (event) => {
  const { type, payload } = event.data;

  if (type === 'start') {
    running = true;
    resetStop();
    clearTranspositionTable();
    currentPosition = payload;
    positionVersion++;
    analyze(positionVersion);
  }

  if (type === 'update') {
    requestStop();  // Stop current search immediately
    clearTranspositionTable(); // Clear TT on position change
    currentPosition = payload;
    positionVersion++;
    // Don't resetStop() here - let analyze() do it after detecting the change
  }

  if (type === 'stop') {
    running = false;
    requestStop();
  }
};

async function analyze(version) {
  let depth = 2;

  while (running) {
    // Restart if position changed
    if (version !== positionVersion) {
      version = positionVersion;
      depth = 2;
      resetStop();  // Clear stop flag now that we've detected the change
    }
    const { squares, whiteGold, blackGold, whiteIsActive } = currentPosition;

    // const startTime = performance.now();
    const topMoves = await getTopMoves(
      squares,
      whiteGold,
      blackGold,
      whiteIsActive,
      depth,
      10
    );
    // const timeMs = performance.now() - startTime;

    // Check again after getTopMoves - position may have changed
    if (!running) break;
    if (version !== positionVersion) {
      // Position changed during search, restart from depth 2
      version = positionVersion;
      depth = 2;
      resetStop();  // Clear stop flag now that we've detected the change
      continue;
    }

    self.postMessage({
      type: 'depth-result',
      depth,
      topMoves,
      // timeMs
    });

    depth++;

    // Yield so stop/update messages can be processed
    await new Promise(r => setTimeout(r, 0));
  }
}