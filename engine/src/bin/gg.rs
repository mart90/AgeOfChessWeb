//! Goldrush Gambit engine CLI.
//!
//!   gg bench    [--seeds F] [--positions N] [--depth D]
//!   gg selfplay --seeds F [--games N] [--threads T] [--time MS | --nodes N | --depth D]
//!               [--params P.json] [--out games.jsonl] [--max-plies P] [--verbose]
//!   gg match    --a A.json --b B.json --seeds F [--games N] [--threads T] [--time MS|--nodes N|--depth D]
//!               (each seed is played twice with colours swapped; "default" = built-in params;
//!                per-engine limits: --a-nodes/--b-nodes, --a-time/--b-time, --a-depth/--b-depth;
//!                weak play: --a-mistake RATE,MAXLOSS / --b-mistake = second-best move with probability
//!                RATE when it is at most MAXLOSS gold worse)
//!               [--out games.jsonl]  (every game, with "a_is_white"; scores are the mover's search)
//!   gg play     --seed S [--time MS ...]   (one verbose game to stdout)
//!   gg bidmatch --seeds F --nodes N [--bid-budget B (default 30·N)] [--fixed-bid 6] [--games N] [--threads T]
//!               [--bid-shares 0.3,0.25,0.2,0.15,0.1] [--bid-scales-m 1.5,1.25,1,0.75,0.5] [--bid-scales-r 5,4,3,2,1] [--skip-bids LO,HI]
//!               [--opening-nodes N --opening-plies P]  (both sides play the first P plies with N nodes)
//!               [--bid-params P.json]  (eval params for the bidder's analysis only)
//!               (A bids with goldrush_engine::bid, B always bids the fixed amount; both play with N nodes)
//!   gg gendata  --seeds F --out FILE [--seed-offset N] [--games N] [--threads T] [--nodes N] [--random-plies K]
//!               [--random-multipv M] [--random-margin GOLD] [--random-nodes N] [--first-game-id I]
//!               [--rng-seed S] [--overwrite] [--nnue NET]  (training data for the eval network, see docs/training.md)
//!   gg nncheck  --net NET.ggnn --check CHECK.bin  (engine network vs PyTorch outputs, eval speed)
//!   Networks: --nnue NET.ggnn (bench/selfplay/play), --a-nnue/--b-nnue (match): use the evaluation
//!             network instead of the hand-written eval (training/export.py writes NET.ggnn)
//!   Common: --black-gold G  (Black's starting gold, default 6)
//!           --classic  (rules before 2026-10-26: no placements next to queens, queen 72)
//!           --queen-drops LETTERS [--queen-cost C]  (custom: pieces that may be placed next to a
//!           queen, e.g. "rbn" or ""; see position::Rules)

