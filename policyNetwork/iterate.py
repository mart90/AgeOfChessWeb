"""Iterative self-play training loop."""
import argparse
import os
import time
import torch

from model import PolicyNetwork
from self_play import (
    generate_training_data, generate_training_data_parallel, save_training_data,
    make_policy_fn, play_game, check_victory, pick_random_move, MAX_MOVES,
    policy_move,
)
from train import run_training, export_onnx
from generate_boards import fetch_boards
from encode import encode_board, move_to_index, get_legal_move_mask
from board import M, P


def evaluate_vs_benchmark(model, device, benchmark_path, num_games=30, temperature=0.5):
    """Play model vs benchmark model and return win rate."""
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

    for i in range(num_games):
        board = boards[i].clone()
        model_is_white = (i % 2 == 0)
        move_count = 0

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
            elif benchmark is not None:
                move = policy_move(benchmark, board, legal_moves, device, temperature)
            else:
                move = pick_random_move(legal_moves, placement_bias=1.0)

            board.do_move(move)
            move_count += 1
        else:
            result = 0

        model_won = (result == 1 and model_is_white) or (result == -1 and not model_is_white)
        if result == 0:
            draws += 1
        elif model_won:
            model_wins += 1
        else:
            bench_wins += 1

    total = model_wins + bench_wins + draws
    win_rate = model_wins / total if total > 0 else 0
    print(f"  Eval vs {bench_label}: {model_wins}W / {bench_wins}L / {draws}D "
          f"({100*win_rate:.0f}% win rate, {num_games} games)")
    return win_rate


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
    parser.add_argument("--patience", type=int, default=2, help="Early stopping patience")
    parser.add_argument("--temperature", type=float, default=1.0, help="Self-play temperature")
    parser.add_argument("--eval-games", type=int, default=200, help="Evaluation games per iteration")
    parser.add_argument("--save-dir", type=str, default="checkpoints", help="Checkpoint directory")
    parser.add_argument("--resume", action="store_true", help="Resume from best_overall.pt")
    parser.add_argument("--workers", type=int, default=None,
                        help="Number of worker processes for self-play (default: cpu_count - 2)")
    parser.add_argument("--benchmark", type=str, default="checkpoints/benchmark.pt",
                        help="Path to benchmark model for evaluation (falls back to random if not found)")
    parser.add_argument("--noise-weight", type=float, default=0.25,
                        help="Fraction of heuristic noise to mix into policy (0-1, 0 = off)")
    parser.add_argument("--selfplay-model", type=str,
                        help="Use a different model for self-play generation (e.g., old architecture). Training still uses --resume model.")
    args = parser.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Using {device}")

    os.makedirs(args.save_dir, exist_ok=True)

    results = []
    best_overall_win_rate = 0
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

        # Use --selfplay-model if specified, otherwise use best_model_path
        selfplay_path = args.selfplay_model if args.selfplay_model else best_model_path

        t0 = time.time()
        if selfplay_path:
            print(f"  Using model from {selfplay_path} for self-play (parallel)")
            board_tensors, move_indices, game_ids = generate_training_data_parallel(
                all_boards,
                model_path=selfplay_path,
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
            results.append({"iteration": iteration, "val_loss": None, "win_rate": 0})
            continue

        data_path = os.path.join(args.save_dir, f"iter{iteration}_data.npz")
        save_training_data(data_path, board_tensors, move_indices, game_ids)

        # Train
        model = PolicyNetwork().to(device)
        if best_model_path:
            model.load_state_dict(torch.load(best_model_path, map_location=device))
            print(f"  Fine-tuning from {best_model_path}")

        save_path = os.path.join(args.save_dir, f"iter{iteration}_best.pt")
        model, val_loss = run_training(
            board_tensors, move_indices, device,
            game_ids=game_ids,
            model=model,
            epochs=args.epochs,
            batch_size=args.batch_size,
            lr=args.lr,
            patience=args.patience,
            save_path=save_path,
        )

        # Evaluate
        bench_label = "benchmark" if os.path.exists(args.benchmark) else "random"
        print(f"Evaluating vs {bench_label} ({args.eval_games} games)...")
        win_rate = evaluate_vs_benchmark(model, device, args.benchmark, num_games=args.eval_games, temperature=0.5)

        # Always use the latest model for next iteration
        overall_best = os.path.join(args.save_dir, "best_overall.pt")
        best_model_path = save_path
        torch.save(model.state_dict(), overall_best)
        if win_rate > best_overall_win_rate:
            best_overall_win_rate = win_rate
            print(f"  New best win rate: {100*win_rate:.0f}%")

        results.append({
            "iteration": iteration,
            "val_loss": val_loss,
            "win_rate": win_rate,
        })

    # Summary
    print(f"\n{'='*60}")
    print("Summary")
    print(f"{'='*60}")
    print(f"{'Iter':>4}  {'Val Loss':>9}  {'Win Rate':>9}")
    print(f"{'-'*4}  {'-'*9}  {'-'*9}")
    for r in results:
        vl = f"{r['val_loss']:.4f}" if r['val_loss'] is not None else "   N/A"
        wr = f"{100*r['win_rate']:.0f}%"
        print(f"{r['iteration']:4d}  {vl:>9}  {wr:>9}")

    # Export best model to ONNX
    if best_model_path:
        best_model = PolicyNetwork().to(device)
        best_model.load_state_dict(torch.load(best_model_path, map_location=device))
        onnx_path = os.path.join(args.save_dir, "policy_net.onnx")
        export_onnx(best_model, onnx_path, device)


if __name__ == "__main__":
    main()
