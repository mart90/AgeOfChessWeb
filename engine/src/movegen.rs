//! Legal move generation (piece moves + placements).

use crate::position::*;

/// Squares of pieces of the side to move that are pinned to their king (moving them may
/// expose the king). Pieces on forests/mines are never pinned: the terrain blocks the line.
fn pinned_mask(pos: &Position, pinned: &mut [bool; MAX_SQ]) -> bool {
    let us = pos.side;
    let them = us ^ 1;
    let ksq = pos.king[us];
    let nbs = &pos.geo.nb;
    let queen = make_piece(QUEEN, them);
    let mut any = false;
    for d in 0..8 {
        let line = make_piece(if d & 1 == 0 { ROOK } else { BISHOP }, them);
        let mut cur = ksq;
        let mut candidate: Option<usize> = None;
        loop {
            let nx = nbs[cur][d];
            if nx == NO_SQ {
                break;
            }
            cur = nx as usize;
            if pos.terrain[cur] == Terrain::Rocks {
                break;
            }
            let o = pos.occ[cur];
            if o != EMPTY {
                if candidate.is_none() {
                    if is_piece(o) && color_of(o) == us && !pos.terrain[cur].stops_rays() {
                        candidate = Some(cur);
                        continue;
                    }
                    break;
                }
                if o == queen || o == line {
                    pinned[candidate.unwrap()] = true;
                    any = true;
                }
                break;
            }
            if pos.terrain[cur].stops_rays() {
                break;
            }
        }
    }
    any
}

/// Pseudo-legal piece moves for the piece on `sq` (ignores own-king safety).
pub fn piece_moves(pos: &Position, sq: usize, out: &mut Vec<Move>) {
    let o = pos.occ[sq];
    let us = color_of(o);
    let geo = &*pos.geo;
    let nbs = &geo.nb;
    let own = |t: usize| {
        let x = pos.occ[t];
        is_piece(x) && color_of(x) == us
    };
    match kind_of(o) {
        KING => {
            for d in 0..8 {
                let t = nbs[sq][d];
                if t == NO_SQ {
                    continue;
                }
                let t = t as usize;
                if pos.terrain[t] != Terrain::Rocks && !own(t) {
                    out.push(Move::new(sq, t));
                }
            }
        }
        KNIGHT => {
            for &t in &geo.knight[sq] {
                let t = t as usize;
                if pos.terrain[t] != Terrain::Rocks && !own(t) {
                    out.push(Move::new(sq, t));
                }
            }
        }
        PAWN => {
            for d in 0..8 {
                let t = nbs[sq][d];
                if t == NO_SQ {
                    continue;
                }
                let t = t as usize;
                if pos.terrain[t] == Terrain::Rocks {
                    continue;
                }
                let x = pos.occ[t];
                if d & 1 == 0 {
                    if x == EMPTY {
                        out.push(Move::new(sq, t));
                    }
                } else if x == TREASURE || (is_piece(x) && color_of(x) != us) {
                    out.push(Move::new(sq, t));
                }
            }
        }
        k => {
            let (d0, step) = match k {
                ROOK => (0, 2),
                BISHOP => (1, 2),
                _ => (0, 1), // queen
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
                    let x = pos.occ[cur];
                    if x != EMPTY {
                        if !(is_piece(x) && color_of(x) == us) {
                            out.push(Move::new(sq, cur));
                        }
                        break;
                    }
                    out.push(Move::new(sq, cur));
                    if pos.terrain[cur].stops_rays() {
                        break;
                    }
                }
                d += step;
            }
        }
    }
}

/// Squares where the side to move may place: (around king, around king + non-pawn pieces,
/// around queens but not around the king — only filled when the rules allow queen drops).
fn placement_squares(pos: &Position, king_sqs: &mut Vec<usize>, pawn_sqs: &mut Vec<usize>, queen_sqs: &mut Vec<usize>) {
    let us = pos.side;
    let nbs = &pos.geo.nb;
    let mut seen = [false; MAX_SQ];
    let add_around = |src: usize, list: &mut Vec<usize>, seen: &mut [bool; MAX_SQ]| {
        for d in 0..8 {
            let t = nbs[src][d];
            if t == NO_SQ {
                continue;
            }
            let t = t as usize;
            if !seen[t] && pos.terrain[t] != Terrain::Rocks && pos.occ[t] == EMPTY {
                seen[t] = true;
                list.push(t);
            }
        }
    };
    add_around(pos.king[us], king_sqs, &mut seen);
    pawn_sqs.extend_from_slice(king_sqs);
    if pos.rules.queen_drops != 0 {
        let mut seen_q = seen;
        let queen = make_piece(QUEEN, us);
        for sq in 0..pos.n() {
            if pos.occ[sq] == queen {
                add_around(sq, queen_sqs, &mut seen_q);
            }
        }
    }
    for sq in 0..pos.n() {
        let o = pos.occ[sq];
        if is_piece(o) && color_of(o) == us && kind_of(o) != PAWN && kind_of(o) != KING {
            add_around(sq, pawn_sqs, &mut seen);
        }
    }
}

#[inline]
fn is_legal_after(pos: &mut Position, m: Move) -> bool {
    let us = pos.side;
    let u = pos.make(m);
    let ok = !pos.is_attacked(pos.king[us], us ^ 1);
    pos.unmake(&u);
    ok
}