use goldrush_engine::eval::EvalParams;
use goldrush_engine::movegen::{legal_moves, perft};
use goldrush_engine::search::{Limits, Searcher, MATE_BOUND};
use goldrush_engine::seed::position_from_seed;
use goldrush_engine::*;
use std::collections::HashMap;
use std::io::Write;
use std::sync::atomic::{AtomicI32, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;

/// Black's starting gold (`--black-gold`, default 6 like the server).
static BLACK_GOLD: AtomicI32 = AtomicI32::new(6);
/// Rule variant for every game (`--queen-drops`, `--queen-cost`).
static RULES: std::sync::OnceLock<Rules> = std::sync::OnceLock::new();

fn start_position(seed: &str) -> Position {
    let mut pos = position_from_seed(seed).expect("seed");
    pos.rules = *RULES.get().unwrap_or(&Rules::default());
    pos
}

struct Args(HashMap<String, String>);

impl Args {
    fn parse(v: &[String]) -> Self {
        let mut m = HashMap::new();
        let mut i = 0;
        while i < v.len() {
            if let Some(k) = v[i].strip_prefix("--") {
                if i + 1 < v.len() && !v[i + 1].starts_with("--") {
                    m.insert(k.to_string(), v[i + 1].clone());
                    i += 2;
                } else {
                    m.insert(k.to_string(), "1".into());
                    i += 1;
                }
            } else {
                i += 1;
            }
        }
        Args(m)
    }
    fn get(&self, k: &str) -> Option<&str> {
        self.0.get(k).map(|s| s.as_str())
    }
    fn num<T: std::str::FromStr>(&self, k: &str, default: T) -> T {
        self.get(k).and_then(|s| s.parse().ok()).unwrap_or(default)
    }
    fn limits(&self) -> Limits {
        self.limits_for("")
    }
    /// Limits for one engine in a match: `--a-nodes`, `--b-time`, ... override `--nodes`, `--time`, ...
    fn limits_for(&self, prefix: &str) -> Limits {
        let get = |k: &str| {
            let specific = if prefix.is_empty() { None } else { self.get(&format!("{prefix}-{k}")) };
            specific.or(self.get(k)).and_then(|s| s.parse().ok())
        };
        Limits { max_depth: get("depth").map(|d: f64| d as u32), max_nodes: get("nodes").map(|n: f64| n as u64), max_time_ms: get("time") }
    }
}

fn load_params(path: Option<&str>) -> EvalParams {
    match path {
        None | Some("default") => EvalParams::default(),
        Some(p) => serde_json::from_str(&std::fs::read_to_string(p).expect("params file")).expect("params json"),
    }
}

fn load_seeds(path: &str) -> Vec<String> {
    std::fs::read_to_string(path)
        .expect("seeds file")
        .lines()
        .map(|l| l.trim().to_string())
        .filter(|l| !l.is_empty())
        .collect()
}

fn result_str(o: Outcome) -> String {
    match o {
        Outcome::Ongoing => "draw".into(),
        Outcome::Win { winner, reason } => format!(
            "{}+{}",
            if winner == WHITE { "w" } else { "b" },
            match reason {
                WinReason::Checkmate => "c",
                WinReason::Stalemate => "s",
                WinReason::Gold => "g",
            }
        ),
    }
}

#[derive(serde::Serialize)]
struct GameRecord {
    seed: String,
    moves: Vec<String>,
    /// Engine score after each move from White's perspective (gold*10 units; ±1e6 = mate).
    scores: Vec<i32>,
    depths: Vec<u32>,
    result: String,
    plies: usize,
    pieces_bought: [u32; 2],
    /// Placements per side by kind, in shop order: Q, R, B, N, P.
    bought_by_kind: [[u32; 5]; 2],
    /// Non-pawn placements that were not next to the own king (only possible via a queen).
    queen_drops: [u32; 2],
    /// Moves/placements that gave check.
    checks: [u32; 2],
    /// Pieces captured (treasures not counted), and queens lost.
    captures: [u32; 2],
    queens_lost: [u32; 2],
    /// Ply of each side's first queen placement (0 = never).
    first_queen_ply: [u32; 2],
    /// Gold and owned mines at the end.
    final_gold: [i32; 2],
    final_mines: [i32; 2],
}

struct GameStats {
    nodes: u64,
    ms: f64,
    depth_sum: u64,
}

/// Weak-level behaviour: with probability `rate`, play the second-best move if it is at most
/// `max_loss` gold worse than the best (mirrors client/src/GamePage.svelte).
#[derive(Clone, Copy, Debug)]
struct Mistake {
    rate: f64,
    max_loss: f64,
}

fn parse_mistake(a: &Args, key: &str) -> Option<Mistake> {
    a.get(key).map(|v| {
        let p: Vec<f64> = v.split(',').map(|x| x.trim().parse().expect("--x-mistake RATE,MAXLOSS")).collect();
        Mistake { rate: p[0], max_loss: p[1] }
    })
}

/// xorshift64 → [0, 1)
fn rng_next(s: &mut u64) -> f64 {
    *s ^= *s << 13;
    *s ^= *s >> 7;
    *s ^= *s << 17;
    (*s >> 11) as f64 / (1u64 << 53) as f64
}

/// Play one game. `engines[c]` plays colour c.
fn play_game(
    seed: &str,
    engines: [&mut Searcher; 2],
    limits: [Limits; 2],
    max_plies: usize,
    verbose: bool,
    black_gold: Option<i32>,
    // (plies, limits): both sides use these limits for the first `plies` plies
    opening: Option<(usize, Limits)>,
    // per colour: deliberately play the second-best move sometimes (weak computer levels)
    mistakes: [Option<Mistake>; 2],
    rng_seed: u64,
) -> (GameRecord, GameStats) {
    let mut rng = rng_seed | 1;
    let mut pos = start_position(seed);
    pos.gold[BLACK] = black_gold.unwrap_or(BLACK_GOLD.load(Ordering::Relaxed));
    let size = pos.size();
    let mut rec = GameRecord {
        seed: seed.to_string(),
        moves: vec![],
        scores: vec![],
        depths: vec![],
        result: "draw".into(),
        plies: 0,
        pieces_bought: [0, 0],
        bought_by_kind: [[0; 5]; 2],
        queen_drops: [0, 0],
        checks: [0, 0],
        captures: [0, 0],
        queens_lost: [0, 0],
        first_queen_ply: [0, 0],
        final_gold: [0, 0],
        final_mines: [0, 0],
    };
    let mut stats = GameStats { nodes: 0, ms: 0.0, depth_sum: 0 };
    let [e0, e1] = engines;
    let engines = [e0, e1];
    let mut moves = Vec::new();
    for _ in 0..max_plies {
        let stm = pos.side;
        let lim = match opening {
            Some((plies, l)) if (pos.ply as usize) < plies => l,
            _ => limits[stm],
        };
        let r = match mistakes[stm] {
            Some(mk) => {
                let lines = engines[stm].search_multi(&mut pos, lim, 2);
                if lines.is_empty() {
                    break;
                }
                let second_ok = lines.len() > 1 && ((lines[0].score - lines[1].score) as f64) <= mk.max_loss * 10.0;
                let pick = if second_ok && rng_next(&mut rng) < mk.rate { 1 } else { 0 };
                let mut r = lines[pick].clone();
                r.nodes = lines.iter().map(|l| l.nodes).sum();
                r
            }
            None => engines[stm].search(&mut pos, lim),
        };
        let Some(m) = r.best else { break };
        stats.nodes += r.nodes;
        stats.ms += r.time_ms;
        stats.depth_sum += r.depth as u64;
        let white_score = if stm == WHITE { r.score } else { -r.score };
        if verbose {
            let sc = if r.score.abs() >= MATE_BOUND { format!("mate{}", if r.score > 0 { "+" } else { "-" }) } else { format!("{:.1}", r.score as f64 / 10.0) };
            println!(
                "{:>3}. {} {:<8} score {:>7} d{:<2} {:>8} nodes {:>6.0}ms  gold W{} B{}",
                pos.ply + 1,
                if stm == WHITE { "W" } else { "B" },
                m.to_notation(size),
                sc,
                r.depth,
                r.nodes,
                r.time_ms,
                pos.gold[0],
                pos.gold[1]
            );
        }
        let to = m.to as usize;
        if m.is_placement() {
            rec.pieces_bought[stm] += 1;
            if let Some(i) = SHOP.iter().position(|&(k, _)| k == m.place) {
                rec.bought_by_kind[stm][i] += 1;
            }
            let (kx, ky, tx, ty) = (pos.king[stm] % size, pos.king[stm] / size, to % size, to / size);
            if m.place != PAWN && kx.abs_diff(tx).max(ky.abs_diff(ty)) > 1 {
                rec.queen_drops[stm] += 1;
            }
            if m.place == QUEEN && rec.first_queen_ply[stm] == 0 {
                rec.first_queen_ply[stm] = pos.ply + 1;
            }
        } else {
            let cap = pos.occ[to];
            if cap != EMPTY && cap != TREASURE {
                rec.captures[stm] += 1;
                if kind_of(cap) == QUEEN {
                    rec.queens_lost[stm ^ 1] += 1;
                }
            }
        }
        rec.moves.push(m.to_notation(size));
        rec.scores.push(white_score);
        rec.depths.push(r.depth);
        pos.make(m);
        if pos.in_check() {
            rec.checks[stm] += 1;
        }
        legal_moves(&mut pos, &mut moves);
        let o = pos.outcome(!moves.is_empty());
        if o != Outcome::Ongoing {
            rec.result = result_str(o);
            break;
        }
    }
    if verbose {
        println!("{}", pos.dump());
        println!("result: {}", rec.result);
    }
    rec.plies = rec.moves.len();
    rec.final_gold = pos.gold;
    rec.final_mines = pos.mines;
    (rec, stats)
}

fn bench(a: &Args) {
    let seeds = load_seeds(a.get("seeds").unwrap_or("testdata/seeds_m10.txt"));
    let n = a.num("positions", 8usize);
    let depth: u32 = a.num("depth", 6);
    // Build midgame positions by letting a fast engine play some plies.
    let mut quick = Searcher::new(EvalParams::default(), 16);
    let mut positions = Vec::new();
    for seed in seeds.iter().take(n) {
        let mut pos = start_position(seed);
        for _ in 0..30 {
            let r = quick.search(&mut pos, Limits { max_depth: Some(2), ..Default::default() });
            match r.best {
                Some(m) => {
                    pos.make(m);
                }
                None => break,
            }
        }
        positions.push(pos);
    }

    let t = Instant::now();
    let mut leaves = 0;
    for p in positions.iter_mut() {
        leaves += perft(p, 3);
    }
    let secs = t.elapsed().as_secs_f64();
    println!("perft(3) over {} positions: {} leaves in {:.2}s = {:.2} M leaves/s", positions.len(), leaves, secs, leaves as f64 / secs / 1e6);

    let mut s = Searcher::new(EvalParams::default(), 64);
    s.nnue = load_nnue(a, "");
    let t = Instant::now();
    let mut nodes = 0;
    for p in positions.iter_mut() {
        s.clear();
        let r = s.search(p, Limits { max_depth: Some(depth), ..Default::default() });
        nodes += r.nodes;
        println!("  depth {} score {:>7} nodes {:>9} {:>7.0}ms best {}", r.depth, r.score, r.nodes, r.time_ms, r.best.map(|m| m.to_notation(p.size())).unwrap_or_default());
    }
    let secs = t.elapsed().as_secs_f64();
    println!("search depth {depth}: {nodes} nodes in {:.2}s = {:.0} k nodes/s", secs, nodes as f64 / secs / 1e3);
}

fn selfplay(a: &Args) {
    let seeds = load_seeds(a.get("seeds").expect("--seeds"));
    let games = a.num("games", seeds.len()).min(seeds.len());
    let threads = a.num("threads", std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4));
    let limits = a.limits();
    let max_plies = a.num("max-plies", 400usize);
    let params = load_params(a.get("params"));
    let verbose = a.get("verbose").is_some();
    let out: Arc<Mutex<Box<dyn Write + Send>>> = Arc::new(Mutex::new(match a.get("out") {
        Some(p) => Box::new(std::io::BufWriter::new(std::fs::File::create(p).unwrap())),
        None => Box::new(std::io::sink()),
    }));
    let next = Arc::new(AtomicUsize::new(0));
    let seeds = Arc::new(seeds);
    let tally = Arc::new(Mutex::new((HashMap::<String, u32>::new(), 0usize, 0u64, 0f64, 0u64, [0u32; 2])));
    let net = load_nnue(a, "");
    let start = Instant::now();

    let handles: Vec<_> = (0..threads)
        .map(|_| {
            let (next, seeds, out, tally, params, net) = (next.clone(), seeds.clone(), out.clone(), tally.clone(), params.clone(), net.clone());
            std::thread::spawn(move || {
                let mut w = Searcher::new(params.clone(), 32);
                let mut b = Searcher::new(params, 32);
                w.nnue = net.clone();
                b.nnue = net;
                loop {
                    let i = next.fetch_add(1, Ordering::SeqCst);
                    if i >= games {
                        break;
                    }
                    w.clear();
                    b.clear();
                    let (rec, st) = play_game(&seeds[i], [&mut w, &mut b], [limits; 2], max_plies, verbose, None, None, [None; 2], 0);
                    {
                        let mut o = out.lock().unwrap();
                        writeln!(o, "{}", serde_json::to_string(&rec).unwrap()).unwrap();
                    }
                    let mut t = tally.lock().unwrap();
                    *t.0.entry(rec.result.clone()).or_default() += 1;
                    t.1 += rec.plies;
                    t.2 += st.nodes;
                    t.3 += st.ms;
                    t.4 += st.depth_sum;
                    t.5[0] += rec.pieces_bought[0];
                    t.5[1] += rec.pieces_bought[1];
                    let done: u32 = t.0.values().sum();
                    eprintln!(
                        "[{done}/{games}] {} in {} plies, bought W{} B{}  ({:.0}s elapsed)",
                        rec.result,
                        rec.plies,
                        rec.pieces_bought[0],
                        rec.pieces_bought[1],
                        start.elapsed().as_secs_f64()
                    );
                }
            })
        })
        .collect();
    for h in handles {
        h.join().unwrap();
    }
    out.lock().unwrap().flush().unwrap();
    let t = tally.lock().unwrap();
    let secs = start.elapsed().as_secs_f64();
    let mut res: Vec<_> = t.0.iter().collect();
    res.sort();
    println!("games: {games}  threads: {threads}  wall: {secs:.1}s  => {:.0} games/hour", games as f64 / secs * 3600.0);
    println!("results: {:?}", res);
    println!(
        "avg plies {:.1}  avg depth {:.2}  avg {:.0} k nodes/s per thread  pieces bought/game {:.1}",
        t.1 as f64 / games as f64,
        t.4 as f64 / t.1.max(1) as f64,
        t.2 as f64 / (t.3 / 1000.0).max(1e-9) / 1e3,
        (t.5[0] + t.5[1]) as f64 / games as f64
    );
}

