import os
import random
import multiprocessing as mp
import numpy as np
import torch
import torch.nn.functional as F
from board import M, P
from encode import encode_board, move_to_index, get_legal_move_mask, augment_180, NUM_ACTIONS

MAX_MOVES = 300
GOLD_VICTORY_THRESHOLD = 175  # 10x10 board

# Worker globals (set by _worker_init)
_worker_model = None
_worker_device = None
_worker_temperature = None
_worker_noise_weight = 0.0


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


PIECE_VALUES = {"q": 90, "r": 52, "b": 42, "n": 40, "p": 25, "k": 0}
MINE_INCOME = 3


def _heuristic_score(board, move):
    """Score a move by simple heuristics (higher = more interesting).
    Prioritizes captures, mine-taking, and treasure collection."""
    score = 0.0

    if move[0] == M:
        dest_sq = board.squares[move[2]]
        # Capturing an enemy piece
        if dest_sq.piece_type is not None and dest_sq.piece_is_white != board.white_is_active:
            score += PIECE_VALUES.get(dest_sq.piece_type, 0)
        # Taking a treasure
        if dest_sq.has_treasure:
            score += 20
        # Taking/stealing a mine
        if dest_sq.terrain_type == "m":
            already_owned = (dest_sq.owned_by == 0 and board.white_is_active) or \
                            (dest_sq.owned_by == 1 and not board.white_is_active)
            if not already_owned:
                score += 15  # mine income is very valuable
    else:
        # Placement — neutral for pieces, strongly discourage pawns
        piece = move[1]
        if piece == "p":
            score -= 100  # Strong penalty for pawns
        # else: neutral (no bonus for placing other pieces)

        # Placing on a mine is valuable
        dest_sq = board.squares[move[2]]
        if dest_sq.terrain_type == "m":
            already_owned = (dest_sq.owned_by == 0 and board.white_is_active) or \
                            (dest_sq.owned_by == 1 and not board.white_is_active)
            if not already_owned:
                score += 15

    return score


def policy_move(model, board, legal_moves, device, temperature=1.0,
                noise_weight=0.0, apply_pawn_cap=True):
    """Pick a move using the policy network.

    Args:
        noise_weight: Fraction of heuristic noise to mix into policy (0-1).
        apply_pawn_cap: If True, filter out pawn placements (for self-play data generation).
                        If False, allow all legal moves (for evaluation).
    """
    # Enforce pawn limit: max 0 pawns per side (completely disabled during self-play)
    if apply_pawn_cap:
        pawn_count = sum(1 for sq in board.squares
                         if sq.piece_type == "p" and sq.piece_is_white == board.white_is_active)
        if pawn_count >= 0:
            # Filter out pawn placements from legal moves
            legal_moves = [m for m in legal_moves if not (m[0] == P and m[1] == "p")]
            if len(legal_moves) == 0:
                # Fallback if filtering removes all moves (shouldn't happen)
                legal_moves = board.get_legal_moves()

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

        # Mix in heuristic-based noise for exploration
        if noise_weight > 0:
            heuristic_logits = torch.full((probs.shape[0],), -1e9, device=device)
            for move in legal_moves:
                mi = move_to_index(move)
                heuristic_logits[mi] = _heuristic_score(board, move)
            heuristic_probs = F.softmax(heuristic_logits, dim=0)
            probs = (1 - noise_weight) * probs + noise_weight * heuristic_probs

        idx = torch.multinomial(probs, 1).item()

    for move in legal_moves:
        if move_to_index(move) == idx:
            return move

    return pick_random_move(legal_moves, placement_bias=1.0)


def make_policy_fn(model, device, temperature=1.0, noise_weight=0.0, apply_pawn_cap=True):
    """Create a policy function compatible with play_game's policy_fn parameter."""
    def fn(board, legal_moves, _temperature):
        return policy_move(model, board, legal_moves, device, temperature,
                           noise_weight=noise_weight, apply_pawn_cap=apply_pawn_cap)
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
    all_game_ids = []
    total_games = 0
    wins = 0
    draws = 0
    game_id = 0

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
                    all_game_ids.append(game_id)

                    if augment:
                        aug_enc, aug_idx = augment_180(encoded, move_idx)
                        all_boards.append(aug_enc)
                        all_moves.append(aug_idx)
                        all_game_ids.append(game_id)

            game_id += 1

    if len(all_boards) == 0:
        print(f"Warning: no training data generated ({total_games} games, {draws} draws)")
        return None, None, None

    board_tensors = np.stack(all_boards)
    move_indices = np.array(all_moves, dtype=np.int64)
    game_ids = np.array(all_game_ids, dtype=np.int32)

    print(f"Generated {len(all_boards)} samples from {wins} decisive games "
          f"({draws} draws discarded, {total_games} total)")

    return board_tensors, move_indices, game_ids


