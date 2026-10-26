//! Board representation, make/unmake, attack detection, game-end rules.

use std::sync::{Arc, OnceLock};

pub const MAX_SQ: usize = 256;
pub const NO_SQ: u16 = u16::MAX;

pub const WHITE: usize = 0;
pub const BLACK: usize = 1;

// Piece kinds
pub const KING: u8 = 1;
pub const QUEEN: u8 = 2;
pub const ROOK: u8 = 3;
pub const BISHOP: u8 = 4;
pub const KNIGHT: u8 = 5;
pub const PAWN: u8 = 6;

/// Occupant code: 0 = empty, TREASURE, or a piece = kind | color << 3.
pub const EMPTY: u8 = 0;
pub const TREASURE: u8 = 7;

pub const MINE_INCOME: i32 = 3;
pub const TREASURE_GOLD: i32 = 20;

/// Placeable kinds in shop order, with costs.
pub const SHOP: [(u8, i32); 5] = [(QUEEN, 75), (ROOK, 40), (BISHOP, 32), (KNIGHT, 30), (PAWN, 26)];
pub const MIN_PIECE_COST: i32 = 26;

#[inline]
pub fn piece_cost(kind: u8) -> i32 {
    match kind {
        QUEEN => 75,
        ROOK => 40,
        BISHOP => 32,
        KNIGHT => 30,
        PAWN => 26,
        _ => 0,
    }
}

/// Placement rules that changed over time: piece kinds that may also be placed next to one of
/// your queens (bitmask of `1 << kind`; 0 = only next to the king) and the queen's price.
/// Default = current rules (since 2026-10-26: rooks, bishops and knights also next to a queen;
/// queen 75). `classic()` = the rules before that, for old data and comparisons.
/// Constant for a whole game, so not part of the hash.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Rules {
    pub queen_drops: u8,
    pub queen_cost: i32,
}

impl Default for Rules {
    fn default() -> Self {
        Rules { queen_drops: Self::drops_mask("rbn"), queen_cost: piece_cost(QUEEN) }
    }
}

impl Rules {
    /// Rules before 2026-10-26: placements only next to the king, queen costs 72.
    pub fn classic() -> Self {
        Rules { queen_drops: 0, queen_cost: 72 }
    }

    /// Bitmask from piece letters, e.g. "rbn".
    pub fn drops_mask(letters: &str) -> u8 {
        letters
            .chars()
            .map(|c| match c.to_ascii_lowercase() {
                'q' => 1 << QUEEN,
                'r' => 1 << ROOK,
                'b' => 1 << BISHOP,
                'n' => 1 << KNIGHT,
                'p' => 1 << PAWN,
                _ => 0,
            })
            .fold(0, |a, b| a | b)
    }

    #[inline]
    pub fn cost(&self, kind: u8) -> i32 {
        if kind == QUEEN {
            self.queen_cost
        } else {
            piece_cost(kind)
        }
    }

    #[inline]
    pub fn queen_drop_allowed(&self, kind: u8) -> bool {
        self.queen_drops & (1 << kind) != 0
    }
}

#[inline]
pub const fn make_piece(kind: u8, color: usize) -> u8 {
    kind | ((color as u8) << 3)
}
#[inline]
pub const fn is_piece(o: u8) -> bool {
    o != EMPTY && o != TREASURE
}
#[inline]
pub const fn color_of(o: u8) -> usize {
    (o >> 3) as usize
}
#[inline]
pub const fn kind_of(o: u8) -> u8 {
    o & 7
}

#[derive(Clone, Copy, PartialEq, Eq, Debug, Default)]
#[repr(u8)]
pub enum Terrain {
    #[default]
    Open = 0,
    Rocks = 1,
    Forest = 2,
    Mine = 3,
}

impl Terrain {
    /// Forest and mines can be entered but stop sliding movement.
    #[inline]
    pub fn stops_rays(self) -> bool {
        matches!(self, Terrain::Forest | Terrain::Mine)
    }
}

