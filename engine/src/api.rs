//! JSON API used by the browser (via `wasm.rs`). Positions use the client's square format:
//! `{ squares: [{x, y, type: "GrassMine", piece: {type: "WhiteKing", isWhite}, mineOwner}],
//!    whiteGold, blackGold, whiteIsActive }`.
//!
//! Requests (`cmd`): `search` {position, timeMs?, depth?, nodes?, multiPv?, nnue?}, `legal` {position},
//! `status` {position}, `newGame`. `nnue: true` evaluates with the network loaded through
//! `load_network` (`wasm.rs` export `load_nnue`) when the board size matches; otherwise, or
//! without a network, the hand-written eval. Search results say which was used (`nnue`).

use crate::eval::EvalParams;
use crate::movegen::legal_moves;
use crate::position::*;
use crate::search::{Limits, Searcher, MATE, MATE_BOUND};
use serde::Deserialize;
use serde_json::{json, Value};
use std::cell::RefCell;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientPiece {
    #[serde(rename = "type")]
    pub ty: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientSquare {
    pub x: usize,
    pub y: usize,
    #[serde(rename = "type", default)]
    pub ty: Option<String>,
    #[serde(default)]
    pub piece: Option<ClientPiece>,
    #[serde(default)]
    pub mine_owner: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientPosition {
    pub squares: Vec<ClientSquare>,
    /// Decimals (to 0.1) are allowed for analysis positions; see `Position::frac`.
    pub white_gold: f64,
    pub black_gold: f64,
    pub white_is_active: bool,
    /// Rules override, e.g. `{ queenDrops: "", queenCost: 72 }` for the classic rules; absent = current rules.
    #[serde(default)]
    pub rules: Option<ClientRules>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientRules {
    #[serde(default)]
    pub queen_drops: String,
    pub queen_cost: Option<i32>,
}

#[derive(Deserialize)]
#[serde(tag = "cmd", rename_all = "camelCase")]
enum Request {
    #[serde(rename_all = "camelCase")]
    Search {
        position: ClientPosition,
        time_ms: Option<f64>,
        depth: Option<u32>,
        nodes: Option<u64>,
        multi_pv: Option<usize>,
        #[serde(default)]
        nnue: bool,
    },
    Legal { position: ClientPosition },
    Status { position: ClientPosition },
    NewGame,
}

pub fn position_from_client(cp: &ClientPosition) -> Result<Position, String> {
    let n = cp.squares.len();
    let size = (n as f64).sqrt().round() as usize;
    if size * size != n || !(2..=16).contains(&size) {
        return Err(format!("unsupported board with {n} squares"));
    }
    let mut pos = Position::empty(size);
    for s in &cp.squares {
        if s.x >= size || s.y >= size {
            return Err("square out of range".into());
        }
        let id = s.y * size + s.x;
        let ty = s.ty.as_deref().unwrap_or("");
        pos.terrain[id] = if ty.contains("Rocks") {
            Terrain::Rocks
        } else if ty.contains("Trees") {
            Terrain::Forest
        } else if ty.contains("Mine") {
            Terrain::Mine
        } else {
            Terrain::Open
        };
        if pos.terrain[id] == Terrain::Mine {
            pos.mine_owner[id] = match s.mine_owner.as_deref() {
                Some("white") => 1,
                Some("black") => 2,
                _ => 0,
            };
        }
        if let Some(p) = &s.piece {
            pos.occ[id] = match p.ty.as_str() {
                "Treasure" => TREASURE,
                t => {
                    let color = if t.starts_with("White") { WHITE } else if t.starts_with("Black") { BLACK } else { continue };
                    let kind = match &t[5..] {
                        "King" => KING,
                        "Queen" => QUEEN,
                        "Rook" => ROOK,
                        "Bishop" => BISHOP,
                        "Knight" => KNIGHT,
                        "Pawn" => PAWN,
                        _ => continue, // e.g. flags: ignore
                    };
                    make_piece(kind, color)
                }
            };
        }
    }
    pos.set_gold_decimal(WHITE, cp.white_gold);
    pos.set_gold_decimal(BLACK, cp.black_gold);
    pos.side = if cp.white_is_active { WHITE } else { BLACK };
    if let Some(r) = &cp.rules {
        pos.rules = Rules { queen_drops: Rules::drops_mask(&r.queen_drops), queen_cost: r.queen_cost.unwrap_or(piece_cost(QUEEN)) };
    }
    let kings = |c| (0..n).filter(|&sq| pos.occ[sq] == make_piece(KING, c)).count();
    if kings(WHITE) != 1 || kings(BLACK) != 1 {
        return Err("each side needs exactly one king".into());
    }
    pos.refresh();
    Ok(pos)
}

fn kind_name(k: u8) -> &'static str {
    match k {
        QUEEN => "Queen",
        ROOK => "Rook",
        BISHOP => "Bishop",
        KNIGHT => "Knight",
        _ => "Pawn",
    }
}

pub fn move_json(pos: &Position, m: Move) -> Value {
    let size = pos.size();
    let xy = |s: u8| json!({ "x": s as usize % size, "y": s as usize / size });
    if m.is_placement() {
        json!({ "type": "p", "pieceType": kind_name(m.place), "toPos": xy(m.to), "notation": m.to_notation(size) })
    } else {
        json!({ "type": "m", "fromPos": xy(m.from), "toPos": xy(m.to), "notation": m.to_notation(size) })
    }
}

fn outcome_json(o: Outcome) -> Value {
    match o {
        Outcome::Ongoing => Value::Null,
        Outcome::Win { winner, reason } => json!({
            "winner": if winner == WHITE { "white" } else { "black" },
            "reason": match reason {
                WinReason::Checkmate => "checkmate",
                WinReason::Stalemate => "stalemate",
                WinReason::Gold => "gold",
            },
        }),
    }
}

/// Signed moves-to-mate for a White-perspective score (None if not a mate score).
fn mate_moves(white_score: i32) -> Option<i32> {
    if white_score.abs() >= MATE_BOUND {
        let moves = (MATE - white_score.abs() + 1) / 2;
        Some(if white_score > 0 { moves } else { -moves })
    } else {
        None
    }
}

thread_local! {
    static SEARCHER: RefCell<Option<Searcher>> = const { RefCell::new(None) };
    static NETWORK: RefCell<Option<std::sync::Arc<crate::nnue::Network>>> = const { RefCell::new(None) };
}

/// Load the evaluation network (a `.ggnn` file) for searches with `nnue: true`.
pub fn load_network(bytes: &[u8]) -> Result<(), String> {
    let net = crate::nnue::Network::from_bytes(bytes)?;
    NETWORK.with(|n| *n.borrow_mut() = Some(std::sync::Arc::new(net)));
    Ok(())
}

fn with_searcher<R>(f: impl FnOnce(&mut Searcher) -> R) -> R {
    SEARCHER.with(|s| {
        let mut s = s.borrow_mut();
        let s = s.get_or_insert_with(|| Searcher::new(EvalParams::default(), 32));
        f(s)
    })
}

/// Select the eval for a search on `pos`; returns whether the network is used. Switching evals
/// clears the transposition table (its scores belong to the other eval).
fn select_eval(s: &mut Searcher, pos: &Position, nnue: bool) -> bool {
    let net = if nnue { NETWORK.with(|n| n.borrow().clone()).filter(|n| n.board_size == pos.size()) } else { None };
    let use_net = net.is_some();
    if use_net != s.nnue.is_some() {
        s.clear();
    }
    s.nnue = net;
    use_net
}

/// Handle one JSON request; always returns a JSON object (with `error` on failure).
pub fn handle(input: &str) -> String {
    match handle_inner(input) {
        Ok(v) => v.to_string(),
        Err(e) => json!({ "error": e }).to_string(),
    }
}

fn handle_inner(input: &str) -> Result<Value, String> {
    let req: Request = serde_json::from_str(input).map_err(|e| e.to_string())?;
    match req {
        Request::NewGame => {
            with_searcher(|s| s.clear());
            Ok(json!({ "ok": true }))
        }
        Request::Legal { position } => {
            let mut pos = position_from_client(&position)?;
            let mut moves = Vec::new();
            legal_moves(&mut pos, &mut moves);
            let list: Vec<Value> = moves.iter().map(|&m| move_json(&pos, m)).collect();
            Ok(json!({ "moves": list, "inCheck": pos.in_check() }))
        }
        Request::Status { position } => {
            let mut pos = position_from_client(&position)?;
            let mut moves = Vec::new();
            legal_moves(&mut pos, &mut moves);
            Ok(json!({
                "result": outcome_json(pos.outcome(!moves.is_empty())),
                "inCheck": pos.in_check(),
                "goldThreshold": pos.gold_threshold(),
            }))
        }
        Request::Search { position, time_ms, depth, nodes, multi_pv: Some(n), nnue } if n > 1 => {
            let mut pos = position_from_client(&position)?;
            let limits = Limits {
                max_depth: depth,
                max_nodes: nodes,
                max_time_ms: time_ms.or(if depth.is_some() || nodes.is_some() { None } else { Some(1000.0) }),
            };
            let (lines, used_net) = with_searcher(|s| {
                let used = select_eval(s, &pos, nnue);
                (s.search_multi(&mut pos, limits, n), used)
            });
            let size = pos.size();
            let stm_white = pos.side == WHITE;
            let json_lines: Vec<Value> = lines
                .iter()
                .filter_map(|r| {
                    let m = r.best?;
                    let ws = if stm_white { r.score } else { -r.score };
                    Some(json!({
                        "move": move_json(&pos, m),
                        "score": ws as f64 / 10.0,
                        "mate": mate_moves(ws),
                        "depth": r.depth,
                        "pv": r.pv.iter().map(|m| m.to_notation(size)).collect::<Vec<_>>(),
                    }))
                })
                .collect();
            Ok(json!({
                "lines": json_lines,
                "depth": lines.iter().map(|r| r.depth).min().unwrap_or(0),
                "nodes": lines.iter().map(|r| r.nodes).sum::<u64>(),
                "nnue": used_net,
            }))
        }
        Request::Search { position, time_ms, depth, nodes, nnue, .. } => {
            let mut pos = position_from_client(&position)?;
            let limits = Limits {
                max_depth: depth,
                max_nodes: nodes,
                max_time_ms: time_ms.or(if depth.is_some() || nodes.is_some() { None } else { Some(1000.0) }),
            };
            let (r, used_net) = with_searcher(|s| {
                let used = select_eval(s, &pos, nnue);
                (s.search(&mut pos, limits), used)
            });
            let Some(best) = r.best else {
                return Ok(json!({ "best": null, "nnue": used_net }));
            };
            let white_score = if pos.side == WHITE { r.score } else { -r.score };
            let mate = mate_moves(white_score);
            let size = pos.size();
            Ok(json!({
                "best": move_json(&pos, best),
                "score": white_score as f64 / 10.0,
                "mate": mate,
                "depth": r.depth,
                "nodes": r.nodes,
                "timeMs": r.time_ms,
                "pv": r.pv.iter().map(|m| m.to_notation(size)).collect::<Vec<_>>(),
                "nnue": used_net,
            }))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn client_json_from_seed(seed: &str) -> String {
        let pos = crate::seed::position_from_seed(seed).unwrap();
        let size = pos.size();
        let squares: Vec<Value> = (0..pos.n())
            .map(|sq| {
                let ty = match pos.terrain[sq] {
                    Terrain::Open => "Grass",
                    Terrain::Rocks => "GrassRocks",
                    Terrain::Forest => "DirtTrees",
                    Terrain::Mine => "GrassMine",
                };
                let o = pos.occ[sq];
                let piece = if o == TREASURE {
                    json!({ "type": "Treasure", "isWhite": false })
                } else if is_piece(o) {
                    json!({ "type": if color_of(o) == WHITE { "WhiteKing" } else { "BlackKing" }, "isWhite": color_of(o) == WHITE })
                } else {
                    Value::Null
                };
                json!({ "x": sq % size, "y": sq / size, "type": ty, "piece": piece, "mineOwner": null })
            })
            .collect();
        json!({ "squares": squares, "whiteGold": 0, "blackGold": 5, "whiteIsActive": true }).to_string()
    }

    #[test]
    fn search_and_legal_roundtrip() {
        let p = client_json_from_seed("m_10x10_r1kt2f1t2m92m8fmr7f4r2");
        let out: Value = serde_json::from_str(&handle(&format!(r#"{{"cmd":"search","position":{p},"timeMs":50}}"#))).unwrap();
        assert!(out["best"]["toPos"].is_object(), "{out}");
        let legal: Value = serde_json::from_str(&handle(&format!(r#"{{"cmd":"legal","position":{p}}}"#))).unwrap();
        assert!(!legal["moves"].as_array().unwrap().is_empty());
        let status: Value = serde_json::from_str(&handle(&format!(r#"{{"cmd":"status","position":{p}}}"#))).unwrap();
        assert!(status["result"].is_null());
        let multi: Value = serde_json::from_str(&handle(&format!(r#"{{"cmd":"search","position":{p},"depth":5,"multiPv":4}}"#))).unwrap();
        let lines = multi["lines"].as_array().unwrap();
        assert_eq!(lines.len(), 4, "{multi}");
        let notations: std::collections::HashSet<_> = lines.iter().map(|l| l["move"]["notation"].as_str().unwrap()).collect();
        assert_eq!(notations.len(), 4);
        let bad: Value = serde_json::from_str(&handle(r#"{"cmd":"legal","position":{"squares":[],"whiteGold":0,"blackGold":0,"whiteIsActive":true}}"#)).unwrap();
        assert!(bad["error"].is_string());
    }
}
