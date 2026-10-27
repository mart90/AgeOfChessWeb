//! Training-data records for the evaluation network (written by `gg gendata`, read by
//! `training/ggdata.py`). See `docs/training.md` for the format; keep the two in sync.
//!
//! A file is a 32-byte header followed by fixed-size records, one per position:
//!
//! Header: `b"GGTD"`, u16 version, u16 board size N, u16 record size, u8 queen-drop mask,
//! u8 queen cost, u32 nodes per move, then zero padding to 32 bytes.
//!
//! Record (N*N + 24 bytes, little-endian):
//! - N*N square bytes (square id = y*N + x): bits 0-3 content (0 empty, 1-6 white K Q R B N P,
//!   7-12 black K Q R B N P, 13 treasure), bits 4-5 terrain (0 open, 1 rocks, 2 forest, 3 mine),
//!   bits 6-7 mine owner (0 none, 1 white, 2 black)
//! - u8 side to move (0 white, 1 black)
//! - u8 flags: 1 = side to move in check, 2 = best move is tactical (capture, treasure, mine),
//!   4 = the move played was a random opening move, 8 = score is a mate score
//! - i16 white gold, i16 black gold
//! - u16 ply
//! - i16 search score for the side to move (eval units, 1 gold = 10; mates = ±32000)
//! - i8 game result for the side to move (+1 win, 0 draw / unfinished, -1 loss)
//! - u8 result reason (0 none, 1 checkmate, 2 gold, 3 stalemate)
//! - u32 game id
//! - u8 best move from-square (255 for placements), u8 to-square, u8 placed kind (0 = move)
//! - u8 search depth
//! - i16 static eval of the hand-written evaluation for the side to move (version 2+; 0 in v1)
//! - 2 bytes reserved (0)

use crate::position::*;
use crate::search::MATE_BOUND;
use std::io::Write;

pub const MAGIC: &[u8; 4] = b"GGTD";
pub const VERSION: u16 = 2;
pub const HEADER_SIZE: usize = 32;
pub const TAIL_SIZE: usize = 24;

pub const FLAG_IN_CHECK: u8 = 1;
pub const FLAG_BEST_TACTICAL: u8 = 2;
pub const FLAG_RANDOM_MOVE: u8 = 4;
pub const FLAG_MATE_SCORE: u8 = 8;

pub const REASON_NONE: u8 = 0;
pub const REASON_CHECKMATE: u8 = 1;
pub const REASON_GOLD: u8 = 2;
pub const REASON_STALEMATE: u8 = 3;

pub fn record_size(board_size: usize) -> usize {
    board_size * board_size + TAIL_SIZE
}

pub fn write_header<W: Write>(w: &mut W, board_size: usize, rules: Rules, nodes: u64) -> std::io::Result<()> {
    let mut h = [0u8; HEADER_SIZE];
    h[0..4].copy_from_slice(MAGIC);
    h[4..6].copy_from_slice(&VERSION.to_le_bytes());
    h[6..8].copy_from_slice(&(board_size as u16).to_le_bytes());
    h[8..10].copy_from_slice(&(record_size(board_size) as u16).to_le_bytes());
    h[10] = rules.queen_drops;
    h[11] = rules.queen_cost.clamp(0, 255) as u8;
    h[12..16].copy_from_slice(&(nodes.min(u32::MAX as u64) as u32).to_le_bytes());
    w.write_all(&h)
}

/// Square content code: 0 empty, 1-6 white K..P, 7-12 black K..P, 13 treasure.
fn content_code(o: u8) -> u8 {
    if o == EMPTY {
        0
    } else if o == TREASURE {
        13
    } else {
        kind_of(o) + if color_of(o) == WHITE { 0 } else { 6 }
    }
}

/// One position with its search label; the game result is filled in when the game ends.
pub struct Record {
    pub bytes: Vec<u8>,
}

impl Record {
    /// `static_eval`: the hand-written evaluation of `pos` (no search), side to move's view.
    pub fn new(pos: &Position, score: i32, static_eval: i32, best: Option<Move>, depth: u32, game_id: u32, random_move: bool) -> Record {
        let n = pos.n();
        let mut b = vec![0u8; n + TAIL_SIZE];
        for sq in 0..n {
            let terrain = pos.terrain[sq] as u8 & 3;
            b[sq] = content_code(pos.occ[sq]) | (terrain << 4) | ((pos.mine_owner[sq] & 3) << 6);
        }
        let t = &mut b[n..];
        t[0] = pos.side as u8;
        let mut flags = 0;
        if pos.in_check() {
            flags |= FLAG_IN_CHECK;
        }
        if let Some(m) = best {
            if pos.is_tactical(m) {
                flags |= FLAG_BEST_TACTICAL;
            }
        }
        if random_move {
            flags |= FLAG_RANDOM_MOVE;
        }
        let score16: i16 = if score.abs() >= MATE_BOUND {
            flags |= FLAG_MATE_SCORE;
            if score > 0 { 32000 } else { -32000 }
        } else {
            score.clamp(-30000, 30000) as i16
        };
        t[1] = flags;
        t[2..4].copy_from_slice(&(pos.gold[WHITE].clamp(-32768, 32767) as i16).to_le_bytes());
        t[4..6].copy_from_slice(&(pos.gold[BLACK].clamp(-32768, 32767) as i16).to_le_bytes());
        t[6..8].copy_from_slice(&(pos.ply.min(65535) as u16).to_le_bytes());
        t[8..10].copy_from_slice(&score16.to_le_bytes());
        // t[10] result, t[11] reason: set by `set_result`
        t[12..16].copy_from_slice(&game_id.to_le_bytes());
        match best {
            Some(m) if m.is_placement() => {
                t[16] = 255;
                t[17] = m.to;
                t[18] = m.place;
            }
            Some(m) => {
                t[16] = m.from;
                t[17] = m.to;
                t[18] = 0;
            }
            None => {
                t[16] = 255;
                t[17] = 255;
                t[18] = 0;
            }
        }
        t[19] = depth.min(255) as u8;
        t[20..22].copy_from_slice(&(static_eval.clamp(-30000, 30000) as i16).to_le_bytes());
        Record { bytes: b }
    }

