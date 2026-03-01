import random
import numpy as np
import torch
import torch.nn.functional as F
from board import M, P
from encode import encode_board, move_to_index, get_legal_move_mask, augment_180, NUM_ACTIONS

MAX_MOVES = 300
GOLD_VICTORY_THRESHOLD = 175  # 10x10 board


def check_victory(board):
    """
    Check if the game is over.
    Returns +1 if white wins, -1 if black wins, None if game continues.
    """
    # Gold victory
    if board.white_gold >= GOLD_VICTORY_THRESHOLD:
        return 1
    if board.black_gold >= GOLD_VICTORY_THRESHOLD:
        return -1

    # Mate: active player has 0 legal moves → they lose
    legal_moves = board.get_legal_moves()
    if len(legal_moves) == 0:
        return -1 if board.white_is_active else 1

    return None


def pick_random_move(legal_moves, placement_bias=2.0):
    """Pick a random move with optional bias toward placements."""
    weights = [placement_bias if m[0] == P else 1.0 for m in legal_moves]
    return random.choices(legal_moves, weights=weights, k=1)[0]


def policy_move(model, board, legal_moves, device, temperature=1.0):
    """Pick a move using the policy network."""
    encoded = encode_board(board)
    board_tensor = torch.from_numpy(encoded).unsqueeze(0).to(device)

    with torch.no_grad():
        logits = model(board_tensor).squeeze(0)

    mask = get_legal_move_mask(board)
    mask_tensor = torch.from_numpy(mask).to(device)
    logits = logits + mask_tensor

    if temperature <= 0:
        idx = logits.argmax().item()
    else:
        probs = F.softmax(logits / temperature, dim=0)
        idx = torch.multinomial(probs, 1).item()

    for move in legal_moves:
        if move_to_index(move) == idx:
            return move

    return pick_random_move(legal_moves, placement_bias=1.0)


def make_policy_fn(model, device, temperature=1.0):
    """Create a policy function compatible with play_game's policy_fn parameter."""
    def fn(board, legal_moves, _temperature):
        return policy_move(model, board, legal_moves, device, temperature)
    return fn


def play_game(board, policy_fn=None, temperature=1.0, placement_bias=2.0):
    """
    Play a game to completion on the given board.

    Args:
        board: Board instance (will be mutated)
        policy_fn: None for random play, or fn(board, legal_moves) -> move
        temperature: sampling temperature for policy_fn
        placement_bias: weight multiplier for placement moves in random play

    Returns:
        list of (encoded_board, move_index, active_was_white) tuples,
        and the game result (+1 white wins, -1 black wins, 0 draw)
    """
    records = []
    move_count = 0

    while move_count < MAX_MOVES:
        result = check_victory(board)
        if result is not None:
            return records, result

        legal_moves = board.get_legal_moves()
        if len(legal_moves) == 0:
            # Active player has no moves = they lose
            result = -1 if board.white_is_active else 1
            return records, result

        encoded = encode_board(board)
        active_is_white = board.white_is_active

        if policy_fn is not None:
            move = policy_fn(board, legal_moves, temperature)
        else:
            move = pick_random_move(legal_moves, placement_bias)

        move_idx = move_to_index(move)
        records.append((encoded, move_idx, active_is_white))

        board.do_move(move)
        move_count += 1

    # Move limit reached → draw
    return records, 0


def generate_training_data(boards, games_per_board=10, policy_fn=None,
                           temperature=1.0, placement_bias=2.0, augment=True):
    """
    Generate training data from self-play on multiple boards.

    Args:
        boards: list of Board instances
        games_per_board: number of games to play per board
        policy_fn: None for random, or sampling function
        temperature: for policy sampling
        placement_bias: for random move selection
        augment: whether to apply 180° rotation augmentation

    Returns:
        (board_tensors, move_indices, masks) as numpy arrays
        Only includes positions from the winning side.
    """
    all_boards = []
    all_moves = []
    total_games = 0
    wins = 0
    draws = 0

    for board in boards:
        for _ in range(games_per_board):
            game_board = board.clone()
            records, result = play_game(
                game_board,
                policy_fn=policy_fn,
                temperature=temperature,
                placement_bias=placement_bias,
            )
            total_games += 1

            if result == 0:
                draws += 1
                continue  # discard draws

            wins += 1

            for encoded, move_idx, was_white in records:
                # Keep only winning side's moves
                if (result == 1 and was_white) or (result == -1 and not was_white):
                    all_boards.append(encoded)
                    all_moves.append(move_idx)

                    if augment:
                        aug_enc, aug_idx = augment_180(encoded, move_idx)
                        all_boards.append(aug_enc)
                        all_moves.append(aug_idx)

    if len(all_boards) == 0:
        print(f"Warning: no training data generated ({total_games} games, {draws} draws)")
        return None, None

    board_tensors = np.stack(all_boards)
    move_indices = np.array(all_moves, dtype=np.int64)

    print(f"Generated {len(all_boards)} samples from {wins} decisive games "
          f"({draws} draws discarded, {total_games} total)")

    return board_tensors, move_indices


def save_training_data(filepath, board_tensors, move_indices):
    """Save training data to a .npz file."""
    np.savez_compressed(filepath, boards=board_tensors, moves=move_indices)
    print(f"Saved {len(board_tensors)} samples to {filepath}")


def load_training_data(filepath):
    """Load training data from a .npz file."""
    data = np.load(filepath)
    return data["boards"], data["moves"]
