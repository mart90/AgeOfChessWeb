//! Static evaluation. Units: 1 gold = 10 points. Positive = good for the side to move.
//!
//! Design: pieces are valued at roughly their cost, so buying is neutral by itself; a
//! purchase must pay off through what the piece does (king pressure, mobility, mine
//! control). Gold also counts toward the gold-victory race, which gives the engine a reason
//! to either hoard or attack instead of buying forever.

use crate::position::*;
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct EvalParams {
    /// Piece values by kind index (KING..PAWN).
    pub piece: [i32; 7],
    /// Value per gold.
    pub gold: i32,
    /// Value of owning a mine (future income).
    pub mine: i32,
    /// Gold-race weight: score += race * (1/(turns_me+1) - 1/(turns_opp+1)).
    pub race: i32,
    /// Mobility per pseudo-legal move, by kind.
    pub mobility: [i32; 7],
    /// Bonus per own piece near the enemy king, by kind, times (4 - chebyshev distance).
    pub king_tropism: [i32; 7],
    /// Per attacked square in the enemy king zone (king square + neighbours).
    pub king_zone_attack: i32,
    /// Bonus when the enemy king has no safe square, scaled by attacked zone squares.
    pub king_no_escape: i32,
    /// Per enemy-owned/unowned mine our pieces attack (pressure on income).
    pub mine_pressure: i32,
    pub tempo: i32,
}

impl Default for EvalParams {
    fn default() -> Self {
        EvalParams {
            piece: [0, 0, 750, 400, 320, 300, 260],
            gold: 10,
            mine: 240,
            race: 2000,
            mobility: [0, 0, 2, 3, 3, 4, 2],
            king_tropism: [0, 0, 12, 8, 8, 10, 4],
            king_zone_attack: 20,
            king_no_escape: 40,
            mine_pressure: 15,
            tempo: 0, // was 10 (1 gold for the side to move): tempo 0 is +40 ± 22 Elo
        }
    }
}

#[inline]
fn cheb(size: usize, a: usize, b: usize) -> i32 {
    let (ax, ay) = ((a % size) as i32, (a / size) as i32);
    let (bx, by) = ((b % size) as i32, (b / size) as i32);
    (ax - bx).abs().max((ay - by).abs())
}

/// Number of pseudo-legal destination squares for the piece on `sq` (no allocation).
/// Also marks every square the piece attacks in `attacked`.
fn mobility_and_attacks(pos: &Position, sq: usize, attacked: &mut [u8; MAX_SQ]) -> i32 {
    let o = pos.occ[sq];
    let us = color_of(o);
    let geo = &*pos.geo;
    let nbs = &geo.nb;
    let mut count = 0;
    let own = |t: usize| {
        let x = pos.occ[t];
        is_piece(x) && color_of(x) == us
    };
    match kind_of(o) {
        KING => {
            for d in 0..8 {
                let t = nbs[sq][d];
                if t != NO_SQ && pos.terrain[t as usize] != Terrain::Rocks {
                    attacked[t as usize] = attacked[t as usize].saturating_add(1);
                }
            }
        }
        KNIGHT => {
            for &t in &geo.knight[sq] {
                let t = t as usize;
                if pos.terrain[t] != Terrain::Rocks {
                    attacked[t] = attacked[t].saturating_add(1);
                    if !own(t) {
                        count += 1;
                    }
                }
            }
        }
        PAWN => {
            for d in 0..8 {
                let t = nbs[sq][d];
                if t == NO_SQ || pos.terrain[t as usize] == Terrain::Rocks {
                    continue;
                }
                let t = t as usize;
                if d & 1 == 1 {
                    attacked[t] = attacked[t].saturating_add(1);
                    let x = pos.occ[t];
                    if x == TREASURE || (is_piece(x) && color_of(x) != us) {
                        count += 1;
                    }
                } else if pos.occ[t] == EMPTY {
                    count += 1;
                }
            }
        }
        k => {
            let (d0, step) = match k {
                ROOK => (0, 2),
                BISHOP => (1, 2),
                _ => (0, 1),
            };
            let mut d = d0;
            while d < 8 {
                let mut cur = sq;
                loop {
                    let nx = nbs[cur][d];
                    if nx == NO_SQ {
                        break;
                    }
                    cur = nx as usize;
                    if pos.terrain[cur] == Terrain::Rocks {
                        break;
                    }
                    attacked[cur] = attacked[cur].saturating_add(1);
                    let x = pos.occ[cur];
                    if x != EMPTY {
                        if !own(cur) {
                            count += 1;
                        }
                        break;
                    }
                    count += 1;
                    if pos.terrain[cur].stops_rays() {
                        break;
                    }
                }
                d += step;
            }
        }
    }
    count
}

