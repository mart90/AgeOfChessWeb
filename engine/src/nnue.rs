//! Evaluation network trained in `training/` (docs/training.md), as an alternative to the
//! hand-written `eval::evaluate`.
//!
//! Two paths: `logit`/`evaluate` compute everything in f32 from scratch (the reference, equal
//! to PyTorch); the search uses `evaluate_incremental`: 16-bit integer first-layer sums kept per
//! search ply, an integer first dense layer, and f32 for the two tiny last layers. Inputs and
//! layers must match `training/features.py` and `training/model.py`.
//!
//! File format (written by `training/export.py`, little-endian): `b"GGNN"`, u32 version,
//! u32 board size N, u32 number of features, u32 hidden, u32 l2, u32 l3, f32 K, then f32
//! arrays: ft weights [feature][hidden], ft bias [hidden], l1 weights [l2][2*hidden + D],
//! l1 bias [l2], l2 weights [l3][l2], l2 bias [l3], out weights [l3], out bias.
//! Version 1: D = 4 dense inputs (gold / threshold, mines / 10, side to move first); version 2:
//! D = 8, adding the gold race: 1 / (t + 1) and min(t, 40) / 40 with t = `eval::turns_to_gold`.
//! Version 3: as 2, plus f32 `race` right after K: a fixed gold-race term added to the output,
//! logit += race * (1/(t_us+1) - 1/(t_them+1)) (the hand-written eval's race term / K); the
//! network was trained as the residual on top of it.
//! Version 4: as 1, plus f32 `ml_weight` and `ml_cap` right after K, and a moves-left head after
//! the out bias: ml1 weights [l3][l2], ml1 bias [l3], ml2 weights [l3], ml2 bias. Its input is the
//! first dense layer's output; it predicts the plies until the game ends / ML_SCALE. See
//! `Network::combine` for how the search uses it (prefer faster wins, slower losses).

use crate::eval::turns_to_gold;
use crate::position::*;

pub const MAGIC: &[u8; 4] = b"GGNN";
/// Supported file versions and their number of dense inputs.
const VERSIONS: [(u32, usize); 4] = [(1, 4), (2, 8), (3, 8), (4, 4)];
pub const NUM_BLOCKS: usize = 18;
const MAX_DENSE: usize = 8;
/// Turns at which the race input min(t, H) / H saturates (training/features.py RACE_HORIZON).
const RACE_HORIZON: i32 = 40;
const MAX_HIDDEN: usize = 1024;
const MAX_L: usize = 256;
/// Integer scales: first-layer sums and activations (0..1 -> 0..QA), first dense layer weights.
const QA: i32 = 512;
const QB: i32 = 4096;
/// Moves-left head output unit, in plies (training/model.py ML_SCALE).
pub const ML_SCALE: f32 = 100.0;
/// The moves-left adjustment fades in between these confidences |2p - 1| (p = win probability
/// 0.9 .. 0.99): only clearly decided positions are affected.
const ML_CONF_LO: f32 = 0.8;
const ML_CONF_HI: f32 = 0.98;
/// Predictions are clamped to this many plies.
const ML_MAX_PLIES: f32 = 300.0;

pub struct Network {
    pub board_size: usize,
    pub hidden: usize,
    pub l2: usize,
    pub l3: usize,
    /// Eval units per logit unit: eval = logit * k.
    pub k: f32,
    /// Dense inputs after the two accumulators: 4 (version 1) or 8 (version 2/3, gold race).
    pub num_dense: usize,
    /// Version 3: fixed gold-race term in logit units (0 otherwise).
    pub race: f32,
    /// Version 4: the network has a moves-left head.
    pub has_ml: bool,
    /// Moves-left search weight: eval units per predicted ply (0 = off; default from the file).
    pub ml_weight: f32,
    /// Largest moves-left adjustment, eval units.
    pub ml_cap: f32,
    ft_w: Vec<f32>,
    ft_b: Vec<f32>,
    l1_w: Vec<f32>,
    l1_b: Vec<f32>,
    l2_w: Vec<f32>,
    l2_b: Vec<f32>,
    out_w: Vec<f32>,
    out_b: f32,
    ml1_w: Vec<f32>,
    ml1_b: Vec<f32>,
    ml2_w: Vec<f32>,
    ml2_b: f32,
    // Quantised copies for the search (see QA, QB)
    ft_wq: Vec<i16>,
    ft_bq: Vec<i16>,
    l1_wq: Vec<i16>,
    l1_bq: Vec<i32>,
}

