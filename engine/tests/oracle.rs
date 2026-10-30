//! Replays games recorded by `tools/RulesOracle` (the server's real C# GameLogic) and checks
//! that the Rust engine produces identical legal-move sets, gold and game results.
//! Regenerate data: `dotnet tools/RulesOracle/bin/Release/net9.0/RulesOracle.dll 60 7 engine/testdata/oracle.jsonl`

use goldrush_engine::movegen::legal_moves;
use goldrush_engine::seed::position_from_seed;
use goldrush_engine::*;
use serde::Deserialize;

#[derive(Deserialize)]
struct Ply {
    gold: [i32; 2],
    legal: Vec<String>,
    #[serde(rename = "move")]
    mv: Option<String>,
}

#[derive(Deserialize)]
struct Game {
    seed: String,
    /// Current rules (placements next to queens, queen 75). Absent/false in data recorded before
    /// 2026-10-26, which is replayed with `Rules::classic()`.
    #[serde(default, rename = "queenDrops")]
    queen_drops: bool,
    plies: Vec<Ply>,
    result: Option<String>,
}

fn code(m: Move) -> String {
    if m.is_placement() {
        let c = match m.place {
            QUEEN => 'q',
            ROOK => 'r',
            BISHOP => 'b',
            KNIGHT => 'n',
            _ => 'p',
        };
        format!("p{c}{}", m.to)
    } else {
        format!("m{}-{}", m.from, m.to)
    }
}

fn result_code(o: Outcome) -> Option<String> {
    match o {
        Outcome::Ongoing => None,
        Outcome::Win { winner, reason } => Some(format!(
            "{}+{}",
            if winner == WHITE { 'w' } else { 'b' },
            match reason {
                WinReason::Checkmate => 'c',
                WinReason::Stalemate => 's',
                WinReason::Gold => 'g',
            }
        )),
    }
}

#[test]
fn matches_csharp_rules() {
    // Every *.jsonl in testdata/ (oracle.jsonl is committed; bigger local sets are gitignored).
    let dir = concat!(env!("CARGO_MANIFEST_DIR"), "/testdata");
    let mut data = String::new();
    for e in std::fs::read_dir(dir).unwrap() {
        let p = e.unwrap().path();
        let name = p.file_name().unwrap().to_string_lossy().to_string();
        if name.starts_with("oracle") && name.ends_with(".jsonl") {
            data.push_str(&std::fs::read_to_string(&p).unwrap());
        }
    }
    let mut positions = 0;
    let mut default_start = 0;
    let mut failures = Vec::new();
    for (gi, line) in data.lines().enumerate() {
        let game: Game = serde_json::from_str(line).unwrap();
        let mut pos = position_from_seed(&game.seed).unwrap();
        if !game.queen_drops {
            pos.rules = Rules::classic();
        }
        // Starting gold as recorded: Black started with 6 before 2026-10-30, 5 since (older files
        // keep 6). The default itself is compared on freshly recorded data: see the count below.
        let default_gold = pos.gold;
        if let Some(first) = game.plies.first() {
            pos.gold = first.gold;
            if first.gold == default_gold {
                default_start += 1;
            }
        }
        let mut moves = Vec::new();
        let mut outcome = Outcome::Ongoing;
        for (pi, ply) in game.plies.iter().enumerate() {
            positions += 1;
            legal_moves(&mut pos, &mut moves);
            let mut ours: Vec<String> = moves.iter().map(|&m| code(m)).collect();
            ours.sort();
            if pos.gold != ply.gold || ours != ply.legal {
                let missing: Vec<_> = ply.legal.iter().filter(|c| !ours.contains(c)).collect();
                let extra: Vec<_> = ours.iter().filter(|c| !ply.legal.contains(c)).collect();
                failures.push(format!(
                    "game {gi} ({}) ply {pi}: gold ours {:?} theirs {:?}; missing {:?}; extra {:?}\n{}",
                    game.seed, pos.gold, ply.gold, missing, extra, pos.dump()
                ));
                break;
            }
            let Some(mv) = &ply.mv else { break };
            let m = *moves.iter().find(|&&m| code(m) == *mv).unwrap();
            pos.make(m);
            legal_moves(&mut pos, &mut moves);
            outcome = pos.outcome(!moves.is_empty());
            if outcome != Outcome::Ongoing && pi + 1 != game.plies.len() {
                failures.push(format!("game {gi} ({}) ply {pi}: we ended early with {:?}", game.seed, outcome));
                break;
            }
        }
        if failures.is_empty() || !failures.last().unwrap().starts_with(&format!("game {gi} ")) {
            if result_code(outcome) != game.result {
                failures.push(format!(
                    "game {gi} ({}): result ours {:?} theirs {:?}",
                    game.seed,
                    result_code(outcome),
                    game.result
                ));
            }
        }
    }
    println!("checked {positions} positions; {default_start} games start with the engine's default gold");
    // oracle.jsonl (committed) is regenerated after rule changes, so it must use today's default
    let fresh = std::fs::read_to_string(format!("{dir}/oracle.jsonl")).unwrap();
    for line in fresh.lines() {
        let game: Game = serde_json::from_str(line).unwrap();
        let start = position_from_seed(&game.seed).unwrap().gold;
        assert_eq!(game.plies[0].gold, start, "oracle.jsonl: C# starting gold differs from the engine default");
    }
    assert!(failures.is_empty(), "{} mismatches:\n{}", failures.len(), failures.join("\n"));
}
