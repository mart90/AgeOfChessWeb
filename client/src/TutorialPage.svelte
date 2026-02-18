<div class="page">
  <div class="content">

    <p class="lead">
      Goldrush Gambit is a variant of Chess with randomly generated obstacles on the board, and where you start with a lonely king and will need to
      hire pieces for gold. You earn gold by collecting treasures and occupying mines
      with any piece.
    </p>

    <hr />

    <h2>Full rules</h2>

    <h3>Piece movement &amp; placement</h3>
    <ul>
      <li>The pieces move like in Chess with one exception: Pawns can move any direction in a
          straight line, and capture any direction diagonally.</li>
      <li><strong>Rocks</strong> block movement along files and diagonals and nothing can stand
          on them.</li>
      <li><strong>Trees</strong> block movement along files and diagonals but pieces can stand
          on them.</li>
      <li><strong>Mines</strong> (rocks with gold in them) work the same as trees.</li>
      <li>New pieces can be bought for gold.</li>
      <li>New pieces can only be placed around your king, with the exception of pawns which can
          be placed around any major piece. Pieces can't be placed on top of enemy pieces.</li>
    </ul>

    <h3>Additional win conditions</h3>
    <p>Two additional win conditions compared to regular Chess:</p>
    <ul>
      <li>If a player gets to 150 gold, they win the game.</li>
      <li>Stalemate wins the game instead of drawing it.</li>
    </ul>

    <h3>Economy</h3>
    <ul>
      <li>Treasures give 20 gold once.</li>
      <li>Mines occupied by your pieces give 5 gold each at the start of your turn.</li>
    </ul>

    <h3>Bidding</h3>
    <p>
      With bidding enabled, both players secretly bid gold for the right to play White. The higher
      bidder wins White and their bid is added to Black's starting gold.
    </p>
    <p>
      Bids can be negative when Black is better, which can happen on full random maps. In that
      case, Black starts with negative gold.
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