struct Reader<'a> {
    b: &'a [u8],
    at: usize,
}

impl<'a> Reader<'a> {
    fn u32(&mut self) -> Result<u32, String> {
        let s = self.b.get(self.at..self.at + 4).ok_or("network file too short")?;
        self.at += 4;
        Ok(u32::from_le_bytes(s.try_into().unwrap()))
    }
    fn f32s(&mut self, n: usize) -> Result<Vec<f32>, String> {
        let s = self.b.get(self.at..self.at + 4 * n).ok_or("network file too short")?;
        self.at += 4 * n;
        Ok(s.chunks_exact(4).map(|c| f32::from_le_bytes(c.try_into().unwrap())).collect())
    }
}

impl Network {
    pub fn from_bytes(b: &[u8]) -> Result<Network, String> {
        if b.len() < 32 || &b[0..4] != MAGIC {
            return Err("not a GGNN network file".into());
        }
        let mut r = Reader { b, at: 4 };
        let version = r.u32()?;
        let num_dense = match VERSIONS.iter().find(|v| v.0 == version) {
            Some(&(_, d)) => d,
            None => return Err(format!("network version {version}, engine supports {:?}", VERSIONS.map(|v| v.0))),
        };
        let board_size = r.u32()? as usize;
        let features = r.u32()? as usize;
        let hidden = r.u32()? as usize;
        let l2 = r.u32()? as usize;
        let l3 = r.u32()? as usize;
        let k = f32::from_bits(r.u32()?);
        let race = if version == 3 { f32::from_bits(r.u32()?) } else { 0.0 };
        let has_ml = version == 4;
        let (ml_weight, ml_cap) = if has_ml { (f32::from_bits(r.u32()?), f32::from_bits(r.u32()?)) } else { (0.0, 0.0) };
        if features != NUM_BLOCKS * board_size * board_size {
            return Err(format!("{features} features, expected {} for {board_size}x{board_size}", NUM_BLOCKS * board_size * board_size));
        }
        if hidden > MAX_HIDDEN || l2 > MAX_L || l3 > MAX_L {
            return Err("network layers too large".into());
        }
        let mut net = Network {
            board_size,
            hidden,
            l2,
            l3,
            k,
            num_dense,
            race,
            has_ml,
            ml_weight,
            ml_cap,
            ft_w: r.f32s(features * hidden)?,
            ft_b: r.f32s(hidden)?,
            l1_w: r.f32s(l2 * (2 * hidden + num_dense))?,
            l1_b: r.f32s(l2)?,
            l2_w: r.f32s(l3 * l2)?,
            l2_b: r.f32s(l3)?,
            out_w: r.f32s(l3)?,
            out_b: r.f32s(1)?[0],
            ml1_w: if has_ml { r.f32s(l3 * l2)? } else { Vec::new() },
            ml1_b: if has_ml { r.f32s(l3)? } else { Vec::new() },
            ml2_w: if has_ml { r.f32s(l3)? } else { Vec::new() },
            ml2_b: if has_ml { r.f32s(1)?[0] } else { 0.0 },
            ft_wq: Vec::new(),
            ft_bq: Vec::new(),
            l1_wq: Vec::new(),
            l1_bq: Vec::new(),
        };
        if r.at != b.len() {
            return Err("trailing bytes in network file".into());
        }
        let q16 = |v: &[f32], scale: i32| -> Result<Vec<i16>, String> {
            v.iter()
                .map(|&x| {
                    let q = (x * scale as f32).round();
                    if q.abs() > 32767.0 { Err("weight too large for 16-bit quantisation".to_string()) } else { Ok(q as i16) }
                })
                .collect()
        };
        net.ft_wq = q16(&net.ft_w, QA)?;
        net.ft_bq = q16(&net.ft_b, QA)?;
        net.l1_wq = q16(&net.l1_w, QB)?;
        net.l1_bq = net.l1_b.iter().map(|&b| (b * (QA * QB) as f32).round() as i32).collect();
        Ok(net)
    }

    pub fn load(path: &str) -> Result<Network, String> {
        Network::from_bytes(&std::fs::read(path).map_err(|e| format!("{path}: {e}"))?)
    }

