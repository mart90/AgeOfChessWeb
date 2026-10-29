<div class="page">
  <div class="content">

    <p class="lead">
      Goldrush Gambit is a variant of chess with a simple economy and randomly generated terrain on the board which restricts piece movement. You start with a lonely king
      and will need to buy new pieces for gold, which you earn by collecting treasures and owning gold mines. 
      <br><br>
      Since the board is randomly generated - mirrored by default - every game is different. The strength of the various pieces as well as the viability of defensive strategies depends on
      where the obstacles and mines spawn. We often win by efficiently securing mines, buying the right pieces, and gaining an advantage in gold income
      by pressuring the opponent's mines while securing our own.
    </p>

    <hr />

    <h3>Board generation</h3>
    <p class="lead">
      The board can range from 6 by 6 squares to 16 by 16. It can either be fully randomly generated, or half generated and mirrored so that the players start with
      the same starting position. Fully random boards allow for more strategic diversity, but are less balanced and not recommended for beginners.
    </p>

    <hr />

    <h3>Bidding</h3>
    <p class="lead">
      The value of the first move is about 6 gold on most mirrored boards, but it does vary slightly, and it varies a lot more on fully random boards. To ensure a balanced game, there is an option to bid for White. 
      This allows the players to look at the starting position before it's decided who plays which color. The players each decide how much gold they would pay the other player in return for getting to play White.
      <br><br>
      On fully random boards, bids can be negative when Black is better. This can happen when the Black king spawns favorably, near treasures or mines.
    </p>

    <hr />

    <h3>Discord</h3>
    <p class="lead">
      Join the <a href="https://discord.gg/UhcU64NCs2">discord server</a> to discuss the game, give feedback, or find people to play with.
    </p>

    <h3>Credits</h3>
    <ul class="credits">
      <li>Chess pieces (and the knight in the logo): "cburnett" by Colin M.L. Burnett, via <a href="https://github.com/lichess-org/lila">Lichess</a></li>
      <li>Mountain and tree artwork: <a href="https://github.com/jdecked/twemoji">Twemoji</a>; the mine is drawn in the same style</li>
      <li>Chest icon by Lorc, from <a href="https://game-icons.net">game-icons.net</a></li>
      <li>Handshake icon: <a href="https://lucide.dev">Lucide</a></li>
      <li>Wood textures: <a href="https://ambientcg.com">ambientCG</a></li>
    </ul>

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

  /* Same size as the Rules page's text */
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

  a {
    color: var(--accent);
    text-decoration: none;
  }

  a:hover {
    color: var(--accent-2);
    text-decoration: underline;
  }

  hr {
    border: none;
    border-top: 1px solid var(--line);
    margin: 1.75rem 0 0.25rem;
  }

  @media (max-width: 640px) {
    h1 { font-size: 1.5rem; }
  }
</style>
