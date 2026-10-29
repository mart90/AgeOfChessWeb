<script>
  import { SHOP, MINE_INCOME } from './lib/constants.js';

  const costs = SHOP.map(s => s.cost);
  const minCost = Math.min(...costs);
  const maxCost = Math.max(...costs);
  const cheapest = SHOP.find(s => s.cost === minCost)?.type.toLowerCase();
  const mostExpensive = SHOP.find(s => s.cost === maxCost)?.type.toLowerCase();
</script>

<div class="page">
  <div class="content">
    <h3>Economy</h3>
    <ul>
      <li>Black starts with 6 gold by default.</li>
      <li>Players get 1 gold at the start of each turn.</li>
      <li>Capturing a treasure earns 20 gold.</li>
      <li>Gold mines owned by a player earn {MINE_INCOME} gold each at the start of their turn.</li>
      <li>When a piece moves onto a gold mine, the mine becomes owned by that player.</li>
      <li>New pieces cost gold, ranging from {minCost} gold for a {cheapest} to {maxCost} gold for a {mostExpensive}.</li>
    </ul>

    <h3>Piece movement</h3>
    <ul>
      <li>Pieces other than pawns move as in chess.</li>
      <li>Pawns can move any direction in a straight line, and capture any direction diagonally.</li>
      <li>Mountains block movement along files and diagonals and nothing can stand
          on them.</li>
      <li>Forests block movement along files and diagonals, but can be occupied.</li>
      <li>Gold mines affect movement the same way forests do.</li>
    </ul>

    <h3>Piece placement</h3>
    <ul>
      <li>We can spend our turn and gold to place a new piece on the board.</li>
      <li>All pieces can be placed around the king.</li>
      <li>Rooks, bishops and knights can also be placed around queens.</li>
      <li>Pawns can be placed around any piece except other pawns.</li>
      <li>Pieces can't be placed on top of enemy pieces or treasures.</li>
    </ul>
    
    <h3>Additional win conditions</h3>
    <p>Two additional win conditions compared to regular Chess:</p>
    <ul>
      <li>If a player stores a high amount of gold, they win the game. This is 150 gold for 6x6 boards, and increases by 25 
          per board size up to 275 for 16x16. You win at the start of your turn if you have enough gold and your king is not in check.</li>
      <li>If a player has no legal moves (stalemate), they lose the game.</li>
    </ul>

    <h3>Bidding</h3>
    <p>
      With bidding enabled, both players secretly bid gold for the right to play White. The higher
      bidder wins White and their bid becomes Black's starting gold. Bids can be negative.
    </p>
    <p class="example">
      <strong>Example:</strong> You bid 8 gold, the opponent bids 6 gold. You play White, and the opponent
      plays Black with 8 starting gold.
    </p>

    <hr />

    <h2>Piece movement example</h2>
    <p>
      In the position below, we are trying to move our queen. She can move one space on the left-down diagonal onto the forest, but no further. 
      This "here and no further" also applies to the mine above her. It does not apply to the
      mountains, which she can't move to at all.
    </p>
    <div class="board-img-wrap">
      <img
        class="board-img"
        src="/assets/other/tutorial_board.png"
        alt="Example board position showing queen movement"
      />
    </div>

  </div>
</div>

<style>
  .page {
    display: flex;
    justify-content: center;
    padding: 2rem 1rem 4rem;
    flex: 1;
  }

  .content {
    width: 100%;
    max-width: 720px;
    display: flex;
    flex-direction: column;
    gap: 0.1rem;
  }

  h1 {
    font-size: 2rem;
    font-weight: 700;
    color: var(--accent-2);
    margin: 0 0 0.5rem;
    letter-spacing: 0.01em;
  }

  h2 {
    font-size: 1.25rem;
    font-weight: 700;
    color: var(--fg-2);
    margin: 1.5rem 0 0.6rem;
  }

  h3 {
    font-size: 1rem;
    font-weight: 600;
    color: var(--muted);
    margin: 1.1rem 0 0.4rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    font-size: 0.8rem;
  }

  /* Same size as the About page's text (its .lead paragraphs) */
  p, li {
    font-size: 1.05rem;
    line-height: 1.75;
    color: var(--fg-2);
  }

  p { margin: 0.4rem 0; }

  .lead {
    font-size: 1.05rem;
    color: var(--fg-2);
    line-height: 1.75;
    margin-top: 0.75rem;
  }

  ul {
    margin: 0.3rem 0 0.3rem 1.2rem;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
  }

  li { color: var(--fg-2); }
  li strong { color: var(--fg); }

  .example {
    background: var(--surface);
    border-left: 3px solid var(--button);
    padding: 0.6rem 0.9rem;
    border-radius: 0 6px 6px 0;
    color: var(--fg-2);
  }
  .example strong { color: var(--fg); }

  hr {
    border: none;
    border-top: 1px solid var(--line);
    margin: 1.75rem 0 0.25rem;
  }

  .board-img-wrap {
    margin-top: 1.25rem;
    display: flex;
    justify-content: center;
  }

  /* Full text width; the 1204px source is downscaled smoothly (no `pixelated`) */
  .board-img {
    display: block;
    width: 100%;
    height: auto;
    border-radius: 6px;
    box-shadow: 0 4px 24px rgba(0,0,0,0.5);
  }

  @media (max-width: 640px) {
    h1 { font-size: 1.5rem; }
  }
</style>
