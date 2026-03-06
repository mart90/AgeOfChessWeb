# python3 iterate.py --resume --boards 900 --workers 17 --iterations 5 --noise-weight 0.25

"""Iterative self-play training loop."""
import argparse
import json
import os
import time
import torch

from model import PolicyNetwork
from self_play import (
    generate_training_data, generate_training_data_parallel, save_training_data,
    make_policy_fn, play_game, check_victory, pick_random_move, MAX_MOVES,
    policy_move, GOLD_VICTORY_THRESHOLD,
)
from train import run_training, export_onnx
from generate_boards import fetch_boards
from encode import encode_board, move_to_index, get_legal_move_mask
from board import M, P


def serialize_board(board):
    """Serialize initial board state to JSON-compatible dict."""
    squares = []
    for sq in board.squares:
        s = {"id": sq.id, "x": sq.x, "y": sq.y}
        if sq.terrain_type is not None:
            s["terrain"] = sq.terrain_type
        if sq.piece_type is not None:
            s["piece"] = sq.piece_type
            s["isWhite"] = sq.piece_is_white
        if sq.has_treasure:
            s["treasure"] = True
        if sq.owned_by is not None:
            s["ownedBy"] = sq.owned_by
        squares.append(s)
    return {
        "size": board.size,
        "squares": squares,
        "whiteGold": board.white_gold,
        "blackGold": board.black_gold,
    }


def serialize_move(move):
    """Serialize a move tuple to JSON-compatible dict."""
    if move[0] == M:
        return {"type": "move", "from": move[1], "to": move[2]}
    else:
        return {"type": "place", "piece": move[1], "dest": move[2], "isWhite": move[3]}


def evaluate_vs_benchmark(model, device, benchmark_path, num_games=30, temperature=0.0):
    """Play model vs benchmark model and return score (wins + 0.5*draws)."""
    boards = fetch_boards(amount=num_games)
    model.eval()

    # Load benchmark model
    if os.path.exists(benchmark_path):
        benchmark = PolicyNetwork().to(device)
        benchmark.load_state_dict(torch.load(benchmark_path, map_location=device))
        benchmark.eval()
        bench_label = os.path.basename(benchmark_path)
    else:
        benchmark = None
        bench_label = "random"

    model_wins = 0
    bench_wins = 0
    draws = 0
    mate_endings = 0
    move_counts = []
    model_pawn_placements = 0
    model_queen_placements = 0
    model_total_moves = 0
    recorded_game = None  # Will store first game for inspection

    for i in range(num_games):
        board = boards[i].clone()
        initial_board = boards[i].clone()  # Save initial state for JSON
        model_is_white = (i % 2 == 0)
        move_count = 0
        moves_list = [] if i == 0 else None  # Record first game only

        while move_count < MAX_MOVES:
            result = check_victory(board)
            if result is not None:
                break

            legal_moves = board.get_legal_moves()
            if len(legal_moves) == 0:
                result = -1 if board.white_is_active else 1
                break

            is_model_turn = (board.white_is_active == model_is_white)
            if is_model_turn:
                move = policy_move(model, board, legal_moves, device, temperature)
                model_total_moves += 1
                # Track piece placements
                if move[0] == P:
                    if move[1] == "p":
                        model_pawn_placements += 1
                    elif move[1] == "q":
                        model_queen_placements += 1
            elif benchmark is not None:
                move = policy_move(benchmark, board, legal_moves, device, temperature)
            else:
                move = pick_random_move(legal_moves, placement_bias=1.0)

            if moves_list is not None:
                moves_list.append(serialize_move(move))

            board.do_move(move)
            move_count += 1
        else:
            result = 0

        move_counts.append(move_count)

        # Track mate endings (not gold victory, not draw)
        if result != 0:
            is_gold_victory = (board.white_gold >= GOLD_VICTORY_THRESHOLD or
                             board.black_gold >= GOLD_VICTORY_THRESHOLD)
            if not is_gold_victory:
                mate_endings += 1

        # Save first game for inspection
        if i == 0:
            result_str = {1: "White wins", -1: "Black wins", 0: "Draw"}[result]
            recorded_game = {
                "gameNumber": 1,
                "initialBoard": serialize_board(initial_board),
                "moves": moves_list,
                "result": result,
                "totalMoves": len(moves_list),
                "modelSide": "white" if model_is_white else "black",
                "temperature": temperature,
            }

        model_won = (result == 1 and model_is_white) or (result == -1 and not model_is_white)
        if result == 0:
            draws += 1
        elif model_won:
            model_wins += 1
        else:
            bench_wins += 1

    total = model_wins + bench_wins + draws
    score = (model_wins + draws * 0.5) / total if total > 0 else 0
    avg_moves = sum(move_counts) / len(move_counts) if move_counts else 0
    pawn_rate = 100 * model_pawn_placements / model_total_moves if model_total_moves > 0 else 0
    queen_rate = 100 * model_queen_placements / model_total_moves if model_total_moves > 0 else 0

    print(f"  Eval vs {bench_label}:")
    print(f"    Result: {model_wins}W / {bench_wins}L / {draws}D ({100*score:.0f}% score)")
    print(f"    Games: {num_games} total, {avg_moves:.1f} avg moves, {mate_endings} mates")
    print(f"    Model: {pawn_rate:.2f}% pawns, {queen_rate:.2f}% queens")

    # Save first game for inspection
    if recorded_game is not None:
        os.makedirs("eval_games", exist_ok=True)
        save_path = "eval_games/last_eval_game.json"
        with open(save_path, "w") as f:
            json.dump(recorded_game, f, indent=2)
        print(f"  Saved eval game to {save_path}")

    return score