fn run_match(a: &Args) {
    let seeds = load_seeds(a.get("seeds").expect("--seeds"));
    let games = a.num("games", 100usize).min(seeds.len());
    let threads = a.num("threads", std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4));
    let (la, lb) = (a.limits_for("a"), a.limits_for("b"));
    let (ma, mb) = (parse_mistake(a, "a-mistake"), parse_mistake(a, "b-mistake"));
    if ma.is_some() || mb.is_some() {
        eprintln!("mistakes: A {:?}, B {:?}", ma, mb);
    }
    let max_plies = a.num("max-plies", 400usize);
    let pa = load_params(a.get("a"));
    let pb = load_params(a.get("b"));
    let (na, nb) = (load_nnue(a, "a"), load_nnue(a, "b"));
    let out: Arc<Mutex<Box<dyn Write + Send>>> = Arc::new(Mutex::new(match a.get("out") {
        Some(p) => Box::new(std::io::BufWriter::new(std::fs::File::create(p).unwrap())),
        None => Box::new(std::io::sink()),
    }));
    let next = Arc::new(AtomicUsize::new(0));
    let seeds = Arc::new(seeds);
    // (a wins, b wins, draws, reasons)
    let tally = Arc::new(Mutex::new((0u32, 0u32, 0u32, HashMap::<String, u32>::new())));
    let start = Instant::now();
    let handles: Vec<_> = (0..threads)
        .map(|_| {
            let (next, seeds, tally, pa, pb, na, nb, out) =
                (next.clone(), seeds.clone(), tally.clone(), pa.clone(), pb.clone(), na.clone(), nb.clone(), out.clone());
            std::thread::spawn(move || {
                let mut ea = Searcher::new(pa, 32);
                let mut eb = Searcher::new(pb, 32);
                ea.nnue = na;
                eb.nnue = nb;
                loop {
                    let i = next.fetch_add(1, Ordering::SeqCst);
                    if i >= games * 2 {
                        break;
                    }
                    let seed = &seeds[i / 2];
                    let a_is_white = i % 2 == 0;
                    ea.clear();
                    eb.clear();
                    let (rec, _) = if a_is_white {
                        play_game(seed, [&mut ea, &mut eb], [la, lb], max_plies, false, None, None, [ma, mb], i as u64 * 7919 + 1)
                    } else {
                        play_game(seed, [&mut eb, &mut ea], [lb, la], max_plies, false, None, None, [mb, ma], i as u64 * 7919 + 1)
                    };
                    {
                        let mut v = serde_json::to_value(&rec).unwrap();
                        v["a_is_white"] = serde_json::Value::Bool(a_is_white);
                        v["game"] = serde_json::Value::from(i);
                        writeln!(out.lock().unwrap(), "{v}").unwrap();
                    }
                    let mut t = tally.lock().unwrap();
                    *t.3.entry(rec.result.clone()).or_default() += 1;
                    match rec.result.chars().next() {
                        Some('w') if a_is_white => t.0 += 1,
                        Some('b') if !a_is_white => t.0 += 1,
                        Some('w') | Some('b') => t.1 += 1,
                        _ => t.2 += 1,
                    }
                    let n = t.0 + t.1 + t.2;
                    let score = (t.0 as f64 + 0.5 * t.2 as f64) / n as f64;
                    eprintln!("[{n}/{}] A {} - B {} - D {}  score {:.3}  elo {:+.0}", games * 2, t.0, t.1, t.2, score, elo(score));
                }
            })
        })
        .collect();
    for h in handles {
        h.join().unwrap();
    }
    let t = tally.lock().unwrap();
    let n = t.0 + t.1 + t.2;
    let score = (t.0 as f64 + 0.5 * t.2 as f64) / n as f64;
    let mut res: Vec<_> = t.3.iter().collect();
    res.sort();
    println!(
        "A {} - B {} - D {}  score {:.3}  elo {:+.0} ± {:.0}  ({:.0}s)",
        t.0,
        t.1,
        t.2,
        score,
        elo(score),
        elo_margin(t.0, t.1, t.2),
        start.elapsed().as_secs_f64()
    );
    println!("results: {:?}", res);
}

