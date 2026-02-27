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
      <li>Players get 1 gold at the start of each turn.</li>
      <li>Capturing a treasure earns 20 gold.</li>
      <li>Mines owned by a player earn {MINE_INCOME} gold each at the start of their turn.</li>
      <li>When a piece moves onto a mine, the mine becomes owned by that player.</li>
      <li>New pieces cost gold, ranging from {minCost} gold for a {cheapest} to {maxCost} gold for a {mostExpensive}.</li>
    </ul>

    <h3>Piece movement</h3>
    <ul>
      <li>Pieces other than pawns move as in Chess.</li>
      <li>Pawns can move any direction in a straight line, and capture any direction diagonally.</li>
      <li><strong>Rocks</strong> block movement along files and diagonals and nothing can stand
          on them.</li>
      <li><strong>Forests</strong> block movement along files and diagonals, but can be occupied.</li>
      <li><strong>Mines</strong> (rocks with gold in them) affect movement the same way forests do.</li>
    </ul>

    <h3>Piece placement</h3>
    <ul>
      <li>Instead of moving a piece, a new piece can be placed on the board.</li>
      <li>New non-pawn pieces can only be placed around the king.</li>
      <li>Pawns can be placed around any piece except other pawns.</li> 
      <li>Pieces can't be placed on top of enemy pieces or treasures.</li>
    </ul>
    
    <h3>Additional win conditions</h3>
    <p>Two additional win conditions compared to regular Chess:</p>
    <ul>
      <li>If a player reaches a high amount of gold, they win the game. This is 125 gold for 6x6 boards, and increases by 25 
          per board size up to 250 for 16x16</li>
      <li>Stalemate wins the game instead of drawing it.</li>
    </ul>

    <h3>Bidding</h3>
    <p>
      With bidding enabled, both players secretly bid gold for the right to play White. The higher
      bidder wins White and their bid becomes Black's starting gold. Bids can be negative.
    </p>
    <p class="example">
      <strong>Example:</strong> You bid 8g, the opponent bids 6g. You play White, and the opponent
      plays Black with 8 starting gold.
    </p>

    <hr />

    <h2>Piece movement example</h2>
    <p>
      In the position below, we are trying to move our queen. She can move one space to her right
      onto the mine, but no further. This "here and no further" also applies to the treasure on
      i7, which she could capture for 20 gold, and the forest on e5. It does not apply to the
      rocks on g3 and f11, which she can't move to at all.
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
    color: #a0b0ff;
    margin: 0 0 0.5rem;
    letter-spacing: 0.01em;
  }

  h2 {
    font-size: 1.25rem;
    font-weight: 700;
    color: #ccd;
    margin: 1.5rem 0 0.6rem;
  }

  h3 {
    font-size: 1rem;
    font-weight: 600;
    color: #9ab;
    margin: 1.1rem 0 0.4rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    font-size: 0.8rem;
  }

  p, li {
    font-size: 0.97rem;
    line-height: 1.7;
    color: #ccc;
  }

  p { margin: 0.4rem 0; }

  .lead {
    font-size: 1.05rem;
    color: #bbb;
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

  li { color: #bbb; }
  li strong { color: #ddd; }

  .example {
    background: #1e1e38;
    border-left: 3px solid #4a6fa5;
    padding: 0.6rem 0.9rem;
    border-radius: 0 6px 6px 0;
    color: #bbb;
  }
  .example strong { color: #ddd; }

  hr {
    border: none;
    border-top: 1px solid #2a2a4a;
    margin: 1.75rem 0 0.25rem;
  }

  .board-img-wrap {
    margin-top: 1.25rem;
    display: flex;
    justify-content: center;
  }

  .board-img {
    width: 100%;
    max-width: 560px;
    border-radius: 6px;
    box-shadow: 0 4px 24px rgba(0,0,0,0.5);
    image-rendering: pixelated;
  }

  @media (max-width: 640px) {
    h1 { font-size: 1.5rem; }
    p, li { font-size: 0.92rem; }
  }
</style>
