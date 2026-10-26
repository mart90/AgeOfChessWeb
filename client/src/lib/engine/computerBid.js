// How the computer bids for White (games vs the computer with bidding).
// A Rust copy for testing lives in engine/src/bid.rs — keep them in sync.
//
// Each pass analyses the starting position with Black holding the current bid estimate as
// starting gold; the result r is White's remaining advantage in gold. Corrections use a log
// scale, so small results move the bid a little and big results don't run away:
//
//   step(r, k) = sign(r) · k·ln(1 + |r|)        (k=1: 0.5→0.4, 1→0.7, 3→1.4, 10→2.4, 30→3.4)
//
//   5 passes using 30% / 25% / 20% / 15% / 10% of the budget. Pass 1 analyses with Black on
//   the 5g baseline (bid = 5 + step), each later pass with Black on the current bid (bid += step).
//
//   k = 1.5, 1.25, 1, 0.75, 0.5 on mirrored boards (3-2.5-2-1.5-1 overpaid for White);
//   5, 4, 3, 2, 1 on full random boards (often very lopsided).
//
// High-bid correction: if the bid would be ≥ 7 (estimate ≥ 6.5), subtract round(estimate / 5)
// (6.5–7.4 → −1, 7.5–12.4 → −2, …) before rounding: tests showed the bidder overpaying for
// White on high bids.
//
// The estimate stays fractional; only the final bid is rounded. Each analysis gives Black the
// estimate rounded to 0.1 gold; the engine adds 0.1 per Black turn until it is a whole gold.
// Budget = level nodes × 30 (BID_BUDGET_FACTOR). No caps.

export const BID_BASELINE = 5;
export const BID_BUDGET_FACTOR = 30;        // budget = level nodes × this
const SHARES = [0.3, 0.25, 0.2, 0.15, 0.1]; // share of the node budget per pass
const SCALES_MIRRORED = [1.5, 1.25, 1, 0.75, 0.5];
const SCALES_FULL_RANDOM = [5, 4, 3, 2, 1];
const HIGH_BID = 7;            // bids that would be ≥ this get corrected down
const HIGH_BID_DIVISOR = 5;    // by round(estimate / 5)

/** Bid correction for a remaining advantage r (gold): sign(r) · k·ln(1 + |r|). */
const step = (r, k) => Math.sign(r) * k * Math.log(1 + Math.abs(r));

/** White's advantage in gold from a search result (forced mates count as a huge advantage). */
function advantage(res) {
  if (res.mate != null) return Math.sign(res.mate) * 100;
  return res.score;
}

/**
 * @param squares     starting-position squares (plain objects, client format)
 * @param budget      total node budget (level nodes × BID_BUDGET_FACTOR)
 * @param search      async (position, nodes) => engine search result
 * @param fullRandom  true for full random boards (map seed starting with "r")
 * @returns { bid, steps: [{ blackGold, nodes, advantage, estimate }] }
 */
export async function computeComputerBid(squares, budget, search, fullRandom = false) {
  const scales = fullRandom ? SCALES_FULL_RANDOM : SCALES_MIRRORED;
  const steps = [];
  let estimate = BID_BASELINE;   // fractional until the end
  for (let i = 0; i < SHARES.length; i++) {
    const nodes = Math.max(1000, Math.round(budget * SHARES[i]));
    const blackGold = Math.round(estimate * 10) / 10;
    const res = await search({ squares, whiteGold: 0, blackGold, whiteIsActive: true }, nodes);
    const adv = advantage(res);
    estimate += step(adv, scales[i]);
    steps.push({ blackGold, nodes, advantage: adv, estimate });
  }
  const correction = Math.round(estimate) >= HIGH_BID ? Math.round(estimate / HIGH_BID_DIVISOR) : 0;
  return { bid: Math.round(estimate - correction), correction, steps };
}