/// Turns until `color` reaches the gold threshold at its current income.
#[inline]
pub fn turns_to_gold(pos: &Position, color: usize) -> i32 {
    let need = pos.gold_threshold() - pos.gold[color];
    if need <= 0 {
        return 0;
    }
    let inc = pos.income(color).max(1);
    (need + inc - 1) / inc
}

pub fn evaluate(pos: &Position, p: &EvalParams) -> i32 {
    let size = pos.size();
    let mut score = [0i32; 2];
    let mut attacked = [[0u8; MAX_SQ]; 2];
    // Pieces are valued at their cost; follow the queen's price when the rules change it.
    let mut piece_value = p.piece;
    piece_value[QUEEN as usize] += (pos.rules.queen_cost - piece_cost(QUEEN)) * p.gold;

    for sq in 0..pos.n() {
        let o = pos.occ[sq];
        if !is_piece(o) {
            continue;
        }
        let c = color_of(o);
        let k = kind_of(o) as usize;
        score[c] += piece_value[k];
        let mob = mobility_and_attacks(pos, sq, &mut attacked[c]);
        score[c] += p.mobility[k] * mob;
        if k != KING as usize {
            let dist = cheb(size, sq, pos.king[c ^ 1]);
            if dist < 4 {
                score[c] += p.king_tropism[k] * (4 - dist);
            }
        }
    }

    let nbs = &pos.geo.nb;
    for c in 0..2 {
        let them = c ^ 1;
        score[c] += pos.gold[c] * p.gold + pos.frac[c] as i32 * p.gold / 10 + pos.mines[c] * p.mine;

        // Pressure on the enemy king
        let ek = pos.king[them];
        let mut zone_attacked = (attacked[c][ek] > 0) as i32;
        let mut safe = 0;
        for d in 0..8 {
            let t = nbs[ek][d];
            if t == NO_SQ {
                continue;
            }
            let t = t as usize;
            if attacked[c][t] > 0 {
                zone_attacked += 1;
            } else if pos.terrain[t] != Terrain::Rocks && !(is_piece(pos.occ[t]) && color_of(pos.occ[t]) == them) {
                safe += 1;
            }
        }
        score[c] += p.king_zone_attack * zone_attacked;
        if safe == 0 && zone_attacked > 0 {
            score[c] += p.king_no_escape * zone_attacked;
        }
    }

    // Mine pressure: attacked mines we don't own
    for sq in 0..pos.n() {
        if pos.terrain[sq] == Terrain::Mine {
            let ow = pos.mine_owner[sq];
            for c in 0..2 {
                if ow != c as u8 + 1 && attacked[c][sq] > 0 {
                    score[c] += p.mine_pressure;
                }
            }
        }
    }

    // Gold race
    let t0 = turns_to_gold(pos, WHITE);
    let t1 = turns_to_gold(pos, BLACK);
    let race = p.race * 1000 / (t0 + 1) - p.race * 1000 / (t1 + 1);
    let race = race / 1000;

    let stm = pos.side;
    let white_rel = score[WHITE] - score[BLACK] + race;
    let rel = if stm == WHITE { white_rel } else { -white_rel };
    rel + p.tempo
}
