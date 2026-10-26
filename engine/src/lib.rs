//! Goldrush Gambit engine.
//!
//! Rules mirror the authoritative C# implementation in `AgeOfChess.Server/GameLogic/`
//! (see `docs/game-rules.md`). Move legality is defined as "own king not attacked after
//! the move", which is what the C# pin/check logic computes.

pub mod position;
pub mod movegen;
pub mod seed;
pub mod eval;
pub mod search;
pub mod clock;
pub mod api;
pub mod bid;
#[cfg(target_arch = "wasm32")]
pub mod wasm;

pub use position::*;
