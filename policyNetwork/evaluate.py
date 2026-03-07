"""Evaluate trained model vs random play and save replay games."""
import argparse
import json
import os
import torch

from model import PolicyNetwork
from self_play import check_victory, pick_random_move, policy_move, MAX_MOVES
from generate_boards import fetch_boards
from record_games import serialize_board, serialize_move


def play_eval_game(board, model, device, model_is_white=True, temperature=0.5):
    """Play model vs random. Returns (moves, result, model_was_white)."""
    game_board = board.clone()
    moves = []
    move_count = 0

    while move_count < MAX_MOVES:
        result = check_victory(game_board)
        if result is not None:
            return moves, result, model_is_white

        legal_moves = game_board.get_legal_moves()
        if len(legal_moves) == 0:
            result = -1 if game_board.white_is_active else 1
            return moves, result, model_is_white

        is_model_turn = (game_board.white_is_active == model_is_white)

        if is_model_turn:
            move = policy_move(model, game_board, legal_moves, device, temperature, filter_pawns=False)
        else:
            move = pick_random_move(legal_moves, placement_bias=1.0)

        moves.append(serialize_move(move))
        game_board.do_move(move)
        move_count += 1

    return moves, 0, model_is_white


def main():
    parser = argparse.ArgumentParser(description="Evaluate model vs random")
    parser.add_argument("--model-file", type=str, default="checkpoints/phase1_best.pt")
    parser.add_argument("--games", type=int, default=50, help="Number of games to play")
    parser.add_argument("--save", type=int, default=5, help="Number of replays to save")
    parser.add_argument("--temperature", type=float, default=0.5)
    args = parser.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Using {device}")

    model = PolicyNetwork().to(device)
    model.load_state_dict(torch.load(args.model_file, map_location=device))
    model.eval()
    print(f"Loaded model from {args.model_file}")

    print(f"Fetching {args.games} boards...")
    boards = fetch_boards(amount=args.games)

    os.makedirs("replays", exist_ok=True)
    model_wins = 0
    random_wins = 0
    draws = 0
    saved = 0

    for i in range(args.games):
        model_is_white = (i % 2 == 0)  # alternate sides
        moves, result, was_white = play_eval_game(
            boards[i], model, device,
            model_is_white=model_is_white,
            temperature=args.temperature,
        )

        model_won = (result == 1 and was_white) or (result == -1 and not was_white)
        random_won = (result == 1 and not was_white) or (result == -1 and was_white)

        if result == 0:
            draws += 1
            tag = "Draw"
        elif model_won:
            model_wins += 1
            tag = "Model wins"
        else:
            random_wins += 1
            tag = "Random wins"

        side = "W" if was_white else "B"
        print(f"Game {i+1:2d} (model={side}): {len(moves)} moves, {tag}")

        if saved < args.save:
            game_data = {
                "gameNumber": i + 1,
                "initialBoard": serialize_board(boards[i]),
                "moves": moves,
                "result": result,
                "totalMoves": len(moves),
                "modelIsWhite": was_white,
            }
            path = os.path.join("replays", f"eval_{i+1:02d}.json")
            with open(path, "w") as f:
                json.dump(game_data, f, indent=2)
            print(f"  -> Saved to {path}")
            saved += 1

    total = model_wins + random_wins + draws
    print(f"\nResults ({total} games):")
    print(f"  Model wins:  {model_wins} ({100*model_wins/total:.1f}%)")
    print(f"  Random wins: {random_wins} ({100*random_wins/total:.1f}%)")
    print(f"  Draws:       {draws} ({100*draws/total:.1f}%)")


if __name__ == "__main__":
    main()
