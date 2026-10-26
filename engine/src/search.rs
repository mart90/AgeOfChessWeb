//! Iterative-deepening PVS alpha-beta with TT, null move, LMR and quiescence.
//!
//! Scores are from the side to move's perspective. Losing (no legal moves = checkmate or
//! stalemate) and gold victory are scored as `±(MATE - ply)`.

use crate::clock::now_ms;
use crate::eval::{evaluate, EvalParams};
use crate::movegen::{legal_moves, tactical_moves};
use crate::position::*;

pub const MATE: i32 = 1_000_000;
pub const INF: i32 = MATE + 1;
pub const MATE_BOUND: i32 = MATE - 1000;
const MAX_PLY: usize = 100;

#[derive(Clone, Copy, Debug, Default)]
pub struct Limits {
    pub max_depth: Option<u32>,
    pub max_nodes: Option<u64>,
    pub max_time_ms: Option<f64>,
}

#[derive(Clone, Debug)]
pub struct SearchResult {
    pub best: Option<Move>,
    pub score: i32,
    pub depth: u32,
    pub nodes: u64,
    pub time_ms: f64,
    pub pv: Vec<Move>,
}

#[derive(Clone, Copy, Default)]
struct TTEntry {
    key: u64,
    mv: Move,
    score: i32,
    depth: i8,
    flag: u8,
}

const TT_EXACT: u8 = 1;
const TT_LOWER: u8 = 2;
const TT_UPPER: u8 = 3;

pub struct Searcher {
    pub params: EvalParams,
    tt: Vec<TTEntry>,
    tt_mask: usize,
    killers: [[Move; 2]; MAX_PLY],
    /// [side][slot 0..13][to]
    history: Vec<i32>,
    lmr: [[u8; 64]; 64],
    move_bufs: Vec<Vec<Move>>,
    score_bufs: Vec<Vec<i32>>,
    nodes: u64,
    /// Limits are checked when `nodes & check_mask == 0` (finer for tiny budgets).
    check_mask: u64,
    stop: bool,
    start_ms: f64,
    limits: Limits,
    /// Best root move found in the current iteration (fully searched, so valid on abort).
    root_best: Move,
    /// Root moves to skip (multi-PV: already reported lines).
    excluded_root: Vec<Move>,
    /// Called every few thousand nodes with the node count; return true to abort.
    pub should_stop: Option<Box<dyn FnMut(u64) -> bool>>,
}

impl Searcher {
    /// `tt_mb`: transposition-table size in megabytes.
    pub fn new(params: EvalParams, tt_mb: usize) -> Self {
        let entries = (tt_mb * 1024 * 1024 / std::mem::size_of::<TTEntry>()).next_power_of_two() / 2;
        let entries = entries.max(1024);
        let mut lmr = [[0u8; 64]; 64];
        for d in 1..64 {
            for i in 1..64 {
                lmr[d][i] = (0.75 + (d as f64).ln() * (i as f64).ln() / 2.25) as u8;
            }
        }
        Searcher {
            params,
            tt: vec![TTEntry::default(); entries],
            tt_mask: entries - 1,
            killers: [[Move::NULL; 2]; MAX_PLY],
            history: vec![0; 2 * 13 * MAX_SQ],
            lmr,
            move_bufs: (0..MAX_PLY + 1).map(|_| Vec::with_capacity(256)).collect(),
            score_bufs: (0..MAX_PLY + 1).map(|_| Vec::with_capacity(256)).collect(),
            nodes: 0,
            check_mask: 2047,
            stop: false,
            start_ms: 0.0,
            limits: Limits::default(),
            root_best: Move::NULL,
            excluded_root: Vec::new(),
            should_stop: None,
        }
    }

    pub fn clear(&mut self) {
        self.tt.iter_mut().for_each(|e| *e = TTEntry::default());
        self.history.iter_mut().for_each(|h| *h = 0);
        self.killers = [[Move::NULL; 2]; MAX_PLY];
    }

