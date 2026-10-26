//! Computer bidding for White. Mirrors `client/src/lib/engine/computerBid.js` exactly (that
//! file is what the website uses); this copy exists for testing the bidding with `gg bidmatch`.
//!
//! Each pass analyses the starting position with Black holding the current bid as starting gold
//! (result r = White's remaining advantage in gold) and adds sign(r)·k·ln(1+|r|), in 5 passes
//! using 30/25/20/15/10% of the node budget (level nodes × 30). k = 1.5, 1.25, 1, 0.75, 0.5 on
//! mirrored boards and 5, 4, 3, 2, 1 on full random boards. Baseline 5. The estimate stays
//! fractional; only the final bid is rounded, after the high-bid correction
//! (if the bid would be ≥ 7 → minus round(estimate / 5)). Each analysis gives Black the estimate rounded to
//! 0.1 gold (fractional gold ticks up 0.1 per Black turn until whole, see `Position::frac`).

use crate::position::*;
use crate::search::{Limits, Searcher, MATE_BOUND};

pub const BID_BASELINE: i32 = 5;
pub const BID_BUDGET_FACTOR: u64 = 30;
pub const SHARES: [f64; 5] = [0.3, 0.25, 0.2, 0.15, 0.1];
pub const SCALES_MIRRORED: [f64; 5] = [1.5, 1.25, 1.0, 0.75, 0.5];
pub const SCALES_FULL_RANDOM: [f64; 5] = [5.0, 4.0, 3.0, 2.0, 1.0];
/// Bids that would be ≥ HIGH_BID are corrected down by round(estimate / HIGH_BID_DIVISOR).
pub const HIGH_BID: f64 = 7.0;
pub const HIGH_BID_DIVISOR: f64 = 5.0;

/// JavaScript's Math.round (halves round towards +∞).
fn js_round(x: f64) -> i32 {
    (x + 0.5).floor() as i32
}

/// Bid correction for a remaining advantage r (gold): sign(r) · k·ln(1 + |r|).
pub fn step(r: f64, k: f64) -> f64 {
    if r == 0.0 { 0.0 } else { r.signum() * k * (1.0 + r.abs()).ln() }
}

/// Returns the bid and, per pass, (black gold analysed, advantage, estimate after the pass).
pub fn computer_bid(searcher: &mut Searcher, start: &Position, budget: u64, full_random: bool) -> (i32, Vec<(f64, f64, f64)>) {
    let scales = if full_random { SCALES_FULL_RANDOM } else { SCALES_MIRRORED };
    computer_bid_with(searcher, start, budget, &SHARES, &scales)
}

/// Same with explicit per-pass budget shares and scales (for experiments).
pub fn computer_bid_with(searcher: &mut Searcher, start: &Position, budget: u64, shares: &[f64], scales: &[f64]) -> (i32, Vec<(f64, f64, f64)>) {
    let mut estimate = BID_BASELINE as f64;   // fractional until the end
    let mut steps = Vec::new();
    for (share, &k) in shares.iter().zip(scales) {
        let nodes = ((budget as f64 * share).round() as u64).max(1000);
        let black_gold = (estimate * 10.0).round() / 10.0;
        let mut pos = start.clone();
        pos.side = WHITE;
        pos.gold[WHITE] = 0;
        pos.frac[WHITE] = 0;
        pos.set_gold_decimal(BLACK, black_gold);
        pos.refresh();
        let r = searcher.search(&mut pos, Limits { max_nodes: Some(nodes), ..Default::default() });
        let adv = if r.score.abs() >= MATE_BOUND { r.score.signum() as f64 * 100.0 } else { r.score as f64 / 10.0 };
        estimate += step(adv, k);
        steps.push((black_gold, adv, estimate));
    }
    let correction = if js_round(estimate) as f64 >= HIGH_BID { js_round(estimate / HIGH_BID_DIVISOR) as f64 } else { 0.0 };
    (js_round(estimate - correction), steps)
}