    /// Active features of `pos` seen from `persp` (WHITE/BLACK): per square at most one piece
    /// or treasure feature and one terrain feature. Same table as training/features.py.
    pub fn features(pos: &Position, persp: usize, out: &mut Vec<usize>) {
        out.clear();
        let n = pos.n();
        for sq in 0..n {
            let o = pos.occ[sq];
            if o == TREASURE {
                out.push(12 * n + sq);
            } else if is_piece(o) {
                let kind = (kind_of(o) - 1) as usize;
                let block = if color_of(o) == persp { kind } else { 6 + kind };
                out.push(block * n + sq);
            }
            let block = match pos.terrain[sq] {
                Terrain::Open => continue,
                Terrain::Rocks => 13,
                Terrain::Forest => 14,
                Terrain::Mine => match pos.mine_owner[sq] {
                    0 => 15,
                    ow if ow as usize - 1 == persp => 16,
                    _ => 17,
                },
            };
            out.push(block * n + sq);
        }
    }

    /// Version 3: the fixed gold-race term (logit, side to move), as in training/model.py.
    #[inline]
    fn race_term(&self, pos: &Position) -> f32 {
        if self.race == 0.0 {
            return 0.0;
        }
        let (t0, t1) = (turns_to_gold(pos, pos.side), turns_to_gold(pos, pos.side ^ 1));
        self.race * (1.0 / (t0 + 1) as f32 - 1.0 / (t1 + 1) as f32)
    }

    /// Win-probability logit for the side to move.
    pub fn logit(&self, pos: &Position) -> f32 {
        self.forward_f32(pos).0
    }

    /// Moves-left head output (plies until the game ends / ML_SCALE), f32 path; 0 without a head.
    pub fn moves_left_raw(&self, pos: &Position) -> f32 {
        self.forward_f32(pos).1
    }

    /// Last layers on top of the first dense layer's outputs `y1`: (logit, moves-left output).
    fn head(&self, pos: &Position, y1: &[f32]) -> (f32, f32) {
        let mut y2 = [0f32; MAX_L];
        for j in 0..self.l3 {
            let w = &self.l2_w[j * self.l2..(j + 1) * self.l2];
            let s: f32 = w.iter().zip(&y1[..self.l2]).map(|(a, b)| a * b).sum();
            y2[j] = (s + self.l2_b[j]).clamp(0.0, 1.0);
        }
        let s: f32 = self.out_w.iter().zip(&y2[..self.l3]).map(|(a, b)| a * b).sum();
        let value = s + self.out_b + self.race_term(pos);
        if !self.has_ml {
            return (value, 0.0);
        }
        let mut m = 0f32;
        for j in 0..self.l3 {
            let w = &self.ml1_w[j * self.l2..(j + 1) * self.l2];
            let s: f32 = w.iter().zip(&y1[..self.l2]).map(|(a, b)| a * b).sum();
            m += (s + self.ml1_b[j]).clamp(0.0, 1.0) * self.ml2_w[j];
        }
        (value, m + self.ml2_b)
    }

    /// Engine evaluation (side to move, 1 gold = 10) from the logit and the moves-left output.
    /// In clearly decided positions the side that is winning prefers a shorter predicted game and
    /// the losing side a longer one: |eval| shrinks by up to `ml_weight` per predicted ply (capped
    /// at `ml_cap` and at half of |eval|, so it never changes who is winning).
    #[inline]
    pub fn combine(&self, logit: f32, ml: f32) -> i32 {
        let e = logit * self.k;
        if !self.has_ml || self.ml_weight == 0.0 {
            return e.round() as i32;
        }
        let conf = (2.0 / (1.0 + (-logit.abs()).exp()) - 1.0 - ML_CONF_LO) / (ML_CONF_HI - ML_CONF_LO);
        let conf = conf.clamp(0.0, 1.0);
        let plies = (ml * ML_SCALE).clamp(0.0, ML_MAX_PLIES);
        let adj = (conf * self.ml_weight * plies).min(self.ml_cap).min(e.abs() * 0.5);
        (e - logit.signum() * adj).round() as i32
    }

