//! Map seeds, identical to `MapGenerator.GenerateFromSeed` / `Map.SetSeed` in C#.
//!
//! Format: `{m|r}_{W}x{H}_{body}`. Body chars: digit = skip that many empty squares,
//! `k` king (black if in the top half), `t` treasure, `m` mine, `r` rocks, `f` forest.
//! Mirrored seeds only describe the first half; square `i` is copied to `n-1-i` for
//! `i < n/2 - 1` (the C# loop skips square `n/2 - 1`, replicated here).

use crate::position::*;

pub fn position_from_seed(seed: &str) -> Result<Position, String> {
    let parts: Vec<&str> = seed.split('_').collect();
    if parts.len() != 3 {
        return Err(format!("bad seed: {seed}"));
    }
    let mirrored = parts[0] == "m";
    let wh: Vec<&str> = parts[1].split('x').collect();
    let w: usize = wh[0].parse().map_err(|_| "bad width")?;
    let h: usize = wh.get(1).ok_or("bad size")?.parse().map_err(|_| "bad height")?;
    if w != h {
        return Err("only square boards are supported".into());
    }
    let n = w * h;
    let mut pos = Position::empty(w);
    let mut id = 0usize;
    for c in parts[2].chars() {
        if let Some(d) = c.to_digit(10) {
            id += d as usize;
            continue;
        }
        if id >= n {
            return Err("seed overflows board".into());
        }
        match c {
            'k' => pos.occ[id] = make_piece(KING, if id >= n / 2 { BLACK } else { WHITE }),
            't' => pos.occ[id] = TREASURE,
            'm' => pos.terrain[id] = Terrain::Mine,
            'r' => pos.terrain[id] = Terrain::Rocks,
            'f' => pos.terrain[id] = Terrain::Forest,
            _ => return Err(format!("bad seed char {c}")),
        }
        id += 1;
    }
    if mirrored {
        for i in 0..(n / 2).saturating_sub(1) {
            let j = n - 1 - i;
            pos.terrain[j] = pos.terrain[i];
            match pos.occ[i] {
                TREASURE => pos.occ[j] = TREASURE,
                o if is_piece(o) && kind_of(o) == KING => pos.occ[j] = make_piece(KING, BLACK),
                _ => {}
            }
        }
    }
    pos.refresh();
    if pos.king[WHITE] == usize::MAX || pos.king[BLACK] == usize::MAX {
        return Err("seed lacks a king".into());
    }
    Ok(pos)
}