/// Bidding engine (A) vs a fixed bidder (B). Each seed is played twice, alternating which side
/// is the "creator" (wins ties, like the server). Higher bid plays White; Black starts with the
/// winning bid. Both engines play the game itself with the same node limit.
fn run_bidmatch(a: &Args) {
    use goldrush_engine::bid::{computer_bid_with, SCALES_FULL_RANDOM, SCALES_MIRRORED, SHARES};
    let list = |k: &str, default: &[f64]| -> Vec<f64> {
        a.get(k).map(|s| s.split(',').map(|x| x.trim().parse().expect("number list")).collect()).unwrap_or_else(|| default.to_vec())
    };
    let shares = Arc::new(list("bid-shares", &SHARES));
    let scales_m = Arc::new(list("bid-scales-m", &SCALES_MIRRORED));
    let scales_r = Arc::new(list("bid-scales-r", &SCALES_FULL_RANDOM));
    assert!(shares.len() == scales_m.len() && shares.len() == scales_r.len(), "shares/scales length mismatch");
    eprintln!("bid passes: shares {:?}, scales mirrored {:?}, full random {:?}", shares, scales_m, scales_r);
    let seeds = load_seeds(a.get("seeds").expect("--seeds"));
    let games = a.num("games", 200usize).min(seeds.len());
    let threads = a.num("threads", std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4));
    let nodes: u64 = a.num("nodes", 12_000);
    let budget: u64 = a.num("bid-budget", nodes * goldrush_engine::bid::BID_BUDGET_FACTOR);
    let fixed: i32 = a.num("fixed-bid", 6);
    // --skip-bids LO,HI: ignore boards where the bidding engine bids within [LO, HI]
    let skip: Option<(i32, i32)> = a.get("skip-bids").map(|s| {
        let v: Vec<i32> = s.split(',').map(|x| x.trim().parse().expect("--skip-bids LO,HI")).collect();
        (v[0], v[1])
    });
    let skipped = Arc::new(AtomicUsize::new(0));
    let max_plies = a.num("max-plies", 400usize);
    let limits = Limits { max_nodes: Some(nodes), ..Default::default() };
    // --opening-nodes N --opening-plies P: both sides play the first P plies with N nodes
    let opening = a.get("opening-nodes").map(|n| {
        (a.num("opening-plies", 40usize), Limits { max_nodes: Some(n.parse().expect("--opening-nodes")), ..Default::default() })
    });
    if let Some((p, l)) = opening {
        eprintln!("opening: first {p} plies at {:?} nodes", l.max_nodes);
    }
    // --bid-params P: eval params used only for the bidder's analysis (games use the defaults)
    let bid_params = load_params(a.get("bid-params"));
    let next = Arc::new(AtomicUsize::new(0));
    let seeds = Arc::new(seeds);
    #[derive(Default)]
    struct T { a: f64, n: u32, a_white: u32, a_white_score: f64, a_black_score: f64, bid_sum: i64, bids: Vec<i32>, reasons: HashMap<String, u32> }
    let tally = Arc::new(Mutex::new(T::default()));
    let start = Instant::now();
    let handles: Vec<_> = (0..threads)
        .map(|_| {
            let (next, seeds, tally) = (next.clone(), seeds.clone(), tally.clone());
            let (shares, scales_m, scales_r) = (shares.clone(), scales_m.clone(), scales_r.clone());
            let skipped = skipped.clone();
            let bid_params = bid_params.clone();
            std::thread::spawn(move || {
                let mut ea = Searcher::new(EvalParams::default(), 32);
                let mut eb = Searcher::new(EvalParams::default(), 32);
                let mut e_bid = Searcher::new(bid_params, 32);
                loop {
                    let i = next.fetch_add(1, Ordering::SeqCst);
                    if i >= games * 2 {
                        break;
                    }
                    let seed = &seeds[i / 2];
                    let a_is_creator = i % 2 == 0;
                    ea.clear();
                    eb.clear();
                    e_bid.clear();
                    let start_pos = start_position(seed);
                    let scales = if seed.starts_with('r') { &scales_r } else { &scales_m };
                    let (a_bid, _) = computer_bid_with(&mut e_bid, &start_pos, budget, &shares, scales);
                    ea.clear();
                    if let Some((lo, hi)) = skip {
                        if a_bid >= lo && a_bid <= hi {
                            skipped.fetch_add(1, Ordering::SeqCst);
                            continue;
                        }
                    }
                    let a_white = if a_bid == fixed { a_is_creator } else { a_bid > fixed };
                    let winning = a_bid.max(fixed);
                    let (rec, _) = if a_white {
                        play_game(seed, [&mut ea, &mut eb], [limits; 2], max_plies, false, Some(winning), opening, [None; 2], 0)
                    } else {
                        play_game(seed, [&mut eb, &mut ea], [limits; 2], max_plies, false, Some(winning), opening, [None; 2], 0)
                    };
                    let a_score = match rec.result.chars().next() {
                        Some('w') => if a_white { 1.0 } else { 0.0 },
                        Some('b') => if a_white { 0.0 } else { 1.0 },
                        _ => 0.5,
                    };
                    let mut t = tally.lock().unwrap();
                    t.a += a_score;
                    t.n += 1;
                    if a_white { t.a_white += 1; t.a_white_score += a_score; } else { t.a_black_score += a_score; }
                    if a_is_creator { t.bid_sum += a_bid as i64; t.bids.push(a_bid); }
                    *t.reasons.entry(rec.result.clone()).or_default() += 1;
                    let s = t.a / t.n as f64;
                    eprintln!("[{}/{}] A bid {a_bid} vs {fixed} -> A {} ; A score {:.3} elo {:+.0}", t.n, games * 2, if a_white { "White" } else { "Black" }, s, elo(s));
                }
            })
        })
        .collect();
    for h in handles {
        h.join().unwrap();
    }
    let t = tally.lock().unwrap();
    if skip.is_some() {
        println!("skipped {} of {} games (bid inside the skip range)", skipped.load(Ordering::SeqCst), games * 2);
    }
    let n = t.n as f64;
    let s = t.a / n;
    let se = (s * (1.0 - s) / n).sqrt();
    let mut bids = t.bids.clone();
    bids.sort();
    let a_black = t.n - t.a_white;
    println!(
        "A (bidding) score {:.3} ± {:.3} over {} games  ≈ {:+.0} Elo  ({:.0}s)",
        s, 1.96 * se, t.n, elo(s), start.elapsed().as_secs_f64()
    );
    println!(
        "A played White {} times (score {:.3}), Black {} times (score {:.3})",
        t.a_white,
        if t.a_white > 0 { t.a_white_score / t.a_white as f64 } else { 0.0 },
        a_black,
        if a_black > 0 { t.a_black_score / a_black as f64 } else { 0.0 }
    );
    if !bids.is_empty() {
        println!(
            "A bids: min {} median {} max {} avg {:.1}",
            bids[0], bids[bids.len() / 2], bids[bids.len() - 1], t.bid_sum as f64 / bids.len() as f64
        );
    }
    let mut res: Vec<_> = t.reasons.iter().collect();
    res.sort();
    println!("results: {:?}", res);
}