    /// Side to move of this record (0 white, 1 black).
    pub fn side(&self) -> usize {
        self.bytes[self.bytes.len() - TAIL_SIZE] as usize
    }

    /// `winner`: None for a draw / unfinished game.
    pub fn set_result(&mut self, winner: Option<usize>, reason: u8) {
        let side = self.side();
        let n = self.bytes.len() - TAIL_SIZE;
        self.bytes[n + 10] = match winner {
            None => 0,
            Some(w) if w == side => 1,
            Some(_) => (-1i8) as u8,
        };
        self.bytes[n + 11] = reason;
    }
}

/// Rebuild the position stored in a record (board, gold, side to move; current rules).
pub fn position_from_record(bytes: &[u8], board_size: usize) -> Position {
    let n = board_size * board_size;
    let mut pos = Position::empty(board_size);
    for sq in 0..n {
        let b = bytes[sq];
        let content = b & 15;
        pos.occ[sq] = match content {
            0 => EMPTY,
            13 => TREASURE,
            1..=6 => make_piece(content, WHITE),
            _ => make_piece(content - 6, BLACK),
        };
        pos.terrain[sq] = match (b >> 4) & 3 {
            1 => Terrain::Rocks,
            2 => Terrain::Forest,
            3 => Terrain::Mine,
            _ => Terrain::Open,
        };
        pos.mine_owner[sq] = b >> 6;
    }
    let t = &bytes[n..];
    pos.side = t[0] as usize;
    pos.gold = [i16::from_le_bytes([t[2], t[3]]) as i32, i16::from_le_bytes([t[4], t[5]]) as i32];
    pos.ply = u16::from_le_bytes([t[6], t[7]]) as u32;
    pos.refresh();
    pos
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn record_layout() {
        let mut pos = Position::empty(8);
        pos.occ[0] = make_piece(KING, WHITE);
        pos.occ[9] = make_piece(PAWN, BLACK);
        pos.occ[10] = TREASURE;
        pos.occ[63] = make_piece(KING, BLACK);
        pos.terrain[20] = Terrain::Mine;
        pos.mine_owner[20] = 2;
        pos.terrain[21] = Terrain::Rocks;
        pos.gold = [-3, 250];
        pos.side = BLACK;
        pos.refresh();
        let mut r = Record::new(&pos, -57, 31, Some(Move::placement(QUEEN, 17)), 9, 123456, true);
        r.set_result(Some(WHITE), REASON_GOLD);
        let b = &r.bytes;
        assert_eq!(b.len(), record_size(8));
        assert_eq!(b[0], 1); // white king
        assert_eq!(b[9], 6 + 6); // black pawn
        assert_eq!(b[10], 13); // treasure
        assert_eq!(b[63], 1 + 6); // black king
        assert_eq!(b[20], (3 << 4) | (2 << 6)); // mine owned by black
        assert_eq!(b[21], 1 << 4); // rocks
        let t = &b[64..];
        assert_eq!(t[0], 1);
        assert_eq!(t[1], FLAG_RANDOM_MOVE);
        assert_eq!(i16::from_le_bytes([t[2], t[3]]), -3);
        assert_eq!(i16::from_le_bytes([t[4], t[5]]), 250);
        assert_eq!(i16::from_le_bytes([t[8], t[9]]), -57);
        assert_eq!(t[10] as i8, -1); // black to move, white won
        assert_eq!(t[11], REASON_GOLD);
        assert_eq!(u32::from_le_bytes([t[12], t[13], t[14], t[15]]), 123456);
        assert_eq!((t[16], t[17], t[18], t[19]), (255, 17, QUEEN, 9));
        assert_eq!(i16::from_le_bytes([t[20], t[21]]), 31);

        // and back
        let p2 = position_from_record(b, 8);
        assert_eq!(p2.occ[..64], pos.occ[..64]);
        assert_eq!(p2.terrain[..64], pos.terrain[..64]);
        assert_eq!(p2.mine_owner[..64], pos.mine_owner[..64]);
        assert_eq!((p2.gold, p2.side, p2.hash), (pos.gold, pos.side, pos.hash));
    }
}