    fn forward_f32(&self, pos: &Position) -> (f32, f32) {
        let h = self.hidden;
        let stm = pos.side;
        let mut feats = Vec::with_capacity(64);
        let mut x = [0f32; 2 * MAX_HIDDEN + MAX_DENSE];
        for (half, persp) in [stm, stm ^ 1].into_iter().enumerate() {
            let acc = &mut x[half * h..half * h + h];
            acc.copy_from_slice(&self.ft_b);
            Network::features(pos, persp, &mut feats);
            for &f in &feats {
                let w = &self.ft_w[f * h..f * h + h];
                for (a, wi) in acc.iter_mut().zip(w) {
                    *a += wi;
                }
            }
            for a in acc.iter_mut() {
                *a = a.clamp(0.0, 1.0);
            }
        }
        let size = self.board_size as i32;
        let threshold = (150 + 25 * (size - 6) / 2) as f32;
        x[2 * h] = pos.gold[stm] as f32 / threshold;
        x[2 * h + 1] = pos.gold[stm ^ 1] as f32 / threshold;
        x[2 * h + 2] = pos.mines[stm] as f32 / 10.0;
        x[2 * h + 3] = pos.mines[stm ^ 1] as f32 / 10.0;
        if self.num_dense > 4 {
            let (t0, t1) = (turns_to_gold(pos, stm), turns_to_gold(pos, stm ^ 1));
            x[2 * h + 4] = 1.0 / (t0 + 1) as f32;
            x[2 * h + 5] = 1.0 / (t1 + 1) as f32;
            x[2 * h + 6] = t0.min(RACE_HORIZON) as f32 / RACE_HORIZON as f32;
            x[2 * h + 7] = t1.min(RACE_HORIZON) as f32 / RACE_HORIZON as f32;
        }
        let inputs = &x[..2 * h + self.num_dense];

        let mut y1 = [0f32; MAX_L];
        for j in 0..self.l2 {
            let w = &self.l1_w[j * inputs.len()..(j + 1) * inputs.len()];
            let s: f32 = w.iter().zip(inputs).map(|(a, b)| a * b).sum();
            y1[j] = (s + self.l1_b[j]).clamp(0.0, 1.0);
        }
        self.head(pos, &y1)
    }

    /// Evaluation in engine units (1 gold = 10) for the side to move.
    pub fn evaluate(&self, pos: &Position) -> i32 {
        let (v, ml) = self.forward_f32(pos);
        self.combine(v, ml)
    }

    // ── Incremental evaluation (used by the search) ─────────────────────────────────────────
    //
    // The first layer is a sum of weight rows, one per active feature, so a move only needs a
    // few rows added/subtracted. `NnStack` keeps one pair of sums (white's and black's
    // perspective) per search ply and brings them up to date lazily, only when an evaluation
    // is requested; moves made just to test legality cost nothing. Sums are 16-bit integers
    // (scale QA), so updates and the next layer vectorise well; integer arithmetic also makes
    // incremental and from-scratch results identical.

    #[inline]
    fn piece_feature(o: u8, sq: usize, persp: usize, n: usize) -> usize {
        let block = if o == TREASURE {
            12
        } else {
            let kind = (kind_of(o) - 1) as usize;
            if color_of(o) == persp { kind } else { 6 + kind }
        };
        block * n + sq
    }

    #[inline]
    fn mine_feature(owner: u8, sq: usize, persp: usize, n: usize) -> usize {
        let block = match owner {
            0 => 15,
            ow if ow as usize - 1 == persp => 16,
            _ => 17,
        };
        block * n + sq
    }

    /// Both perspectives' sums (white's at [0..h], black's at [h..2h]) from scratch.
    fn refresh(&self, pos: &Position, acc: &mut [i16]) {
        let h = self.hidden;
        let mut feats = Vec::with_capacity(64);
        for persp in [WHITE, BLACK] {
            let a = &mut acc[persp * h..persp * h + h];
            a.copy_from_slice(&self.ft_bq);
            Network::features(pos, persp, &mut feats);
            for &f in &feats {
                for (x, w) in a.iter_mut().zip(&self.ft_wq[f * h..f * h + h]) {
                    *x = x.wrapping_add(*w);
                }
            }
        }
    }

    #[inline]
    fn add_row(&self, a: &mut [i16], f: usize) {
        let h = self.hidden;
        for (x, w) in a.iter_mut().zip(&self.ft_wq[f * h..f * h + h]) {
            *x = x.wrapping_add(*w);
        }
    }