    #[inline]
    fn hist_idx(pos: &Position, m: Move) -> usize {
        let slot = if m.is_placement() { 6 + m.place as usize } else { kind_of(pos.occ[m.from as usize]) as usize };
        (pos.side * 13 + slot) * MAX_SQ + m.to as usize
    }

    fn check_time(&mut self) {
        if let Some(n) = self.limits.max_nodes {
            if self.nodes >= n {
                self.stop = true;
            }
        }
        if let Some(t) = self.limits.max_time_ms {
            if now_ms() - self.start_ms >= t {
                self.stop = true;
            }
        }
        if let Some(f) = self.should_stop.as_mut() {
            if f(self.nodes) {
                self.stop = true;
            }
        }
    }

    // ── TT ──────────────────────────────────────────────────────────────────

    #[inline]
    fn tt_probe(&self, key: u64) -> Option<TTEntry> {
        let e = self.tt[key as usize & self.tt_mask];
        if e.key == key && e.flag != 0 {
            Some(e)
        } else {
            None
        }
    }

    #[inline]
    fn tt_store(&mut self, key: u64, mv: Move, score: i32, depth: i32, flag: u8, ply: usize) {
        let idx = key as usize & self.tt_mask;
        let e = &mut self.tt[idx];
        // Replace if different position or not shallower
        if e.key != key || depth as i8 >= e.depth - 2 || flag == TT_EXACT {
            let s = score_to_tt(score, ply);
            let mv = if mv == Move::NULL && e.key == key { e.mv } else { mv };
            *e = TTEntry { key, mv, score: s, depth: depth.clamp(-1, 120) as i8, flag };
        }
    }

    // ── ordering ────────────────────────────────────────────────────────────

    fn score_moves(&self, pos: &Position, ply: usize, tt_move: Move, moves: &[Move], scores: &mut Vec<i32>) {
        scores.clear();
        let p = &self.params;
        for &m in moves {
            let s = if m == tt_move {
                100_000_000
            } else if pos.is_tactical(m) {
                let to = m.to as usize;
                let victim = pos.occ[to];
                let mut v = if m.is_placement() {
                    0
                } else if victim == TREASURE {
                    TREASURE_GOLD * p.gold
                } else if is_piece(victim) {
                    p.piece[kind_of(victim) as usize]
                } else {
                    0
                };
                if pos.terrain[to] == Terrain::Mine {
                    v += if pos.mine_owner[to] != 0 { 2 * p.mine } else { p.mine };
                }
                let attacker = if m.is_placement() { p.piece[m.place as usize] } else { p.piece[kind_of(pos.occ[m.from as usize]) as usize] };
                10_000_000 + v * 16 - attacker / 16
            } else if m == self.killers[ply][0] {
                9_000_000
            } else if m == self.killers[ply][1] {
                8_000_000
            } else {
                self.history[Self::hist_idx(pos, m)]
            };
            scores.push(s);
        }
    }

    #[inline]
    fn pick(moves: &mut [Move], scores: &mut [i32], i: usize) -> Move {
        let mut best = i;
        for j in i + 1..moves.len() {
            if scores[j] > scores[best] {
                best = j;
            }
        }
        moves.swap(i, best);
        scores.swap(i, best);
        moves[i]
    }

    // ── search ──────────────────────────────────────────────────────────────