fn elo(score: f64) -> f64 {
    let s = score.clamp(0.001, 0.999);
    -400.0 * (1.0 / s - 1.0).log10()
}

/// Approximate 95% confidence margin in Elo.
fn elo_margin(w: u32, l: u32, d: u32) -> f64 {
    let n = (w + l + d) as f64;
    let s = (w as f64 + 0.5 * d as f64) / n;
    let var = (w as f64 * (1.0 - s).powi(2) + l as f64 * s.powi(2) + d as f64 * (0.5 - s).powi(2)) / n;
    let se = (var / n).sqrt();
    (elo(s + 1.96 * se) - elo(s - 1.96 * se)) / 2.0
}

/// Evaluation network for an engine: `--a-nnue F` / `--b-nnue F` (match), or `--nnue F` for all.
fn load_nnue(a: &Args, prefix: &str) -> Option<Arc<goldrush_engine::nnue::Network>> {
    let specific = if prefix.is_empty() { None } else { a.get(&format!("{prefix}-nnue")) };
    let path = specific.or(a.get("nnue"))?;
    let net = goldrush_engine::nnue::Network::load(path).unwrap_or_else(|e| panic!("{e}"));
    eprintln!("{}: network {path} ({}x{}, hidden {}, K {:.0})",
              if prefix.is_empty() { "engine" } else { prefix }, net.board_size, net.board_size, net.hidden, net.k);
    Some(Arc::new(net))
}