    #[inline]
    fn sub_row(&self, a: &mut [i16], f: usize) {
        let h = self.hidden;
        for (x, w) in a.iter_mut().zip(&self.ft_wq[f * h..f * h + h]) {
            *x = x.wrapping_sub(*w);
        }
    }

    /// `child` = `parent` + the feature changes of move `d`.
    fn apply(&self, parent: &[i16], child: &mut [i16], d: &Dirty, n: usize) {
        child.copy_from_slice(parent);
        if d.null {
            return;
        }
        let h = self.hidden;
        for persp in [WHITE, BLACK] {
            let a = &mut child[persp * h..persp * h + h];
            let to = d.to as usize;
            if !d.placement {
                self.sub_row(a, Network::piece_feature(d.piece, d.from as usize, persp, n));
                if d.captured != EMPTY {
                    self.sub_row(a, Network::piece_feature(d.captured, to, persp, n));
                }
            }
            self.add_row(a, Network::piece_feature(d.piece, to, persp, n));
            if d.mine && d.prev_owner != d.new_owner {
                self.sub_row(a, Network::mine_feature(d.prev_owner, to, persp, n));
                self.add_row(a, Network::mine_feature(d.new_owner, to, persp, n));
            }
        }
    }

    /// Output layers on top of the first-layer sums (`acc` as in `refresh`): (logit, moves-left).
    fn output(&self, pos: &Position, acc: &[i16]) -> (f32, f32) {
        let h = self.hidden;
        let stm = pos.side;
        let ins = 2 * h + self.num_dense;
        let mut x = [0i16; 2 * MAX_HIDDEN + MAX_DENSE];
        for (half, persp) in [stm, stm ^ 1].into_iter().enumerate() {
            for (o, a) in x[half * h..half * h + h].iter_mut().zip(&acc[persp * h..persp * h + h]) {
                *o = (*a).clamp(0, QA as i16);
            }
        }
        let size = self.board_size as i32;
        let threshold = 150 + 25 * (size - 6) / 2;
        let q = |v: i32, div: i32| ((v * QA + div / 2).div_euclid(div)).clamp(-32768, 32767) as i16;
        x[2 * h] = q(pos.gold[stm], threshold);
        x[2 * h + 1] = q(pos.gold[stm ^ 1], threshold);
        x[2 * h + 2] = q(pos.mines[stm], 10);
        x[2 * h + 3] = q(pos.mines[stm ^ 1], 10);
        if self.num_dense > 4 {
            let (t0, t1) = (turns_to_gold(pos, stm), turns_to_gold(pos, stm ^ 1));
            x[2 * h + 4] = q(1, t0 + 1);
            x[2 * h + 5] = q(1, t1 + 1);
            x[2 * h + 6] = q(t0.min(RACE_HORIZON), RACE_HORIZON);
            x[2 * h + 7] = q(t1.min(RACE_HORIZON), RACE_HORIZON);
        }
        let inputs = &x[..ins];

        // First dense layer: integer dot products (exact, so the compiler may vectorise freely)
        let scale = 1.0 / (QA as f32 * QB as f32);
        let mut y1 = [0f32; MAX_L];
        for j in 0..self.l2 {
            let w = &self.l1_wq[j * ins..(j + 1) * ins];
            let s: i32 = w.iter().zip(inputs).map(|(&a, &b)| a as i32 * b as i32).sum();
            y1[j] = ((s + self.l1_bq[j]) as f32 * scale).clamp(0.0, 1.0);
        }
        self.head(pos, &y1)
    }

    /// Moves-left output of the search's (quantised) path, from scratch.
    pub fn moves_left_quantized(&self, pos: &Position) -> f32 {
        let mut acc = vec![0i16; 2 * self.hidden];
        self.refresh(pos, &mut acc);
        self.output(pos, &acc).1
    }

    /// The search's (quantised) evaluation computed from scratch; equals `evaluate_incremental`.
    pub fn evaluate_quantized(&self, pos: &Position) -> i32 {
        let mut acc = vec![0i16; 2 * self.hidden];
        self.refresh(pos, &mut acc);
        let (v, ml) = self.output(pos, &acc);
        self.combine(v, ml)
    }