    pub fn search(&mut self, pos: &mut Position, limits: Limits) -> SearchResult {
        self.limits = limits;
        self.stop = false;
        self.nodes = 0;
        let small = limits.max_nodes.is_some_and(|n| n < 200_000) || limits.max_time_ms.is_some_and(|t| t < 100.0);
        self.check_mask = if small { 127 } else { 2047 };
        self.start_ms = now_ms();
        for h in self.history.iter_mut() {
            *h /= 4;
        }
        self.killers = [[Move::NULL; 2]; MAX_PLY];

        let max_depth = limits.max_depth.unwrap_or(MAX_PLY as u32 - 10).min(MAX_PLY as u32 - 10);
        let mut result = SearchResult { best: None, score: 0, depth: 0, nodes: 0, time_ms: 0.0, pv: vec![] };

        let mut root_moves = Vec::new();
        legal_moves(pos, &mut root_moves);
        root_moves.retain(|m| !self.excluded_root.contains(m));
        if root_moves.is_empty() {
            result.score = -MATE;
            return result;
        }
        result.best = Some(root_moves[0]);
        // Only reply: play it instantly (not for multi-PV lines, which need a real score)
        if root_moves.len() == 1 && self.excluded_root.is_empty() {
            result.depth = 1;
            result.score = evaluate(pos, &self.params);
            return result;
        }

        let mut prev_score: i32 = 0;
        for depth in 1..=max_depth {
            // Aspiration window around the previous score
            let mut delta = 300;
            let (mut alpha, mut beta) = if depth >= 4 && prev_score.abs() < MATE_BOUND {
                (prev_score - delta, prev_score + delta)
            } else {
                (-INF, INF)
            };
            self.root_best = Move::NULL;
            let score = loop {
                let s = self.negamax(pos, depth as i32, alpha, beta, 0, true);
                if self.stop {
                    break s;
                }
                if s <= alpha {
                    alpha = (alpha - delta).max(-INF);
                    delta *= 2;
                } else if s >= beta {
                    beta = (beta + delta).min(INF);
                    delta *= 2;
                } else {
                    break s;
                }
            };
            if self.root_best != Move::NULL {
                result.best = Some(self.root_best);
            }
            if self.stop {
                break;
            }
            prev_score = score;
            result.score = score;
            result.depth = depth;
            if score.abs() >= MATE_BOUND && depth as i32 > MATE - score.abs() + 2 {
                break; // forced result found
            }
        }
        result.nodes = self.nodes;
        result.time_ms = now_ms() - self.start_ms;
        result.pv = self.extract_pv(pos, result.depth as usize);
        result
    }

    /// Best `n` distinct root moves, each with its own score and PV (multi-PV).
    pub fn search_multi(&mut self, pos: &mut Position, limits: Limits, n: usize) -> Vec<SearchResult> {
        self.excluded_root.clear();
        let mut lines = Vec::new();
        for _ in 0..n.max(1) {
            let r = self.search(pos, limits);
            match r.best {
                Some(m) if !self.excluded_root.contains(&m) => {
                    self.excluded_root.push(m);
                    let mut r = r;
                    if r.pv.first() != Some(&m) {
                        r.pv = vec![m];
                    }
                    lines.push(r);
                }
                _ => break,
            }
            // Each line gets the full limits (a node/time limit stopping one line must not end the rest).
        }
        self.excluded_root.clear();
        lines.sort_by(|a, b| b.score.cmp(&a.score));
        lines
    }

    fn extract_pv(&self, pos: &mut Position, max_len: usize) -> Vec<Move> {
        let mut pv = Vec::new();
        let mut undos = Vec::new();
        let mut moves = Vec::new();
        while pv.len() < max_len.max(1) {
            let Some(e) = self.tt_probe(pos.key()) else { break };
            legal_moves(pos, &mut moves);
            if e.mv == Move::NULL || !moves.contains(&e.mv) {
                break;
            }
            pv.push(e.mv);
            undos.push(pos.make(e.mv));
        }
        for u in undos.iter().rev() {
            pos.unmake(u);
        }
        pv
    }

