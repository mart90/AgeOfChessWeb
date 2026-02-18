// Reactive state shared between GamePage (writes) and NavBar (reads).
// mapSeed is null when the user is not viewing a game board.
export const currentGame = $state({ mapSeed: null });