def _worker_init(model_path, temperature, noise_weight=0.0):
    """Initialize a worker process with its own model."""
    global _worker_model, _worker_device, _worker_temperature, _worker_noise_weight
    # Limit intra-op threads per worker to avoid OpenMP deadlock on Linux
    torch.set_num_threads(1)
    from model import PolicyNetwork
    _worker_device = torch.device("cpu")  # workers always use CPU
    _worker_model = PolicyNetwork().to(_worker_device)
    _worker_model.load_state_dict(torch.load(model_path, map_location=_worker_device))
    _worker_model.eval()
    _worker_temperature = temperature
    _worker_noise_weight = noise_weight


def _worker_play_boards(args):
    """Worker function: play games on a chunk of boards."""
    board_chunk, games_per_board, augment = args
    global _worker_model, _worker_device, _worker_temperature, _worker_noise_weight

    policy_fn = make_policy_fn(_worker_model, _worker_device, _worker_temperature,
                               noise_weight=_worker_noise_weight)

    all_boards = []
    all_moves = []
    all_game_ids = []
    wins = 0
    draws = 0
    game_id = 0

    for board in board_chunk:
        for _ in range(games_per_board):
            game_board = board.clone()
            records, result = play_game(
                game_board,
                policy_fn=policy_fn,
                temperature=_worker_temperature,
            )

            if result == 0:
                draws += 1
                continue

            wins += 1
            for encoded, move_idx, was_white in records:
                if (result == 1 and was_white) or (result == -1 and not was_white):
                    all_boards.append(encoded)
                    all_moves.append(move_idx)
                    all_game_ids.append(game_id)

                    if augment:
                        aug_enc, aug_idx = augment_180(encoded, move_idx)
                        all_boards.append(aug_enc)
                        all_moves.append(aug_idx)
                        all_game_ids.append(game_id)

            game_id += 1

    total = wins + draws
    return all_boards, all_moves, all_game_ids, wins, draws, total


def generate_training_data_parallel(boards, model_path, games_per_board=1,
                                     temperature=1.0, augment=True, workers=None,
                                     noise_weight=0.0):
    """Generate training data using multiple worker processes."""
    if workers is None:
        workers = max(1, os.cpu_count() - 2)

    # Split boards into chunks
    chunk_size = max(1, len(boards) // workers)
    chunks = []
    for i in range(0, len(boards), chunk_size):
        chunks.append(boards[i:i + chunk_size])

    print(f"  Using {workers} workers, {len(chunks)} chunks")

    # Limit OpenMP/MKL threads so spawned workers don't deadlock on Linux
    os.environ.setdefault("OMP_NUM_THREADS", "1")
    os.environ.setdefault("MKL_NUM_THREADS", "1")

    # Use 'spawn' to avoid deadlock from CUDA state inherited via fork
    ctx = mp.get_context("spawn")
    with ctx.Pool(workers, initializer=_worker_init,
                  initargs=(model_path, temperature, noise_weight)) as pool:
        results = pool.map(_worker_play_boards,
                           [(chunk, games_per_board, augment) for chunk in chunks])

    # Merge results
    all_boards = []
    all_moves = []
    all_game_ids = []
    total_wins = 0
    total_draws = 0
    total_games = 0
    game_id_offset = 0

    for boards_chunk, moves_chunk, gids_chunk, wins, draws, total in results:
        all_boards.extend(boards_chunk)
        all_moves.extend(moves_chunk)
        all_game_ids.extend([gid + game_id_offset for gid in gids_chunk])
        total_wins += wins
        total_draws += draws
        total_games += total
        if len(gids_chunk) > 0:
            game_id_offset += max(gids_chunk) + 1

    if len(all_boards) == 0:
        print(f"Warning: no training data generated ({total_games} games, {total_draws} draws)")
        return None, None, None

    board_tensors = np.stack(all_boards)
    move_indices = np.array(all_moves, dtype=np.int64)
    game_ids = np.array(all_game_ids, dtype=np.int32)

    print(f"Generated {len(all_boards)} samples from {total_wins} decisive games "
          f"({total_draws} draws discarded, {total_games} total)")

    return board_tensors, move_indices, game_ids


def save_training_data(filepath, board_tensors, move_indices, game_ids=None):
    """Save training data to a .npz file."""
    if game_ids is not None:
        np.savez_compressed(filepath, boards=board_tensors, moves=move_indices, game_ids=game_ids)
    else:
        np.savez_compressed(filepath, boards=board_tensors, moves=move_indices)
    print(f"Saved {len(board_tensors)} samples to {filepath}")


def load_training_data(filepath):
    """Load training data from a .npz file."""
    data = np.load(filepath)
    game_ids = data["game_ids"] if "game_ids" in data else None
    return data["boards"], data["moves"], game_ids