    fn negamax(&mut self, pos: &mut Position, mut depth: i32, mut alpha: i32, beta: i32, ply: usize, allow_null: bool) -> i32 {
        if self.nodes & self.check_mask == 0 {
            self.check_time();
        }
        if self.stop {
            return 0;
        }
        self.nodes += 1;
        let pv_node = beta - alpha > 1;
        let stm = pos.side;

        // Gold victory can only go to the side to move (checked after the no-legal-moves rule).
        let gold_win = if ply > 0 { pos.gold_winner() } else { None };

        let in_check = pos.in_check();
        if in_check && ply < MAX_PLY / 2 {
            depth += 1;
        }
        if depth <= 0 || ply >= MAX_PLY - 2 {
            return self.qsearch(pos, alpha, beta, ply);
        }

        // Mate distance pruning
        let alpha_md = alpha.max(-(MATE - ply as i32));
        let beta_md = beta.min(MATE - ply as i32 - 1);
        if ply > 0 && alpha_md >= beta_md {
            return alpha_md;
        }

        let key = pos.key();
        let mut tt_move = Move::NULL;
        if let Some(e) = self.tt_probe(key) {
            tt_move = e.mv;
            if ply > 0 && !pv_node && e.depth as i32 >= depth {
                let s = score_from_tt(e.score, ply);
                match e.flag {
                    TT_EXACT => return s,
                    TT_LOWER if s >= beta => return s,
                    TT_UPPER if s <= alpha => return s,
                    _ => {}
                }
            }
        }

        let mut moves = std::mem::take(&mut self.move_bufs[ply]);
        legal_moves(pos, &mut moves);
        if moves.is_empty() {
            self.move_bufs[ply] = moves;
            return -(MATE - ply as i32); // checkmate or stalemate: side to move loses
        }
        if gold_win == Some(stm) {
            self.move_bufs[ply] = moves;
            return MATE - ply as i32;
        }

        let static_eval = if in_check { -INF } else { evaluate(pos, &self.params) };

        if !pv_node && !in_check && ply > 0 {
            // Reverse futility pruning
            if depth <= 3 && static_eval - 150 * depth >= beta && beta.abs() < MATE_BOUND {
                self.move_bufs[ply] = moves;
                return static_eval;
            }
            // Null move pruning (skip when only the king is left: zugzwang/stalemate risk)
            if allow_null && depth >= 3 && static_eval >= beta && has_non_king(pos) {
                let r = 2 + depth / 4;
                let u = pos.make_null();
                let s = -self.negamax(pos, depth - 1 - r, -beta, -beta + 1, ply + 1, false);
                pos.unmake_null(&u);
                if self.stop {
                    self.move_bufs[ply] = moves;
                    return 0;
                }
                if s >= beta && s < MATE_BOUND {
                    self.move_bufs[ply] = moves;
                    return s;
                }
            }
        }

        let mut scores = std::mem::take(&mut self.score_bufs[ply]);
        self.score_moves(pos, ply, tt_move, &moves, &mut scores);

        let orig_alpha = alpha;
        let mut best = -INF;
        let mut best_move = Move::NULL;
        let mut quiets_tried = 0;

        let mut searched = 0usize;
        for i in 0..moves.len() {
            let m = Self::pick(&mut moves, &mut scores, i);
            if ply == 0 && !self.excluded_root.is_empty() && self.excluded_root.contains(&m) {
                continue;
            }
            let tactical = pos.is_tactical(m);
            let is_killer = m == self.killers[ply][0] || m == self.killers[ply][1];

            // Late move pruning of quiet moves at shallow depth
            if !pv_node && !in_check && !tactical && depth <= 3 && best > -MATE_BOUND && quiets_tried >= 4 + 4 * depth * depth {
                continue;
            }

            let u = pos.make(m);
            let gives_check = pos.in_check();
            let mut s;
            if searched == 0 {
                s = -self.negamax(pos, depth - 1, -beta, -alpha, ply + 1, true);
            } else {
                let mut r = 0;
                if depth >= 3 && searched >= 2 && !tactical && !in_check && !gives_check && !is_killer {
                    r = self.lmr[depth.min(63) as usize][searched.min(63)] as i32;
                    if pv_node {
                        r -= 1;
                    }
                    r = r.clamp(0, depth - 2);
                }
                s = -self.negamax(pos, depth - 1 - r, -alpha - 1, -alpha, ply + 1, true);
                if s > alpha && r > 0 {
                    s = -self.negamax(pos, depth - 1, -alpha - 1, -alpha, ply + 1, true);
                }
                if s > alpha && s < beta {
                    s = -self.negamax(pos, depth - 1, -beta, -alpha, ply + 1, true);
                }
            }
            pos.unmake(&u);
            searched += 1;
            if self.stop {
                self.move_bufs[ply] = moves;
                self.score_bufs[ply] = scores;
                return 0;
            }
            if !tactical {
                quiets_tried += 1;
            }

            if s > best {
                best = s;
                best_move = m;
                if s > alpha {
                    alpha = s;
                    if ply == 0 {
                        self.root_best = m;
                    }
                    if s >= beta {
                        if !tactical {
                            if self.killers[ply][0] != m {
                                self.killers[ply][1] = self.killers[ply][0];
                                self.killers[ply][0] = m;
                            }
                            let h = &mut self.history[Self::hist_idx(pos, m)];
                            *h = (*h + depth * depth).min(1_000_000);
                        }
                        break;
                    }
                }
            }
        }

        let flag = if best >= beta {
            TT_LOWER
        } else if best > orig_alpha {
            TT_EXACT
        } else {
            TT_UPPER
        };
        if ply > 0 || self.excluded_root.is_empty() {
            self.tt_store(key, best_move, best, depth, flag, ply);
        }
        self.move_bufs[ply] = moves;
        self.score_bufs[ply] = scores;
        best
    }