/// All legal moves for the side to move. `out` is cleared first.
pub fn legal_moves(pos: &mut Position, out: &mut Vec<Move>) {
    gen_legal(pos, out, false);
}

/// Legal tactical moves only (see `Position::is_tactical`), for quiescence search.
pub fn tactical_moves(pos: &mut Position, out: &mut Vec<Move>) {
    gen_legal(pos, out, true);
}

fn gen_legal(pos: &mut Position, out: &mut Vec<Move>, tactical_only: bool) {
    out.clear();
    let us = pos.side;
    let in_check = pos.in_check();
    let mut pinned = [false; MAX_SQ];
    let any_pinned = !in_check && pinned_mask(pos, &mut pinned);

    let mut pseudo: Vec<Move> = Vec::with_capacity(128);
    for sq in 0..pos.n() {
        let o = pos.occ[sq];
        if !(is_piece(o) && color_of(o) == us) {
            continue;
        }
        pseudo.clear();
        piece_moves(pos, sq, &mut pseudo);
        let must_test = in_check || kind_of(o) == KING || (any_pinned && pinned[sq]);
        for &m in &pseudo {
            if tactical_only && !pos.is_tactical(m) {
                continue;
            }
            if !must_test || is_legal_after(pos, m) {
                out.push(m);
            }
        }
    }

    let gold = pos.gold[us];
    if gold >= MIN_PIECE_COST {
        let mut ks = Vec::with_capacity(8);
        let mut ps = Vec::with_capacity(32);
        let mut qs = Vec::new();
        placement_squares(pos, &mut ks, &mut ps, &mut qs);
        for &(kind, _) in SHOP.iter() {
            if gold < pos.rules.cost(kind) {
                continue;
            }
            let extra: &[usize] = if kind != PAWN && pos.rules.queen_drop_allowed(kind) { &qs } else { &[] };
            let squares = if kind == PAWN { &ps[..] } else { &ks[..] };
            for &t in squares.iter().chain(extra) {
                let m = Move::placement(kind, t);
                if tactical_only && !pos.is_tactical(m) {
                    continue;
                }
                if !in_check || is_legal_after(pos, m) {
                    out.push(m);
                }
            }
        }
    }
}

/// Does the side to move have at least one legal move?
pub fn has_legal_move(pos: &mut Position) -> bool {
    let mut v = Vec::with_capacity(128);
    legal_moves(pos, &mut v);
    !v.is_empty()
}

/// Count leaf nodes to `depth` (move-generator correctness/speed test).
pub fn perft(pos: &mut Position, depth: u32) -> u64 {
    let mut moves = Vec::with_capacity(128);
    legal_moves(pos, &mut moves);
    if depth <= 1 {
        return if depth == 0 { 1 } else { moves.len() as u64 };
    }
    let mut total = 0;
    for &m in &moves {
        let u = pos.make(m);
        total += perft(pos, depth - 1);
        pos.unmake(&u);
    }
    total
}

#[cfg(test)]
mod tests {
    use super::*;

    /// White king a1, white queen e5, black king h8 on an empty 8x8 board; White has 100 gold.
    /// `std` below = classic rules, `var` = current rules (drops next to the queen, queen 75).
    fn setup(rules: Rules) -> Position {
        let mut pos = Position::empty(8);
        pos.occ[0] = make_piece(KING, WHITE);
        pos.occ[4 * 8 + 4] = make_piece(QUEEN, WHITE);
        pos.occ[63] = make_piece(KING, BLACK);
        pos.gold = [100, 6];
        pos.rules = rules;
        pos.refresh();
        pos
    }

    fn placements(pos: &mut Position, kind: u8) -> Vec<usize> {
        let mut v = Vec::new();
        legal_moves(pos, &mut v);
        let mut sqs: Vec<usize> = v.iter().filter(|m| m.place == kind).map(|m| m.to as usize).collect();
        sqs.sort();
        sqs
    }

    #[test]
    fn queen_drops_variant() {
        let next_to_queen = |sqs: &[usize]| sqs.iter().filter(|&&s| (s % 8).abs_diff(4) <= 1 && (s / 8).abs_diff(4) <= 1).count();
        let mut std = setup(Rules::classic());
        assert_eq!(next_to_queen(&placements(&mut std, ROOK)), 0);
        assert_eq!(next_to_queen(&placements(&mut std, PAWN)), 8); // pawns: already allowed
        assert_eq!(placements(&mut std, ROOK).len(), 3); // around the a1 king

        let mut var = setup(Rules::default());
        for kind in [ROOK, BISHOP, KNIGHT] {
            let sqs = placements(&mut var, kind);
            assert_eq!(sqs.len(), 3 + 8, "kind {kind}");
            assert_eq!(next_to_queen(&sqs), 8);
        }
        assert_eq!(next_to_queen(&placements(&mut var, QUEEN)), 0); // queens still only next to the king

        // queen costs 75 in the variant
        let m = Move::placement(QUEEN, 1);
        let u = var.make(m);
        assert_eq!(var.gold[WHITE], 25);
        var.unmake(&u);
        var.gold[WHITE] = 74;
        assert!(placements(&mut var, QUEEN).is_empty());
        assert!(!placements(&mut std, QUEEN).is_empty());
    }
}