/// `gg nncheck --net F --check F`: the engine's network outputs vs. PyTorch's (check file from
/// training/export.py), plus evaluation speed vs. the hand-written eval.
fn run_nncheck(a: &Args) {
    use goldrush_engine::eval::evaluate;
    use goldrush_engine::nnue::Network;
    use goldrush_engine::traindata::position_from_record;
    let net = Network::load(a.get("net").expect("--net FILE")).unwrap_or_else(|e| panic!("{e}"));
    let b = std::fs::read(a.get("check").expect("--check FILE")).expect("read check file");
    assert_eq!(&b[0..4], b"GGNC", "not a check file");
    let u = |i: usize| u32::from_le_bytes(b[i..i + 4].try_into().unwrap()) as usize;
    let (count, rec_size, size) = (u(4), u(8), u(12));
    let mut positions = Vec::with_capacity(count);
    let (mut max_diff, mut sum_diff, mut max_eval_diff) = (0f32, 0f64, 0i32);
    for i in 0..count {
        let at = 16 + i * (rec_size + 4);
        let pos = position_from_record(&b[at..at + rec_size], size);
        let expected = f32::from_le_bytes(b[at + rec_size..at + rec_size + 4].try_into().unwrap());
        let got = net.logit(&pos);
        let d = (got - expected).abs();
        max_diff = max_diff.max(d);
        sum_diff += d as f64;
        max_eval_diff = max_eval_diff.max((net.evaluate(&pos) - (expected * net.k).round() as i32).abs());
        positions.push(pos);
    }
    println!("{count} positions: max |logit diff| {max_diff:.2e}, mean {:.2e}, max eval diff {max_eval_diff} (1 gold = 10)",
             sum_diff / count.max(1) as f64);
    println!("{}", if max_diff < 1e-3 { "MATCH: engine and PyTorch agree" } else { "MISMATCH" });
    // The search uses 16-bit integer arithmetic: small rounding differences are expected
    let (mut qmax, mut qsum) = (0i32, 0f64);
    for (i, pos) in positions.iter().enumerate() {
        let at = 16 + i * (rec_size + 4);
        let expected = f32::from_le_bytes(b[at + rec_size..at + rec_size + 4].try_into().unwrap());
        let d = (net.evaluate_quantized(pos) - (expected * net.k).round() as i32).abs();
        qmax = qmax.max(d);
        qsum += d as f64;
    }
    println!("quantised (search) evaluation vs PyTorch: max diff {qmax}, mean {:.2} (eval units, 1 gold = 10)",
             qsum / count.max(1) as f64);

    let params = EvalParams::default();
    let reps = (200_000 / count.max(1)).max(1);
    let t = Instant::now();
    let mut acc = 0i64;
    for _ in 0..reps {
        for p in &positions {
            acc += net.evaluate(p) as i64;
        }
    }
    let net_us = t.elapsed().as_secs_f64() * 1e6 / (reps * count) as f64;
    let t = Instant::now();
    for _ in 0..reps {
        for p in &positions {
            acc += evaluate(p, &params) as i64;
        }
    }
    let hand_us = t.elapsed().as_secs_f64() * 1e6 / (reps * count) as f64;
    println!("speed: network {net_us:.2} µs/eval, hand-written {hand_us:.2} µs/eval ({:.1}x)  [{acc}]", net_us / hand_us);

    // Searches: incremental evaluation must equal from-scratch evaluation everywhere in the tree
    let net = Arc::new(net);
    let searches = positions.len().min(a.num("searches", 60));
    let limits = Limits { max_nodes: Some(a.num("nodes", 20_000)), ..Default::default() };
    let mut s = Searcher::new(EvalParams::default(), 32);
    s.nnue = Some(net.clone());
    s.nn_verify = true;
    for p in positions.iter_mut().take(searches) {
        s.search(p, limits);
    }
    let (refreshes, updates) = s.nn_stats();
    println!("search check: {searches} searches, {} mismatches (incremental vs from scratch), {refreshes} refreshes, {updates} updates",
             s.nn_mismatches);

    // Search speed with the network vs. the hand-written eval
    let mut nps = |net: Option<Arc<goldrush_engine::nnue::Network>>| {
        let mut s = Searcher::new(EvalParams::default(), 32);
        s.nnue = net;
        let (mut nodes, t) = (0u64, Instant::now());
        for p in positions.iter_mut().take(searches) {
            s.clear();
            nodes += s.search(p, limits).nodes;
        }
        nodes as f64 / t.elapsed().as_secs_f64()
    };
    let (with_net, hand) = (nps(Some(net.clone())), nps(None));
    println!("search speed: network {:.0}k nodes/s, hand-written {:.0}k nodes/s ({:.1}x slower)",
             with_net / 1e3, hand / 1e3, hand / with_net);
}

/// Prints a liveness ticker on stderr while a long job runs: "-" every minute and the minute
/// number every 5 minutes, followed by `progress()` in brackets (e.g. "0----5[312 games]----10[640 games]").
fn start_ticker(progress: impl Fn() -> String + Send + 'static) -> (Arc<std::sync::atomic::AtomicBool>, std::thread::JoinHandle<()>) {
    let stop = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let s = stop.clone();
    let h = std::thread::spawn(move || {
        let start = Instant::now();
        eprint!("  0");
        let mut last_min = 0;
        while !s.load(Ordering::Relaxed) {
            std::thread::sleep(std::time::Duration::from_millis(200));
            let min = (start.elapsed().as_secs() / 60) as usize;
            if min > last_min {
                last_min = min;
                if min % 5 == 0 {
                    eprint!("{min}[{}]", progress());
                } else {
                    eprint!("-");
                }
            }
        }
        eprintln!();
    });
    (stop, h)
}