/// Directions in the same order as the C# `Direction` enum. Even = orthogonal, odd = diagonal.
pub const DIRS: [(i32, i32); 8] = [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)];
const KNIGHT_DELTAS: [(i32, i32); 8] = [(1, 2), (1, -2), (-1, 2), (-1, -2), (2, 1), (2, -1), (-2, 1), (-2, -1)];

/// Per-board-size lookup tables.
#[derive(Debug)]
pub struct Geometry {
    pub size: usize,
    pub n: usize,
    /// neighbors[sq][dir] or NO_SQ
    pub nb: Vec<[u16; 8]>,
    /// knight targets per square (only on-board ones)
    pub knight: Vec<Vec<u16>>,
}

impl Geometry {
    pub fn new(size: usize) -> Self {
        let n = size * size;
        let mut nb = vec![[NO_SQ; 8]; n];
        let mut knight = vec![Vec::new(); n];
        for y in 0..size as i32 {
            for x in 0..size as i32 {
                let id = (y * size as i32 + x) as usize;
                for (d, (dx, dy)) in DIRS.iter().enumerate() {
                    let (nx, ny) = (x + dx, y + dy);
                    if nx >= 0 && ny >= 0 && nx < size as i32 && ny < size as i32 {
                        nb[id][d] = (ny * size as i32 + nx) as u16;
                    }
                }
                for (dx, dy) in KNIGHT_DELTAS.iter() {
                    let (nx, ny) = (x + dx, y + dy);
                    if nx >= 0 && ny >= 0 && nx < size as i32 && ny < size as i32 {
                        knight[id].push((ny * size as i32 + nx) as u16);
                    }
                }
            }
        }
        Geometry { size, n, nb, knight }
    }
}

/// A move: either a piece move (place == 0) or a placement of kind `place` on `to`.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug, Default)]
pub struct Move {
    pub from: u8,
    pub to: u8,
    pub place: u8,
}

impl Move {
    pub const NULL: Move = Move { from: 0, to: 0, place: 0 };

    #[inline]
    pub fn new(from: usize, to: usize) -> Self {
        Move { from: from as u8, to: to as u8, place: 0 }
    }
    #[inline]
    pub fn placement(kind: u8, to: usize) -> Self {
        Move { from: 0, to: to as u8, place: kind }
    }
    #[inline]
    pub fn is_placement(self) -> bool {
        self.place != 0
    }

    /// Notation like the server: `e2-e4`, `e2xe4` is not distinguished here, `e4=Q`.
    pub fn to_notation(self, size: usize) -> String {
        let sq = |s: u8| format!("{}{}", (b'a' + (s as usize % size) as u8) as char, s as usize / size + 1);
        if self.is_placement() {
            let c = match self.place {
                QUEEN => "Q",
                ROOK => "R",
                BISHOP => "B",
                KNIGHT => "N",
                _ => "p",
            };
            format!("{}={}", sq(self.to), c)
        } else {
            format!("{}-{}", sq(self.from), sq(self.to))
        }
    }
}

#[derive(Clone, Copy, Debug)]
pub struct Undo {
    pub mv: Move,
    pub captured: u8,
    pub prev_owner: u8,
    pub prev_gold: [i32; 2],
    pub prev_frac: [u8; 2],
    pub prev_mines: [i32; 2],
    pub prev_hash: u64,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum WinReason {
    Checkmate,
    Stalemate,
    Gold,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Outcome {
    Ongoing,
    Win { winner: usize, reason: WinReason },
}

// ── Zobrist keys ────────────────────────────────────────────────────────────

pub struct Zobrist {
    pub occ: Vec<[u64; 16]>,
    pub mine: Vec<[u64; 3]>,
    pub side: u64,
}

fn splitmix(state: &mut u64) -> u64 {
    *state = state.wrapping_add(0x9E3779B97F4A7C15);
    let mut z = *state;
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EB);
    z ^ (z >> 31)
}

pub fn zobrist() -> &'static Zobrist {
    static Z: OnceLock<Zobrist> = OnceLock::new();
    Z.get_or_init(|| {
        let mut s = 0x6A09E667F3BCC909u64;
        let mut occ = vec![[0u64; 16]; MAX_SQ];
        let mut mine = vec![[0u64; 3]; MAX_SQ];
        for sq in 0..MAX_SQ {
            for k in 1..16 {
                occ[sq][k] = splitmix(&mut s);
            }
            for k in 1..3 {
                mine[sq][k] = splitmix(&mut s);
            }
        }
        Zobrist { occ, mine, side: splitmix(&mut s) }
    })
}

