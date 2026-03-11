import os
import random
import time
import threading
import multiprocessing as mp
import numpy as np
import torch
import torch.nn.functional as F
from board import M, P
from encode import encode_board, move_to_index, get_legal_move_mask, augment_180, NUM_ACTIONS
from config import heuristics
from constants import PIECE_COSTS

MAX_MOVES = 300
GOLD_VICTORY_THRESHOLD = 200  # 10x10 board

# Precompute piece value lookup dictionary
PIECE_VALUES = {p["type"]: p["cost"] for p in PIECE_COSTS}

# Worker globals (set by _worker_init)
_worker_model = None
_worker_device = None
_worker_temperature = None
_worker_noise_weight = 0.0
_worker_gold_victory = False


def check_victory(board, gold_victory=True):
    """
    Check gold/king victory conditions (does NOT call get_legal_moves).
    Returns +1 if white wins, -1 if black wins, None if game continues.
    Mate detection (no legal moves) is handled in play_game.
    """
    # Gold victory (disabled during training)
    if gold_victory:
        if board.white_gold >= GOLD_VICTORY_THRESHOLD:
            return 1
        if board.black_gold >= GOLD_VICTORY_THRESHOLD:
            return -1

    return None


def pick_random_move(legal_moves, placement_bias=2.0):
    """Pick a random move with optional bias toward placements."""
    weights = [placement_bias if m[0] == P else 1.0 for m in legal_moves]
    return random.choices(legal_moves, weights=weights, k=1)[0]

MINE_INCOME = 3


def _heuristic_score(board, move):
    """Score a move by simple heuristics (higher = more interesting).
    Prioritizes captures, mine-taking, and treasure collection."""
    score = 0.0

    if move[0] == M:
        pass
        dest_sq = board.squares[move[2]]
        # Capturing an enemy piece
        if dest_sq.piece_type is not None and dest_sq.piece_is_white != board.white_is_active:
            score += heuristics["enemy_capture"]
            score += heuristics["enemy_capture_piece_value"] * PIECE_VALUES.get(dest_sq.piece_type, 0)
        # Taking a treasure
        if dest_sq.has_treasure:
            score += heuristics["treasure_capture"]
        # Taking/stealing a mine
        if dest_sq.terrain_type == "m":
            already_owned = (dest_sq.owned_by == 0 and board.white_is_active) or \
                            (dest_sq.owned_by == 1 and not board.white_is_active)
            if not already_owned:
                score += heuristics["mine_capture"]
    else: # Placement
        score += heuristics["placement"]
        piece = move[1]
        if piece == "p":
            score += heuristics["pawn"]
        if piece == "n":
            score += heuristics["knight"]
        if piece == "b":
            score += heuristics["bishop"]
        if piece == "r":
            score += heuristics["rook"]
        if piece == "q":
            score += heuristics["queen"]

        # Placing on a mine is valuable
        dest_sq = board.squares[move[2]]
        if dest_sq.terrain_type == "m":
            already_owned = (dest_sq.owned_by == 0 and board.white_is_active) or \
                            (dest_sq.owned_by == 1 and not board.white_is_active)
            if not already_owned:
                score += heuristics["mine_capture"]

    return score


def policy_move(model, board, legal_moves, device, temperature=1.0,
                noise_weight=0.0, use_value_lookahead=False, encoded=None):
    """Pick a move using the policy network.

    Args:
        noise_weight: Fraction of heuristic noise to mix into policy (0-1).
        use_value_lookahead: If True, evaluate each legal move with the value head
                             and pick the best (1-ply lookahead). Overrides temperature sampling.
        encoded: Pre-computed encode_board() result; if None, will encode internally.
    """
    # Filter out pawn placements during self-play to prevent model from learning pawn spam
    if heuristics["pawns_disabled"]:
        legal_moves = [m for m in legal_moves if not (m[0] == P and m[1] == "p")]

        if len(legal_moves) == 0:
            # Safety: if somehow all moves were pawns, allow them (shouldn't happen)
            legal_moves = board.get_legal_moves()

    # 1-ply value lookahead: evaluate all moves in a single batched forward pass
    if use_value_lookahead:
        encs = []
        for move in legal_moves:
            test_board = board.clone()
            test_board.do_move(move)
            encs.append(encode_board(test_board))
        batch = torch.from_numpy(np.stack(encs)).to(device)
        with torch.no_grad():
            _, values = model(batch)
        # Negate: after our move it's opponent's turn; their good = our bad
        best_idx = (-values.squeeze(1)).argmax().item()
        return legal_moves[best_idx]

    # Standard policy sampling
    if encoded is None:
        encoded = encode_board(board)
    board_tensor = torch.from_numpy(encoded).unsqueeze(0).to(device)

    with torch.no_grad():
        policy_logits, _ = model(board_tensor)
    logits = policy_logits.squeeze(0)

    mask = get_legal_move_mask(legal_moves)
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