/// Training data for the evaluation network: engine-vs-engine games where every position is
/// recorded with its search score, and the game result is added when the game ends.
/// Format: `goldrush_engine::traindata` / docs/training.md.
fn run_gendata(a: &Args) {
    use goldrush_engine::traindata::{self, Record};
    let seeds = load_seeds(a.get("seeds").expect("--seeds"));
    // --seed-offset N: start at line N of the seed file (boards of earlier runs aren't reused)
    let seed_offset: usize = a.num("seed-offset", 0);
    assert!(seed_offset < seeds.len(), "--seed-offset beyond the seed file");
    let seeds: Vec<String> = seeds[seed_offset..].to_vec();
    let games: usize = a.num("games", seeds.len());
    let threads = a.num("threads", std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4));
    let nodes: u64 = a.num("nodes", 20_000);
    let limits = Limits { max_nodes: Some(nodes), ..Default::default() };
    // Opening diversity: each game plays its first k ∈ [0, random-plies] plies by picking a random
    // line among the top `random-multipv` that is at most `random-margin` gold worse than the best.
    let random_plies: usize = a.num("random-plies", 8);
    let random_multipv: usize = a.num("random-multipv", 4);
    let random_margin: f64 = a.num("random-margin", 3.0);
    let random_limits = Limits { max_nodes: Some(a.num("random-nodes", (nodes / 4).max(1000))), ..Default::default() };
    let max_plies = a.num("max-plies", 400usize);
    let first_game_id: u32 = a.num("first-game-id", 0);
    let rng_seed: u64 = a.num("rng-seed", 1);
    let out_path = a.get("out").expect("--out FILE").to_string();
    if std::path::Path::new(&out_path).exists() && a.get("overwrite").is_none() {
        eprintln!("{out_path} exists; pass --overwrite to replace it");
        std::process::exit(1);
    }
    if games > seeds.len() {
        eprintln!("warning: {games} games but only {} seeds; boards will repeat (with different openings)", seeds.len());
    }

    let first = start_position(&seeds[0]);
    let size = first.size();
    let rules = first.rules;
    let mut file = std::io::BufWriter::new(std::fs::File::create(&out_path).expect("create --out"));
    traindata::write_header(&mut file, size, rules, nodes).unwrap();
    let out = Arc::new(Mutex::new(file));

    println!("gendata: {games} games, {threads} threads, {nodes} nodes/move, board {size}x{size}, black gold {}",
             BLACK_GOLD.load(Ordering::Relaxed));
    println!("  openings: 0-{random_plies} random plies (top {random_multipv} within {random_margin} gold, {:?} nodes/line)",
             random_limits.max_nodes.unwrap());
    println!("  output: {out_path} ({} bytes/position)", traindata::record_size(size));

    #[derive(Default)]
    struct Tally {
        games: usize,
        positions: u64,
        in_check: u64,
        tactical: u64,
        random: u64,
        results: HashMap<String, u32>,
        plies: u64,
        depth_sum: u64,
        bought: [u64; 5],
        moves: u64,
        skipped_size: usize,
    }
    let tally = Arc::new(Mutex::new(Tally::default()));
    let net = load_nnue(a, ""); // --nnue: the network drives the search (static_eval stays hand-written)
    let next = Arc::new(AtomicUsize::new(0));
    let seeds = Arc::new(seeds);
    let start = Instant::now();
    let tick_tally = tally.clone();
    let (stop, ticker) = start_ticker(move || format!("{} games", tick_tally.lock().unwrap().games));

    let handles: Vec<_> = (0..threads)
        .map(|_| {
            let (next, seeds, out, tally, net) = (next.clone(), seeds.clone(), out.clone(), tally.clone(), net.clone());
            std::thread::spawn(move || {
                let mut s = Searcher::new(EvalParams::default(), 32);
                s.nnue = net;
                loop {
                    let i = next.fetch_add(1, Ordering::SeqCst);
                    if i >= games {
                        break;
                    }
                    let mut pos = start_position(&seeds[i % seeds.len()]);
                    if pos.size() != size {
                        tally.lock().unwrap().skipped_size += 1;
                        continue;
                    }
                    pos.gold[BLACK] = BLACK_GOLD.load(Ordering::Relaxed);
                    s.clear();
                    let game_id = first_game_id + i as u32;
                    let mut rng = (rng_seed ^ (i as u64).wrapping_mul(0x9E37_79B9_7F4A_7C15)) | 1;
                    rng_next(&mut rng);
                    let k = (rng_next(&mut rng) * (random_plies + 1) as f64) as usize;
                    let mut records: Vec<Record> = Vec::with_capacity(160);
                    let mut moves = Vec::new();
                    let mut outcome = Outcome::Ongoing;
                    let (mut depth_sum, mut bought, mut random) = (0u64, [0u64; 5], 0u64);
                    for ply in 0..max_plies {
                        let (m, score, depth, is_random) = if ply < k {
                            let lines = s.search_multi(&mut pos, random_limits, random_multipv);
                            if lines.is_empty() {
                                break;
                            }
                            let best = lines[0].score;
                            let ok: Vec<&_> = lines.iter().filter(|l| l.best.is_some() && (best - l.score) as f64 <= random_margin * 10.0).collect();
                            let pick = ok[(rng_next(&mut rng) * ok.len() as f64) as usize % ok.len()];
                            (pick.best, best, lines[0].depth, pick.best != lines[0].best)
                        } else {
                            let r = s.search(&mut pos, limits);
                            (r.best, r.score, r.depth, false)
                        };
                        let Some(m) = m else { break };
                        let static_eval = goldrush_engine::eval::evaluate(&pos, &s.params);
                        records.push(Record::new(&pos, score, static_eval, Some(m), depth, game_id, is_random));
                        depth_sum += depth as u64;
                        random += is_random as u64;
                        if m.is_placement() {
                            if let Some(j) = SHOP.iter().position(|&(kd, _)| kd == m.place) {
                                bought[j] += 1;
                            }
                        }
                        pos.make(m);
                        legal_moves(&mut pos, &mut moves);
                        outcome = pos.outcome(!moves.is_empty());
                        if outcome != Outcome::Ongoing {
                            break;
                        }
                    }
                    let (winner, reason) = match outcome {
                        Outcome::Ongoing => (None, traindata::REASON_NONE),
                        Outcome::Win { winner, reason } => (
                            Some(winner),
                            match reason {
                                WinReason::Checkmate => traindata::REASON_CHECKMATE,
                                WinReason::Gold => traindata::REASON_GOLD,
                                WinReason::Stalemate => traindata::REASON_STALEMATE,
                            },
                        ),
                    };
                    {
                        let mut o = out.lock().unwrap();
                        for r in records.iter_mut() {
                            r.set_result(winner, reason);
                            o.write_all(&r.bytes).unwrap();
                        }
                    }
                    let mut t = tally.lock().unwrap();
                    t.games += 1;
                    t.positions += records.len() as u64;
                    t.in_check += records.iter().filter(|r| r.bytes[size * size + 1] & traindata::FLAG_IN_CHECK != 0).count() as u64;
                    t.tactical += records.iter().filter(|r| r.bytes[size * size + 1] & traindata::FLAG_BEST_TACTICAL != 0).count() as u64;
                    t.random += random;
                    *t.results.entry(result_str(outcome)).or_default() += 1;
                    t.plies += records.len() as u64;
                    t.depth_sum += depth_sum;
                    t.moves += records.len() as u64;
                    for j in 0..5 {
                        t.bought[j] += bought[j];
                    }
                }
            })
        })
        .collect();
    for h in handles {
        h.join().unwrap();
    }
    stop.store(true, Ordering::Relaxed);
    ticker.join().unwrap();
    out.lock().unwrap().flush().unwrap();

    let t = tally.lock().unwrap();
    let secs = start.elapsed().as_secs_f64();
    let g = t.games.max(1) as f64;
    let pct = |n: u64| 100.0 * n as f64 / t.positions.max(1) as f64;
    let res = |k: &str| *t.results.get(k).unwrap_or(&0);
    let white = res("w+c") + res("w+g") + res("w+s");
    let black = res("b+c") + res("b+g") + res("b+s");
    let mates = res("w+c") + res("b+c");
    let gold = res("w+g") + res("b+g");
    let stale = res("w+s") + res("b+s");
    let draws = res("draw");
    let size_mb = std::fs::metadata(&out_path).map(|m| m.len() as f64 / 1e6).unwrap_or(0.0);
    let line = "=".repeat(44);
    println!("\n{line}\n  gendata summary\n{line}");
    println!("  Games:                 {}", t.games);
    if t.skipped_size > 0 {
        println!("  Skipped (other size):  {}", t.skipped_size);
    }
    println!("  Positions:             {}", t.positions);
    println!("  Positions/hour:        {:.0}", t.positions as f64 / secs * 3600.0);
    println!("  File size (MB):        {size_mb:.1}");
    println!("  Time (min):            {:.1}", secs / 60.0);
    println!("  White / Black / Draw:  {:.1}% / {:.1}% / {:.1}%", 100.0 * white as f64 / g, 100.0 * black as f64 / g, 100.0 * draws as f64 / g);
    println!("  Mates / Gold / Stale:  {:.1}% / {:.1}% / {:.1}%", 100.0 * mates as f64 / g, 100.0 * gold as f64 / g, 100.0 * stale as f64 / g);
    println!("  Avg plies:             {:.1}", t.plies as f64 / g);
    println!("  Avg depth:             {:.2}", t.depth_sum as f64 / t.positions.max(1) as f64);
    println!("  In check:              {:.1}%", pct(t.in_check));
    println!("  Best move tactical:    {:.1}%", pct(t.tactical));
    println!("  Random opening moves:  {:.1}%", pct(t.random));
    println!("  Placements % (Q R B N P): {:.2} / {:.2} / {:.2} / {:.2} / {:.2}",
             pct(t.bought[0]), pct(t.bought[1]), pct(t.bought[2]), pct(t.bought[3]), pct(t.bought[4]));
    println!("{line}");
}