/// Mix gold amounts (incl. pending tenths) into a hash (gold is part of the game state).
#[inline]
pub fn gold_key(gold: [i32; 2], frac: [u8; 2]) -> u64 {
    let w = gold[0].wrapping_mul(10).wrapping_add(frac[0] as i32);
    let b = gold[1].wrapping_mul(10).wrapping_add(frac[1] as i32);
    let mut s = (w as u32 as u64) | ((b as u32 as u64) << 32);
    splitmix(&mut s)
}

// ── Position ────────────────────────────────────────────────────────────────

#[derive(Clone)]
pub struct Position {
    pub geo: Arc<Geometry>,
    pub terrain: [Terrain; MAX_SQ],
    pub occ: [u8; MAX_SQ],
    /// 0 = unowned, 1 = white, 2 = black
    pub mine_owner: [u8; MAX_SQ],
    pub gold: [i32; 2],
    /// Pending fractional gold in tenths (0 = none). Only used for analysis positions (e.g. the
    /// computer's bid analysis with 7.7 starting gold): each turn start of that side adds 0.1
    /// until it reaches a whole gold, which is then added to `gold`. Only whole gold can be spent.
    pub frac: [u8; 2],
    pub mines: [i32; 2],
    pub king: [usize; 2],
    pub side: usize,
    /// Hash of pieces, treasures, mine owners and side (gold excluded, see `key()`).
    pub hash: u64,
    pub ply: u32,
    pub rules: Rules,
}

impl Position {
    /// Empty board of the given size. Kings must be set with `put` before use.
    pub fn empty(size: usize) -> Self {
        assert!((2..=16).contains(&size));
        Position {
            geo: Arc::new(Geometry::new(size)),
            terrain: [Terrain::Open; MAX_SQ],
            occ: [EMPTY; MAX_SQ],
            mine_owner: [0; MAX_SQ],
            gold: [0, 6],
            frac: [0, 0],
            mines: [0, 0],
            king: [usize::MAX; 2],
            side: WHITE,
            hash: 0,
            ply: 0,
            rules: Rules::default(),
        }
    }

    #[inline]
    pub fn size(&self) -> usize {
        self.geo.size
    }
    #[inline]
    pub fn n(&self) -> usize {
        self.geo.n
    }

    /// Recompute derived state (kings, mine counts, hash) after manual setup.
    pub fn refresh(&mut self) {
        let z = zobrist();
        let mut h = 0u64;
        self.mines = [0, 0];
        for sq in 0..self.n() {
            let o = self.occ[sq];
            if o != EMPTY {
                h ^= z.occ[sq][o as usize];
            }
            if is_piece(o) && kind_of(o) == KING {
                self.king[color_of(o)] = sq;
            }
            if self.terrain[sq] == Terrain::Mine {
                let ow = self.mine_owner[sq];
                if ow != 0 {
                    self.mines[ow as usize - 1] += 1;
                    h ^= z.mine[sq][ow as usize];
                }
            }
        }
        if self.side == BLACK {
            h ^= z.side;
        }
        self.hash = h;
    }

    /// Full TT key including gold.
    #[inline]
    pub fn key(&self) -> u64 {
        self.hash ^ gold_key(self.gold, self.frac)
    }

    pub fn gold_threshold(&self) -> i32 {
        150 + (self.size() as i32 - 6) * 25 / 2
    }

    #[inline]
    pub fn income(&self, color: usize) -> i32 {
        1 + self.mines[color] * MINE_INCOME
    }

    /// Advance pending fractional gold by 0.1 at the start of `color`'s turn.
    #[inline]
    fn tick_frac(&mut self, color: usize) {
        if self.frac[color] != 0 {
            self.frac[color] += 1;
            if self.frac[color] >= 10 {
                self.frac[color] = 0;
                self.gold[color] += 1;
            }
        }
    }

