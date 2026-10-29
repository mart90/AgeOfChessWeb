/**
 * "My library": saved games with their variations (server: LibraryController).
 * Entries store the packed move tree from gameTransfer.js, positions included.
 */

/** Hand-off from the library page to the analysis board (same tab, so no storage needed). */
export const pendingLibraryOpen = { entry: null };

/** Same notation as the profile page's game history (server: UserController). */
export const RESULT_SHORT = {
  'w+c': 'W+#', 'b+c': 'B+#',
  'w+g': 'W+G', 'b+g': 'B+G',
  'w+s': 'W+S', 'b+s': 'B+S',
  'w+t': 'W+T', 'b+t': 'B+T',
  'w+r': 'W+R', 'b+r': 'B+R',
  'draw': '½–½',
  'abandoned': 'Abandoned',
};