fn main() {
    let argv: Vec<String> = std::env::args().collect();
    let cmd = argv.get(1).map(|s| s.as_str()).unwrap_or("help");
    let a = Args::parse(&argv[1..]);
    BLACK_GOLD.store(a.num("black-gold", 6), Ordering::Relaxed);
    if a.get("classic").is_some() {
        RULES.set(Rules::classic()).unwrap();
    } else if let Some(d) = a.get("queen-drops") {
        let d = if d == "1" { "" } else { d }; // bare flag = none
        RULES.set(Rules { queen_drops: Rules::drops_mask(d), queen_cost: a.num("queen-cost", piece_cost(QUEEN)) }).unwrap();
    }
    if let Some(r) = RULES.get() {
        eprintln!("rules: {r:?}");
    }
    match cmd {
        "bench" => bench(&a),
        "selfplay" => selfplay(&a),
        "match" => run_match(&a),
        "bidmatch" => run_bidmatch(&a),
        "gendata" => run_gendata(&a),
        "nncheck" => run_nncheck(&a),
        "bid" => {
            // Print the bidding passes for the first N seeds
            let seeds = load_seeds(a.get("seeds").unwrap_or("testdata/seeds_m10.txt"));
            let budget: u64 = a.num("nodes", 12_000u64) * goldrush_engine::bid::BID_BUDGET_FACTOR;
            let mut s = Searcher::new(EvalParams::default(), 32);
            for seed in seeds.iter().take(a.num("games", 3)) {
                let pos = start_position(seed);
                s.clear();
                let (bid, steps) = goldrush_engine::bid::computer_bid(&mut s, &pos, budget, seed.starts_with('r'));
                let trace: String = steps.iter().map(|(g, adv, est)| format!("B{g}:{adv:.1}->{est:.2}  ")).collect();
                let trace = format!("{trace}=> bid {bid}");
                println!("{:<24} {trace}", &seed[..seed.len().min(24)]);
            }
        }
        "play" => {
            let seed = a.get("seed").map(|s| s.to_string()).unwrap_or_else(|| load_seeds("testdata/seeds_m10.txt")[0].clone());
            let params = load_params(a.get("params"));
            let mut w = Searcher::new(params.clone(), 64);
            let mut b = Searcher::new(params, 64);
            w.nnue = load_nnue(&a, "");
            b.nnue = w.nnue.clone();
            let (rec, _) = play_game(&seed, [&mut w, &mut b], [a.limits(); 2], a.num("max-plies", 400), true, None, None, [None; 2], 0);
            println!("{} plies, bought W{} B{}", rec.plies, rec.pieces_bought[0], rec.pieces_bought[1]);
        }
        _ => eprintln!("usage: gg bench|selfplay|match|play [options]  (see src/bin/gg.rs)"),
    }
}