    /// Set a side's gold from a decimal amount, rounded to 0.1 (e.g. 7.7 → 7 + 7 tenths,
    /// −5.3 → −6 + 7 tenths).
    pub fn set_gold_decimal(&mut self, color: usize, amount: f64) {
        let tenths = (amount * 10.0).round() as i64;
        self.gold[color] = tenths.div_euclid(10) as i32;
        self.frac[color] = tenths.rem_euclid(10) as u8;
    }

    // ── make / unmake ───────────────────────────────────────────────────────

    pub fn make(&mut self, m: Move) -> Undo {
        let z = zobrist();
        let us = self.side;
        let to = m.to as usize;
        let undo = Undo {
            mv: m,
            captured: self.occ[to],
            prev_owner: self.mine_owner[to],
            prev_gold: self.gold,
            prev_frac: self.frac,
            prev_mines: self.mines,
            prev_hash: self.hash,
        };

        if m.place == 0 {
            let from = m.from as usize;
            let piece = self.occ[from];
            let cap = self.occ[to];
            self.hash ^= z.occ[from][piece as usize] ^ z.occ[to][piece as usize];
            if cap != EMPTY {
                self.hash ^= z.occ[to][cap as usize];
                if cap == TREASURE {
                    self.gold[us] += TREASURE_GOLD;
                }
            }
            self.occ[to] = piece;
            self.occ[from] = EMPTY;
            if kind_of(piece) == KING {
                self.king[us] = to;
            }
        } else {
            let piece = make_piece(m.place, us);
            self.gold[us] -= self.rules.cost(m.place);
            self.occ[to] = piece;
            self.hash ^= z.occ[to][piece as usize];
        }

        if self.terrain[to] == Terrain::Mine {
            let new_owner = us as u8 + 1;
            let old = self.mine_owner[to];
            if old != new_owner {
                if old != 0 {
                    self.mines[old as usize - 1] -= 1;
                    self.hash ^= z.mine[to][old as usize];
                }
                self.mines[us] += 1;
                self.hash ^= z.mine[to][new_owner as usize];
                self.mine_owner[to] = new_owner;
            }
        }

        self.side ^= 1;
        self.hash ^= z.side;
        self.ply += 1;
        let them = self.side;
        self.gold[them] += self.income(them);
        self.tick_frac(them);
        undo
    }

    pub fn unmake(&mut self, u: &Undo) {
        let m = u.mv;
        let to = m.to as usize;
        self.side ^= 1;
        self.ply -= 1;
        let us = self.side;
        if m.place == 0 {
            let from = m.from as usize;
            let piece = self.occ[to];
            self.occ[from] = piece;
            if kind_of(piece) == KING {
                self.king[us] = from;
            }
        }
        self.occ[to] = u.captured;
        self.mine_owner[to] = u.prev_owner;
        self.gold = u.prev_gold;
        self.frac = u.prev_frac;
        self.mines = u.prev_mines;
        self.hash = u.prev_hash;
    }

    // ── attacks ─────────────────────────────────────────────────────────────

    /// Is `sq` attacked by any piece of color `by`?
    pub fn is_attacked(&self, sq: usize, by: usize) -> bool {
        let geo = &*self.geo;
        let knight = make_piece(KNIGHT, by);
        for &t in &geo.knight[sq] {
            if self.occ[t as usize] == knight {
                return true;
            }
        }
        let king = make_piece(KING, by);
        let pawn = make_piece(PAWN, by);
        let queen = make_piece(QUEEN, by);
        let rook = make_piece(ROOK, by);
        let bishop = make_piece(BISHOP, by);
        let nbs = &geo.nb;
        for d in 0..8 {
            let t = nbs[sq][d];
            if t == NO_SQ {
                continue;
            }
            let t = t as usize;
            let o = self.occ[t];
            if o == king || (d & 1 == 1 && o == pawn) {
                return true;
            }
            // Slider walk
            let line = if d & 1 == 0 { rook } else { bishop };
            let mut cur = t;
            loop {
                if self.terrain[cur] == Terrain::Rocks {
                    break;
                }
                let o = self.occ[cur];
                if o != EMPTY {
                    if o == queen || o == line {
                        return true;
                    }
                    break;
                }
                if self.terrain[cur].stops_rays() {
                    break;
                }
                let nx = nbs[cur][d];
                if nx == NO_SQ {
                    break;
                }
                cur = nx as usize;
            }
        }
        false
    }

