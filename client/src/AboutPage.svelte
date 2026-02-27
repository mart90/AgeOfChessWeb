<div class="page">
  <div class="content">

    <p class="lead">
      Goldrush Gambit is a variant of Chess with an economy and randomly generated terrain on the board which restricts piece movement. You start with a lonely king
      and will need to buy new pieces for gold, which you earn by collecting treasures and owning mines. 
      <br><br>
      The game revolves mostly around gaining an advantage in gold income by securing more mines than your opponent, then converting that to a material advantage and 
      ultimately checkmating the enemy king or storing lots of gold to win the game. The early game, though it may seem simple with just a king on the board, is crucial, 
      since a small tempo advantage can lead to a won position many moves later.
    </p>

    <hr />

    <h3>Board generation</h3>
    <p class="lead">
      The board can range from 6 by 6 squares to 16 by 16. It can either be fully randomly generated, or half generated and mirrored so that the players start with
      the same starting position. Fully random boards allow for more strategic diversity, but are less balanced and not recommended for beginners. It's recommended 
      to have bidding enabled for fully random boards, because the value of having White will vary much more than with mirrored boards. Bidding ensures a balanced game.
    </p>

    <hr />

    <h3>Bidding</h3>
    <p class="lead">
      Because the value of having White is different per board spawn, there is an option to bid for White. This allows the players to look at the starting position before
      it's decided who plays which color. The players each decide how much gold they would pay the other player in return for getting to play White. 
      <br><br>
      On fully random boards, bids can be negative when Black is better. This can happen when the Black king spawns favorably, near treasures or mines.
      <br><br>
      After both players submitted their bid, the highest bidder gets White. The winning bid then becomes Black's starting gold. If Black was better and the "winning" bid
      was negative, Black will start in debt (negative gold).
    </p>

    <hr />

    <h3>Discord</h3>
    <p class="lead">
      Join the <a href="https://discord.gg/UhcU64NCs2">discord server</a> to discuss the game, give feedback, or find people to play with.
    </p>
    
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

  a {
    color: #8ab4f8;
    text-decoration: none;
  }

  a:hover {
    color: #aac7ff;
    text-decoration: underline;
  }

  hr {
    border: none;
    border-top: 1px solid #2a2a4a;
    margin: 1.75rem 0 0.25rem;
  }

  @media (max-width: 640px) {
    h1 { font-size: 1.5rem; }
    p, li { font-size: 0.92rem; }
  }
</style>