def make_heuristic_fn():
    """Heuristic-only policy: sample moves weighted by heuristic scores."""
    def fn(board, legal_moves, _temperature, _encoded=None):
        scores = [max(0.01, _heuristic_score(board, m) + 0.1) for m in legal_moves]
        return random.choices(legal_moves, weights=scores, k=1)[0]
    return fn


def make_policy_fn(model, device, temperature=1.0, noise_weight=0.0,
                   use_value_lookahead=False):
    """Create a policy function compatible with play_game's policy_fn parameter."""
    def fn(board, legal_moves, _temperature, encoded=None):
        return policy_move(model, board, legal_moves, device, temperature,
                           noise_weight=noise_weight,
                           use_value_lookahead=use_value_lookahead,
                           encoded=encoded)
    return fn


def play_game(board, policy_fn=None, temperature=1.0, placement_bias=2.0,
              gold_victory=True):
    """
    Play a game to completion on the given board.

    Args:
        board: Board instance (will be mutated)
        policy_fn: None for random play, or fn(board, legal_moves) -> move
        temperature: sampling temperature for policy_fn
        placement_bias: weight multiplier for placement moves in random play
        gold_victory: if False, gold threshold victory is disabled (training mode)

    Returns:
        list of (encoded_board, move_index, active_was_white) tuples,
        and the game result (+1 white wins, -1 black wins, 0 draw)
    """
    records = []
    move_count = 0

    while move_count < MAX_MOVES:
        result = check_victory(board, gold_victory=gold_victory)
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
            move = policy_fn(board, legal_moves, temperature, encoded)
        else:
            move = pick_random_move(legal_moves, placement_bias)

        move_idx = move_to_index(move)
        records.append((encoded, move_idx, active_is_white))

        board.do_move(move)
        move_count += 1

    # Move limit reached → draw
    return records, 0


def _outcome_for_position(result, was_white):
    """Compute outcome label from active player's perspective."""
    if result == 1:
        return 1.0 if was_white else -1.0
    elif result == -1:
        return -1.0 if was_white else 1.0
    else:
        return 0.0


def generate_training_data(boards, games_per_board=10, policy_fn=None,
                           temperature=1.0, placement_bias=2.0, augment=True,
                           gold_victory=True):
    """
    Generate training data from self-play on multiple boards.

    Returns:
        (board_tensors, move_indices, outcome_labels, game_ids) as numpy arrays
        Includes positions from BOTH sides with outcome labels (+1/-1/0).
    """
    all_boards = []
    all_moves = []
    all_outcomes = []
    all_game_ids = []
    total_games = 0
    decisive = 0
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
                gold_victory=gold_victory,
            )
            total_games += 1

            if result == 0:
                draws += 1

            if result != 0:
                decisive += 1

            for encoded, move_idx, was_white in records:
                outcome = _outcome_for_position(result, was_white)
                all_boards.append(encoded)
                all_moves.append(move_idx)
                all_outcomes.append(outcome)
                all_game_ids.append(game_id)

                if augment:
                    aug_enc, aug_idx = augment_180(encoded, move_idx)
                    all_boards.append(aug_enc)
                    all_moves.append(aug_idx)
                    all_outcomes.append(outcome)
                    all_game_ids.append(game_id)

            game_id += 1

    if len(all_boards) == 0:
        print(f"Warning: no training data generated ({total_games} games)")
        return None, None, None, None

    board_tensors = np.stack(all_boards)
    move_indices = np.array(all_moves, dtype=np.int64)
    outcome_labels = np.array(all_outcomes, dtype=np.float32)
    game_ids = np.array(all_game_ids, dtype=np.int32)

    print(f"Generated {len(all_boards)} samples from {total_games} games "
          f"({decisive} decisive, {draws} draws)")

    return board_tensors, move_indices, outcome_labels, game_ids


def _worker_init(model_path, temperature, noise_weight=0.0, gold_victory=False):
    """Initialize a worker process with its own model."""
    global _worker_model, _worker_device, _worker_temperature, _worker_noise_weight, _worker_gold_victory
    # Limit intra-op threads per worker to avoid OpenMP deadlock on Linux
    torch.set_num_threads(1)
    from model import PolicyNetwork
    _worker_device = torch.device("cpu")  # workers always use CPU
    _worker_model = PolicyNetwork().to(_worker_device)
    _worker_model.load_state_dict(torch.load(model_path, map_location=_worker_device))
    _worker_model.eval()
    _worker_temperature = temperature
    _worker_noise_weight = noise_weight
    _worker_gold_victory = gold_victory