    #[inline]
    pub fn in_check(&self) -> bool {
        self.is_attacked(self.king[self.side], self.side ^ 1)
    }

    /// Game status for the side to move, given whether it has any legal move.
    /// Order follows `Game.StartNewTurn`: no-legal-moves first, then gold victory.
    pub fn outcome(&self, has_legal_move: bool) -> Outcome {
        let stm = self.side;
        if !has_legal_move {
            let reason = if self.in_check() { WinReason::Checkmate } else { WinReason::Stalemate };
            return Outcome::Win { winner: stm ^ 1, reason };
        }
        match self.gold_winner() {
            Some(c) => Outcome::Win { winner: c, reason: WinReason::Gold },
            None => Outcome::Ongoing,
        }
    }

    /// Gold-victory winner, if any (ignores the no-legal-moves rule, which takes precedence).
    /// Only the side to move (whose turn is starting) can win by gold, and not while in check.
    #[inline]
    pub fn gold_winner(&self) -> Option<usize> {
        let stm = self.side;
        if self.gold[stm] >= self.gold_threshold() && !self.in_check() {
            Some(stm)
        } else {
            None
        }
    }

    /// "Pass": used for null-move pruning. Mirrors the income step of `make`.
    pub fn make_null(&mut self) -> Undo {
        let undo = Undo {
            mv: Move::NULL,
            captured: 0,
            prev_owner: 0,
            prev_gold: self.gold,
            prev_frac: self.frac,
            prev_mines: self.mines,
            prev_hash: self.hash,
        };
        self.side ^= 1;
        self.hash ^= zobrist().side;
        self.ply += 1;
        let them = self.side;
        self.gold[them] += self.income(them);
        self.tick_frac(them);
        undo
    }

    pub fn unmake_null(&mut self, u: &Undo) {
        self.side ^= 1;
        self.ply -= 1;
        self.gold = u.prev_gold;
        self.frac = u.prev_frac;
        self.hash = u.prev_hash;
    }

    /// Captures an enemy piece or treasure, or takes a mine we don't own.
    #[inline]
    pub fn is_tactical(&self, m: Move) -> bool {
        let to = m.to as usize;
        if self.terrain[to] == Terrain::Mine && self.mine_owner[to] != self.side as u8 + 1 {
            return true;
        }
        !m.is_placement() && self.occ[to] != EMPTY
    }

    /// ASCII dump for debugging.
    pub fn dump(&self) -> String {
        let size = self.size();
        let mut s = String::new();
        for y in (0..size).rev() {
            s.push_str(&format!("{:>2} ", y + 1));
            for x in 0..size {
                let sq = y * size + x;
                let o = self.occ[sq];
                let c = if is_piece(o) {
                    let ch = b"?kqrbnp"[kind_of(o) as usize] as char;
                    if color_of(o) == WHITE { ch.to_ascii_uppercase() } else { ch }
                } else if o == TREASURE {
                    '$'
                } else {
                    match self.terrain[sq] {
                        Terrain::Open => '.',
                        Terrain::Rocks => '#',
                        Terrain::Forest => 'f',
                        Terrain::Mine => match self.mine_owner[sq] {
                            1 => 'M',
                            2 => 'm',
                            _ => 'o',
                        },
                    }
                };
                s.push(c);
                s.push(' ');
            }
            s.push('\n');
        }
        s.push_str("   ");
        for x in 0..size {
            s.push((b'a' + x as u8) as char);
            s.push(' ');
        }
        s.push_str(&format!(
            "\nside={} gold W{} B{} mines W{} B{}\n",
            if self.side == WHITE { "white" } else { "black" },
            self.gold[0],
            self.gold[1],
            self.mines[0],
            self.mines[1]
        ));
        s
    }
}