def fetch_all_boards(count):
    """Fetch boards from server in batches of 100."""
    boards = []
    remaining = count
    while remaining > 0:
        batch = min(remaining, 100)
        boards.extend(fetch_boards(amount=batch))
        remaining -= batch
    return boards


def main():
    parser = argparse.ArgumentParser(description="Iterative self-play training")
    parser.add_argument("--iterations", type=int, default=10, help="Number of iterations")
    parser.add_argument("--boards", type=int, default=1000, help="Boards per iteration")
    parser.add_argument("--games-per-board", type=int, default=1, help="Games per board")
    parser.add_argument("--epochs", type=int, default=20, help="Max epochs per iteration")
    parser.add_argument("--batch-size", type=int, default=256, help="Training batch size")
    parser.add_argument("--lr", type=float, default=1e-3, help="Learning rate")
    parser.add_argument("--patience", type=int, default=1, help="Early stopping patience")
    parser.add_argument("--temperature", type=float, default=1.0, help="Self-play temperature")
    parser.add_argument("--eval-games", type=int, default=100, help="Evaluation games per iteration")
    parser.add_argument("--save-dir", type=str, default="checkpoints", help="Checkpoint directory")
    parser.add_argument("--resume", action="store_true", help="Resume from best_overall.pt")
    parser.add_argument("--workers", type=int, default=16,
                        help="Number of worker processes for self-play (default: cpu_count - 2)")
    parser.add_argument("--benchmark", type=str, default="checkpoints/benchmark.pt",
                        help="Path to benchmark model for evaluation (falls back to random if not found)")
    parser.add_argument("--noise-weight", type=float, default=0.25,
                        help="Fraction of heuristic noise to mix into policy (0-1, 0 = off)")
    args = parser.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Using {device}")

    os.makedirs(args.save_dir, exist_ok=True)

    results = []
    best_overall_score = 0
    best_model_path = None

    if args.resume:
        candidate = os.path.join(args.save_dir, "best_overall.pt")
        if os.path.exists(candidate):
            best_model_path = candidate
            print(f"Resuming from {candidate}")
        else:
            print("No best_overall.pt found, starting from scratch")

    for iteration in range(1, args.iterations + 1):
        print(f"\n{'='*60}")
        print(f"Iteration {iteration}/{args.iterations}")
        print(f"{'='*60}")

        # Generate training data
        print(f"Generating data: {args.boards} boards x {args.games_per_board} games")
        all_boards = fetch_all_boards(args.boards)

        t0 = time.time()
        if best_model_path:
            print(f"  Using model from {best_model_path} for self-play (parallel)")
            board_tensors, move_indices, game_ids = generate_training_data_parallel(
                all_boards,
                model_path=best_model_path,
                games_per_board=args.games_per_board,
                temperature=args.temperature,
                workers=args.workers,
                noise_weight=args.noise_weight,
            )
        else:
            board_tensors, move_indices, game_ids = generate_training_data(
                all_boards,
                games_per_board=args.games_per_board,
            )
        elapsed = time.time() - t0
        print(f"  Self-play took {elapsed:.1f}s")

        if board_tensors is None:
            print("  No data generated, skipping iteration")
            results.append({"iteration": iteration, "val_loss": None, "score": 0})
            continue

        data_path = os.path.join(args.save_dir, f"iter{iteration}_data.npz")
        save_training_data(data_path, board_tensors, move_indices, game_ids)

        # Train
        model = PolicyNetwork().to(device)
        if best_model_path:
            model.load_state_dict(torch.load(best_model_path, map_location=device))
            print(f"  Fine-tuning from {best_model_path}")

        model, val_loss = run_training(
            board_tensors, move_indices, device,
            game_ids=game_ids,
            model=model,
            epochs=args.epochs,
            batch_size=args.batch_size,
            lr=args.lr,
            patience=args.patience,
            save_path=None,  # Don't save individual iteration checkpoints
        )

        # Evaluate
        bench_label = "benchmark" if os.path.exists(args.benchmark) else "random"
        print(f"Evaluating vs {bench_label} ({args.eval_games} games)...")
        score = evaluate_vs_benchmark(model, device, args.benchmark, num_games=args.eval_games, temperature=0.0)

        # Save as best_overall for next iteration
        overall_best = os.path.join(args.save_dir, "best_overall.pt")
        torch.save(model.state_dict(), overall_best)
        best_model_path = overall_best
        if score > best_overall_score:
            best_overall_score = score
            print(f"  New best score: {100*score:.0f}%")

        results.append({
            "iteration": iteration,
            "val_loss": val_loss,
            "score": score,
        })

    # Summary
    print(f"\n{'='*60}")
    print("Summary")
    print(f"{'='*60}")
    print(f"{'Iter':>4}  {'Val Loss':>9}  {'Score':>9}")
    print(f"{'-'*4}  {'-'*9}  {'-'*9}")
    for r in results:
        vl = f"{r['val_loss']:.4f}" if r['val_loss'] is not None else "   N/A"
        sc = f"{100*r['score']:.0f}%"
        print(f"{r['iteration']:4d}  {vl:>9}  {sc:>9}")

    # Export best model to ONNX
    if best_model_path:
        best_model = PolicyNetwork().to(device)
        best_model.load_state_dict(torch.load(best_model_path, map_location=device))
        onnx_path = os.path.join(args.save_dir, "policy_net.onnx")
        export_onnx(best_model, onnx_path, device)


if __name__ == "__main__":
    main()