    fn qsearch(&mut self, pos: &mut Position, mut alpha: i32, beta: i32, ply: usize) -> i32 {
        if self.nodes & self.check_mask == 0 {
            self.check_time();
        }
        if self.stop {
            return 0;
        }
        self.nodes += 1;
        if pos.gold_winner().is_some() {
            return MATE - ply as i32; // side to move wins (assumes it has a legal move)
        }
        if ply >= MAX_PLY - 2 {
            return evaluate(pos, &self.params); // also bounds long check sequences
        }

        let in_check = pos.in_check();
        let mut moves = std::mem::take(&mut self.move_bufs[ply]);
        let mut best;
        if in_check {
            legal_moves(pos, &mut moves);
            if moves.is_empty() {
                self.move_bufs[ply] = moves;
                return -(MATE - ply as i32);
            }
            best = -INF;
        } else {
            let stand = evaluate(pos, &self.params);
            if stand >= beta || ply >= MAX_PLY - 2 {
                self.move_bufs[ply] = moves;
                return stand;
            }
            if stand > alpha {
                alpha = stand;
            }
            best = stand;
            tactical_moves(pos, &mut moves);
        }

        let mut scores = std::mem::take(&mut self.score_bufs[ply]);
        self.score_moves(pos, ply, Move::NULL, &moves, &mut scores);
        for i in 0..moves.len() {
            let m = Self::pick(&mut moves, &mut scores, i);
            let u = pos.make(m);
            let s = -self.qsearch(pos, -beta, -alpha, ply + 1);
            pos.unmake(&u);
            if self.stop {
                break;
            }
            if s > best {
                best = s;
                if s > alpha {
                    alpha = s;
                    if s >= beta {
                        break;
                    }
                }
            }
        }
        self.move_bufs[ply] = moves;
        self.score_bufs[ply] = scores;
        best
    }
}

fn has_non_king(pos: &Position) -> bool {
    let us = pos.side;
    (0..pos.n()).any(|sq| {
        let o = pos.occ[sq];
        is_piece(o) && color_of(o) == us && kind_of(o) != KING
    })
}

#[inline]
fn score_to_tt(s: i32, ply: usize) -> i32 {
    if s >= MATE_BOUND {
        s + ply as i32
    } else if s <= -MATE_BOUND {
        s - ply as i32
    } else {
        s
    }
}

#[inline]
fn score_from_tt(s: i32, ply: usize) -> i32 {
    if s >= MATE_BOUND {
        s - ply as i32
    } else if s <= -MATE_BOUND {
        s + ply as i32
    } else {
        s
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Regression: qsearch in check at the ply limit used to index past the per-ply tables.
    #[test]
    fn qsearch_in_check_at_ply_limit() {
        let mut pos = Position::empty(10);
        pos.occ[0] = make_piece(KING, WHITE);
        pos.occ[99] = make_piece(KING, BLACK);
        pos.occ[9] = make_piece(ROOK, BLACK); // checks the white king along the first rank
        pos.refresh();
        assert!(pos.in_check());
        let mut s = Searcher::new(EvalParams::default(), 1);
        let _ = s.qsearch(&mut pos, -INF, INF, MAX_PLY);
    }
}