    /// Evaluation of `pos` at search ply `ply`, using and updating `st`.
    pub fn evaluate_incremental(&self, st: &mut NnStack, pos: &Position, ply: usize) -> i32 {
        let h2 = 2 * self.hidden;
        if st.h2 != h2 {
            *st = NnStack::new(st.entries(), self.hidden);
        }
        if st.hash[ply] != pos.hash || !st.recorded[ply] {
            // Not reached through recorded moves (e.g. the root): compute from scratch.
            st.hash[ply] = pos.hash;
            st.parent[ply] = NO_PARENT;
            st.recorded[ply] = true;
            let (_, rest) = st.acc.split_at_mut(ply * h2);
            self.refresh(pos, &mut rest[..h2]);
            st.ok[ply] = true;
            st.refreshes += 1;
        } else if !st.ok[ply] {
            // Walk back along the current line to the nearest up-to-date ancestor
            let mut k = ply;
            while k > 0 && !st.ok[k] && st.parent[k] == st.hash[k - 1] && st.recorded[k - 1] {
                k -= 1;
            }
            if st.ok[k] {
                let n = pos.n();
                for i in k + 1..=ply {
                    let (before, rest) = st.acc.split_at_mut(i * h2);
                    let d = st.dirty[i];
                    self.apply(&before[(i - 1) * h2..], &mut rest[..h2], &d, n);
                    st.ok[i] = true;
                }
                st.updates += (ply - k) as u64;
            } else {
                let (_, rest) = st.acc.split_at_mut(ply * h2);
                self.refresh(pos, &mut rest[..h2]);
                st.ok[ply] = true;
                st.refreshes += 1;
            }
        }
        let acc = &st.acc[ply * h2..ply * h2 + h2];
        let (v, ml) = self.output(pos, acc);
        self.combine(v, ml)
    }
}

const NO_PARENT: u64 = u64::MAX;

/// What a move changed, for the first-layer update (see `Network::apply`).
#[derive(Clone, Copy, Default)]
pub struct Dirty {
    null: bool,
    placement: bool,
    piece: u8,
    from: u8,
    to: u8,
    captured: u8,
    mine: bool,
    prev_owner: u8,
    new_owner: u8,
}

/// Per-ply first-layer sums for one search (owned by `Searcher`).
pub struct NnStack {
    h2: usize,
    acc: Vec<i16>,
    dirty: Vec<Dirty>,
    /// Hash of the position at this ply and of its parent (the line the entry belongs to).
    hash: Vec<u64>,
    parent: Vec<u64>,
    recorded: Vec<bool>,
    /// `acc` is up to date for `hash`.
    ok: Vec<bool>,
    pub refreshes: u64,
    pub updates: u64,
}

impl NnStack {
    pub fn new(entries: usize, hidden: usize) -> NnStack {
        NnStack {
            h2: 2 * hidden,
            acc: vec![0; entries * 2 * hidden],
            dirty: vec![Dirty::default(); entries],
            hash: vec![0; entries],
            parent: vec![NO_PARENT; entries],
            recorded: vec![false; entries],
            ok: vec![false; entries],
            refreshes: 0,
            updates: 0,
        }
    }

    fn entries(&self) -> usize {
        self.dirty.len()
    }

    /// Forget everything (new search / new position).
    pub fn reset(&mut self) {
        self.recorded.iter_mut().for_each(|r| *r = false);
        self.ok.iter_mut().for_each(|r| *r = false);
    }

    /// Record the move that led to `pos` (already made) at `ply`.
    #[inline]
    pub fn record(&mut self, ply: usize, pos: &Position, u: &Undo) {
        let m = u.mv;
        let to = m.to as usize;
        self.dirty[ply] = Dirty {
            null: false,
            placement: m.is_placement(),
            piece: pos.occ[to],
            from: m.from,
            to: m.to,
            captured: u.captured,
            mine: pos.terrain[to] == Terrain::Mine,
            prev_owner: u.prev_owner,
            new_owner: pos.mine_owner[to],
        };
        self.hash[ply] = pos.hash;
        self.parent[ply] = u.prev_hash;
        self.recorded[ply] = true;
        self.ok[ply] = false;
    }

    /// Record a null move (side to move changes, board doesn't).
    #[inline]
    pub fn record_null(&mut self, ply: usize, pos: &Position, u: &Undo) {
        self.dirty[ply] = Dirty { null: true, ..Default::default() };
        self.hash[ply] = pos.hash;
        self.parent[ply] = u.prev_hash;
        self.recorded[ply] = true;
        self.ok[ply] = false;
    }
}