def _worker_play_boards(args):
    """Worker function: play games on a chunk of boards."""
    board_chunk, games_per_board, augment = args
    global _worker_model, _worker_device, _worker_temperature, _worker_noise_weight, _worker_gold_victory

    policy_fn = make_policy_fn(_worker_model, _worker_device, _worker_temperature,
                               noise_weight=_worker_noise_weight)

    all_boards = []
    all_moves = []
    all_outcomes = []
    all_game_ids = []
    decisive = 0
    draws = 0
    game_id = 0

    for board in board_chunk:
        for _ in range(games_per_board):
            game_board = board.clone()
            records, result = play_game(
                game_board,
                policy_fn=policy_fn,
                temperature=_worker_temperature,
                gold_victory=_worker_gold_victory,
            )

            if result == 0:
                draws += 1
            else:
                decisive += 1

            for encoded, move_idx, was_white in records:
                outcome = _outcome_for_position(result, was_white)
                all_boards.append(encoded)
                all_moves.append(move_idx)
                all_outcomes.append(outcome)
                all_game_ids.append(game_id)

                if augment:
                    aug_enc, aug_idx = augment_180(encoded, move_idx)
                    all_boards.append(aug_enc)
                    all_moves.append(aug_idx)
                    all_outcomes.append(outcome)
                    all_game_ids.append(game_id)

            game_id += 1

    total = decisive + draws
    return all_boards, all_moves, all_outcomes, all_game_ids, decisive, draws, total


def _timer_thread(stop_event):
    """Background thread that prints elapsed time every minute."""
    start_time = time.time()
    print("  0", end='', flush=True)

    while not stop_event.is_set():
        time.sleep(60)  # Wait 1 minute
        if stop_event.is_set():
            break

        elapsed_min = int((time.time() - start_time) / 60)
        if elapsed_min % 5 == 0:
            print(f"{elapsed_min}", end='', flush=True)
        else:
            print("-", end='', flush=True)

    print()  # Newline when done


def generate_training_data_parallel(boards, model_path, games_per_board=1,
                                     temperature=1.0, augment=True, workers=None,
                                     noise_weight=0.0, gold_victory=False):
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

    # Start timer thread
    stop_event = threading.Event()
    timer = threading.Thread(target=_timer_thread, args=(stop_event,), daemon=True)
    timer.start()

    # Use 'spawn' to avoid deadlock from CUDA state inherited via fork
    ctx = mp.get_context("spawn")
    with ctx.Pool(workers, initializer=_worker_init,
                  initargs=(model_path, temperature, noise_weight, gold_victory)) as pool:
        results = pool.map(_worker_play_boards,
                           [(chunk, games_per_board, augment) for chunk in chunks])

    # Stop timer thread
    stop_event.set()
    timer.join(timeout=1.0)

    # Merge results
    all_boards = []
    all_moves = []
    all_outcomes = []
    all_game_ids = []
    total_decisive = 0
    total_draws = 0
    total_games = 0
    game_id_offset = 0

    for boards_chunk, moves_chunk, outcomes_chunk, gids_chunk, decisive, draws, total in results:
        all_boards.extend(boards_chunk)
        all_moves.extend(moves_chunk)
        all_outcomes.extend(outcomes_chunk)
        all_game_ids.extend([gid + game_id_offset for gid in gids_chunk])
        total_decisive += decisive
        total_draws += draws
        total_games += total
        if len(gids_chunk) > 0:
            game_id_offset += max(gids_chunk) + 1

    if len(all_boards) == 0:
        print(f"\nWarning: no training data generated ({total_games} games)")
        return None, None, None, None

    board_tensors = np.stack(all_boards)
    move_indices = np.array(all_moves, dtype=np.int64)
    outcome_labels = np.array(all_outcomes, dtype=np.float32)
    game_ids = np.array(all_game_ids, dtype=np.int32)

    print(f"\nGenerated {len(all_boards)} samples from {total_games} games "
          f"({total_decisive} decisive, {total_draws} draws)")

    return board_tensors, move_indices, outcome_labels, game_ids


def save_training_data(filepath, board_tensors, move_indices, outcome_labels, game_ids=None):
    """Save training data to a .npz file."""
    arrays = dict(boards=board_tensors, moves=move_indices, outcomes=outcome_labels)
    if game_ids is not None:
        arrays["game_ids"] = game_ids
    np.savez_compressed(filepath, **arrays)
    print(f"Saved {len(board_tensors)} samples to {filepath}")


def load_training_data(filepath):
    """Load training data from a .npz file."""
    data = np.load(filepath)
    game_ids = data["game_ids"] if "game_ids" in data else None
    outcomes = data["outcomes"] if "outcomes" in data else None
    return data["boards"], data["moves"], outcomes, game_ids
