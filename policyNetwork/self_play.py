import math
import os
import random
import time
import threading
import multiprocessing as mp
import numpy as np
import torch
import torch.nn.functional as F
from board import M, P
from encode import encode_board, move_to_index, get_legal_move_mask, augment_180_multi, NUM_ACTIONS
from config import heuristics
from constants import PIECE_COSTS

MAX_MOVES = 300
GOLD_VICTORY_THRESHOLD = 200  # 10x10 board

# Precompute piece value lookup dictionary
PIECE_VALUES = {p["type"]: p["cost"] for p in PIECE_COSTS}

# Worker globals (set by _worker_init)
_worker_model = None
_worker_opponent_model = None
_worker_device = None
_worker_temperature = None
_worker_noise_weight = 0.0
_worker_gold_victory = False
_worker_n_simulations = 10
_worker_c_puct = 1.5
_worker_gamma = 0.99
_worker_n_pad = 10


# ---------------------------------------------------------------------------
# MCTS
# ---------------------------------------------------------------------------

class MCTSNode:
    __slots__ = ['move', 'prior', 'parent', 'children', 'visit_count', 'value_sum', 'terminal_value']

    def __init__(self, move, prior, parent):
        self.move = move                # move tuple that led to this node
        self.prior = prior              # P(a|s) from policy network
        self.parent = parent
        self.children = {}              # move_index -> MCTSNode
        self.visit_count = 0
        self.value_sum = 0.0
        self.terminal_value = None      # set when node is terminal

    def q(self):
        """Mean value from this node's player's perspective."""
        if self.visit_count == 0:
            return 0.0
        return self.value_sum / self.visit_count

    def puct(self, parent_visits, c_puct):
        """PUCT score used by the parent to select this child."""
        return -self.q() + c_puct * self.prior * math.sqrt(parent_visits) / (1 + self.visit_count)


def check_victory(board, gold_victory=True):
    """
    Check gold/king victory conditions (does NOT call get_legal_moves).
    Returns +1 if white wins, -1 if black wins, None if game continues.
    Mate detection (no legal moves) is handled separately.
    """
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
    else:  # Placement
        score += heuristics["placement"]
        piece = move[1]

        uniformity_penalty = heuristics["uniformity_penalty"]
        if uniformity_penalty > 0 and piece != "q":
            uniformity_penalty_threshold = heuristics["uniformity_penalty_threshold"]
            current_count = board.count_by_piece_type(piece)
            if current_count >= uniformity_penalty_threshold:
                score -= uniformity_penalty * (current_count - uniformity_penalty_threshold + 1)

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


def _filter_moves(legal_moves):
    """Filter pawn placements if disabled in heuristics."""
    if heuristics["pawns_disabled"]:
        filtered = [m for m in legal_moves if not (m[0] == P and m[1] == "p")]
        return filtered if filtered else legal_moves
    return legal_moves


def _expand_node(node, board, model, device, gold_victory):
    """Expand a leaf node using the model.

    Returns (value_from_current_player_perspective, is_terminal).
    For terminal nodes, stores the value in node.terminal_value.
    For non-terminal nodes, populates node.children with prior probabilities.
    """
    # Check terminal conditions
    result = check_victory(board, gold_victory=gold_victory)
    if result is not None:
        active = board.white_is_active
        val = 1.0 if (result == 1 and active) or (result == -1 and not active) else -1.0
        node.terminal_value = val
        return val, True

    legal_moves = board.get_legal_moves()
    if len(legal_moves) == 0:
        node.terminal_value = -1.0  # current player has no moves = loses
        return -1.0, True

    legal_moves = _filter_moves(legal_moves)

    encoded = encode_board(board)
    board_tensor = torch.from_numpy(encoded).unsqueeze(0).to(device)

    with torch.no_grad():
        policy_logits, value = model(board_tensor)

    policy_logits = policy_logits.squeeze(0)
    value = value.item()  # from current player's perspective

    mask = get_legal_move_mask(legal_moves)
    mask_tensor = torch.from_numpy(mask).to(device)
    probs = F.softmax(policy_logits + mask_tensor, dim=0)

    for move in legal_moves:
        idx = move_to_index(move)
        node.children[idx] = MCTSNode(move, probs[idx].item(), parent=node)

    return value, False


def _backprop(path, leaf_value):
    """Backpropagate through path, flipping sign at each level."""
    for node in reversed(path):
        node.visit_count += 1
        node.value_sum += leaf_value
        leaf_value = -leaf_value


def _simulate(root, root_board, model, device, c_puct, gold_victory):
    """Run one MCTS simulation from root."""
    node = root
    board = root_board.clone()
    path = []

    # Selection: traverse to a leaf
    while node.children:
        parent_visits = node.visit_count
        best_idx = max(node.children, key=lambda i: node.children[i].puct(parent_visits, c_puct))
        path.append(node)
        board.do_move(node.children[best_idx].move)
        node = node.children[best_idx]

    path.append(node)

    # Expansion / terminal re-use
    if node.terminal_value is not None:
        leaf_value = node.terminal_value
    else:
        leaf_value, _ = _expand_node(node, board, model, device, gold_victory)

    _backprop(path, leaf_value)


def mcts_search(board, model, device, n_simulations, c_puct, gold_victory):
    """Run MCTS and return {move_index: visit_count} for root's children."""
    root = MCTSNode(None, 0.0, None)
    for _ in range(n_simulations):
        _simulate(root, board, model, device, c_puct, gold_victory)
    return {idx: child.visit_count for idx, child in root.children.items()}


def play_game_mcts(board, model, device, n_simulations=10, c_puct=1.5, gold_victory=False):
    """Play a game using MCTS for move selection.

    Returns:
        records: list of (encoded, visit_dict, active_is_white, move_count) tuples
        result: +1 white wins, -1 black wins, 0 draw
    """
    records = []
    move_count = 0

    while move_count < MAX_MOVES:
        result = check_victory(board, gold_victory=gold_victory)
        if result is not None:
            break

        legal_moves = board.get_legal_moves()
        if len(legal_moves) == 0:
            result = -1 if board.white_is_active else 1
            break

        encoded = encode_board(board)
        active_is_white = board.white_is_active

        visit_dict = mcts_search(board, model, device, n_simulations, c_puct, gold_victory)

        # Sample move proportional to visit counts (tau=1 throughout)
        if not visit_dict:
            move = pick_random_move(legal_moves)
            move_idx = move_to_index(move)
            visit_dict = {move_idx: 1}
        else:
            indices = list(visit_dict.keys())
            counts = np.array([visit_dict[i] for i in indices], dtype=np.float32)
            probs = counts / counts.sum()
            chosen_pos = np.random.choice(len(indices), p=probs)
            move_idx = indices[chosen_pos]
            move = next((m for m in legal_moves if move_to_index(m) == move_idx), None)
            if move is None:
                move = pick_random_move(legal_moves)

        records.append((encoded, visit_dict, active_is_white, move_count))
        board.do_move(move)
        move_count += 1

    if result is None:
        result = 0

    return records, result


def _process_records_to_samples(records, result, gamma, augment, n_pad):
    """Convert game records to training samples.

    Keeps all positions from decisive games (both sides) with discounted value targets.
    Draws are discarded.

    Args:
        records: list of (encoded, visit_dict, active_is_white, move_count)
        result:  +1 white wins, -1 black wins, 0 draw
        gamma:   discount factor (positions near the end are discounted less)
        augment: whether to add 180° augmented samples
        n_pad:   pad policy arrays to this width

    Returns:
        (encoded_list, indices_array, counts_array, value_targets_array)
    """
    if result == 0:
        return [], None, None, None

    encoded_list = []
    indices_rows = []
    counts_rows = []
    value_list = []
    total_moves = len(records)

    for t, (encoded, visit_dict, active_is_white, _) in enumerate(records):
        won = (result == 1 and active_is_white) or (result == -1 and not active_is_white)
        outcome = 1.0 if won else -1.0
        moves_until_end = total_moves - t - 1
        value_target = float(outcome * (gamma ** moves_until_end))

        indices = list(visit_dict.keys())
        counts = [visit_dict[i] for i in indices]

        # Pad to n_pad
        padded_idx = np.full(n_pad, -1, dtype=np.int32)
        padded_cnt = np.zeros(n_pad, dtype=np.int32)
        k = min(len(indices), n_pad)
        if k > 0:
            padded_idx[:k] = indices[:k]
            padded_cnt[:k] = counts[:k]

        encoded_list.append(encoded)
        indices_rows.append(padded_idx)
        counts_rows.append(padded_cnt)
        value_list.append(value_target)

        if augment:
            aug_enc, aug_indices, aug_counts = augment_180_multi(encoded, indices, counts)
            # Value target is from current player's perspective; color swap preserves this
            aug_padded_idx = np.full(n_pad, -1, dtype=np.int32)
            aug_padded_cnt = np.zeros(n_pad, dtype=np.int32)
            k2 = min(len(aug_indices), n_pad)
            if k2 > 0:
                aug_padded_idx[:k2] = aug_indices[:k2]
                aug_padded_cnt[:k2] = aug_counts[:k2]

            encoded_list.append(aug_enc)
            indices_rows.append(aug_padded_idx)
            counts_rows.append(aug_padded_cnt)
            value_list.append(value_target)

    if not encoded_list:
        return [], None, None, None

    return (
        encoded_list,
        np.stack(indices_rows),
        np.stack(counts_rows),
        np.array(value_list, dtype=np.float32),
    )


# ---------------------------------------------------------------------------
# Single-pass policy helpers
# ---------------------------------------------------------------------------

def policy_move(model, board, legal_moves, device, temperature=1.0,
                noise_weight=0.0, encoded=None):
    """Pick a move using the policy network (single-pass, no MCTS)."""
    if heuristics["pawns_disabled"]:
        legal_moves = [m for m in legal_moves if not (m[0] == P and m[1] == "p")]
        if len(legal_moves) == 0:
            legal_moves = board.get_legal_moves()

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


def make_policy_fn(model, device, temperature=1.0, noise_weight=0.0):
    """Create a policy function compatible with play_game's policy_fn parameter."""
    def fn(board, legal_moves, _temperature, encoded=None):
        return policy_move(model, board, legal_moves, device, temperature,
                           noise_weight=noise_weight, encoded=encoded)
    return fn


def play_game(board, policy_fn=None, opponent_fn=None, main_is_white=True,
              temperature=1.0, placement_bias=2.0, gold_victory=True):
    """Play a game to completion (single-pass, no MCTS).

    Returns:
        list of (encoded_board, move_index, active_was_white) tuples,
        and the game result (+1 white wins, -1 black wins, 0 draw)
    """
    records = []
    move_count = 0

    while move_count < MAX_MOVES:
        result = check_victory(board, gold_victory=gold_victory)
        if result is not None:
            break

        legal_moves = board.get_legal_moves()
        if len(legal_moves) == 0:
            result = -1 if board.white_is_active else 1
            break

        encoded = encode_board(board)
        active_is_white = board.white_is_active

        if policy_fn is not None:
            if opponent_fn is not None:
                is_main_turn = (board.white_is_active == main_is_white)
                active_fn = policy_fn if is_main_turn else opponent_fn
            else:
                active_fn = policy_fn
            move = active_fn(board, legal_moves, temperature, encoded)
        else:
            move = pick_random_move(legal_moves, placement_bias)

        move_idx = move_to_index(move)
        records.append((encoded, move_idx, active_is_white))

        board.do_move(move)
        move_count += 1

    if result is None:
        result = 0

    return records, result


# ---------------------------------------------------------------------------
# Non-parallel data generation (heuristic / fallback)
# ---------------------------------------------------------------------------

def generate_training_data(boards, games_per_board=10, policy_fn=None,
                           temperature=1.0, placement_bias=2.0, augment=True,
                           gold_victory=False, gamma=0.99):
    """Generate training data from single-pass self-play on multiple boards.

    Returns:
        (board_tensors, policy_indices, policy_counts, value_targets, game_ids,
         decisive, total_games)
    """
    all_encoded = []
    all_pol_idx = []
    all_pol_cnt = []
    all_val_tgt = []
    all_game_ids = []
    total_games = 0
    decisive = 0
    game_id = 0

    for board in boards:
        for _ in range(games_per_board):
            game_board = board.clone()
            records_flat, result = play_game(
                game_board,
                policy_fn=policy_fn,
                temperature=temperature,
                placement_bias=placement_bias,
                gold_victory=gold_victory,
            )
            total_games += 1

            if result != 0:
                decisive += 1
                # Convert flat records to visit-dict format
                records = [(enc, {mi: 1}, aw, mc)
                           for mc, (enc, mi, aw) in enumerate(records_flat)]
                enc_list, idx_arr, cnt_arr, val_arr = _process_records_to_samples(
                    records, result, gamma, augment, n_pad=1
                )
                if enc_list:
                    all_encoded.extend(enc_list)
                    all_pol_idx.append(idx_arr)
                    all_pol_cnt.append(cnt_arr)
                    all_val_tgt.append(val_arr)
                    all_game_ids.extend([game_id] * len(enc_list))

            game_id += 1

    if not all_encoded:
        print(f"Warning: no training data generated ({total_games} games)")
        return None, None, None, None, None, 0, 0

    board_tensors = np.stack(all_encoded)
    policy_indices = np.concatenate(all_pol_idx, axis=0)
    policy_counts = np.concatenate(all_pol_cnt, axis=0)
    value_targets = np.concatenate(all_val_tgt, axis=0)
    game_ids = np.array(all_game_ids, dtype=np.int32)

    draws = total_games - decisive
    print(f"Generated {len(all_encoded)} samples from {total_games} games "
          f"({decisive} decisive, {draws} draws)")

    return board_tensors, policy_indices, policy_counts, value_targets, game_ids, decisive, total_games


# ---------------------------------------------------------------------------
# Worker functions for parallel data generation
# ---------------------------------------------------------------------------

def _worker_init(model_path, temperature, noise_weight=0.0, gold_victory=False,
                 opponent_path=None, n_simulations=10, c_puct=1.5, gamma=0.99):
    """Initialize a worker process."""
    global _worker_model, _worker_opponent_model, _worker_device
    global _worker_temperature, _worker_noise_weight, _worker_gold_victory
    global _worker_n_simulations, _worker_c_puct, _worker_gamma, _worker_n_pad
    torch.set_num_threads(1)
    from model import PolicyNetwork
    _worker_device = torch.device("cpu")
    _worker_model = PolicyNetwork().to(_worker_device)
    _worker_model.load_state_dict(torch.load(model_path, map_location=_worker_device, weights_only=True))
    _worker_model.eval()
    _worker_temperature = temperature
    _worker_noise_weight = noise_weight
    _worker_gold_victory = gold_victory
    _worker_n_simulations = n_simulations
    _worker_c_puct = c_puct
    _worker_gamma = gamma
    _worker_n_pad = n_simulations
    if opponent_path is not None:
        _worker_opponent_model = PolicyNetwork().to(_worker_device)
        _worker_opponent_model.load_state_dict(
            torch.load(opponent_path, map_location=_worker_device, weights_only=True))
        _worker_opponent_model.eval()
    else:
        _worker_opponent_model = None


def _worker_play_boards(args):
    """Worker function: play games on a chunk of boards.

    Uses MCTS when no opponent model is set (self-play), or single-pass when
    an opponent model is set (pool games).
    """
    board_chunk, games_per_board, augment = args
    global _worker_model, _worker_opponent_model, _worker_device
    global _worker_temperature, _worker_noise_weight, _worker_gold_victory
    global _worker_n_simulations, _worker_c_puct, _worker_gamma, _worker_n_pad

    use_mcts = (_worker_opponent_model is None)

    all_encoded = []
    all_pol_idx = []
    all_pol_cnt = []
    all_val_tgt = []
    all_game_ids = []
    decisive = 0
    draws = 0
    game_id = 0

    if not use_mcts:
        policy_fn = make_policy_fn(_worker_model, _worker_device, _worker_temperature,
                                   noise_weight=_worker_noise_weight)
        opponent_fn = make_policy_fn(_worker_opponent_model, _worker_device,
                                     _worker_temperature, noise_weight=0.0)

    for board in board_chunk:
        for _ in range(games_per_board):
            game_board = board.clone()

            if use_mcts:
                records, result = play_game_mcts(
                    game_board,
                    _worker_model,
                    _worker_device,
                    n_simulations=_worker_n_simulations,
                    c_puct=_worker_c_puct,
                    gold_victory=_worker_gold_victory,
                )
            else:
                records_flat, result = play_game(
                    game_board,
                    policy_fn=policy_fn,
                    opponent_fn=opponent_fn,
                    main_is_white=(game_id % 2 == 0),
                    temperature=_worker_temperature,
                    gold_victory=_worker_gold_victory,
                )
                # Convert flat records to visit-dict format
                records = [(enc, {mi: 1}, aw, mc)
                           for mc, (enc, mi, aw) in enumerate(records_flat)]

            if result == 0:
                draws += 1
            else:
                decisive += 1
                enc_list, idx_arr, cnt_arr, val_arr = _process_records_to_samples(
                    records, result, _worker_gamma, augment, n_pad=_worker_n_pad
                )
                if enc_list:
                    all_encoded.extend(enc_list)
                    all_pol_idx.append(idx_arr)
                    all_pol_cnt.append(cnt_arr)
                    all_val_tgt.append(val_arr)
                    all_game_ids.extend([game_id] * len(enc_list))

            game_id += 1

    total = decisive + draws

    if not all_encoded:
        return [], None, None, None, [], decisive, draws, total

    board_tensors = np.stack(all_encoded)
    policy_indices = np.concatenate(all_pol_idx, axis=0)
    policy_counts = np.concatenate(all_pol_cnt, axis=0)
    value_targets = np.concatenate(all_val_tgt, axis=0)

    return board_tensors, policy_indices, policy_counts, value_targets, all_game_ids, decisive, draws, total


def _timer_thread(stop_event):
    """Background thread that prints elapsed time every minute."""
    start_time = time.time()
    print("  0", end='', flush=True)

    while not stop_event.is_set():
        time.sleep(60)
        if stop_event.is_set():
            break

        elapsed_min = int((time.time() - start_time) / 60)
        if elapsed_min % 5 == 0:
            print(f"{elapsed_min}", end='', flush=True)
        else:
            print("-", end='', flush=True)

    print()


def generate_training_data_parallel(boards, model_path, games_per_board=1,
                                     augment=True, workers=None,
                                     gold_victory=False,
                                     n_simulations=10, c_puct=1.5, gamma=0.99,
                                     opponent_path=None,
                                     temperature=1.0, noise_weight=0.0):
    """Generate training data using multiple worker processes.

    When opponent_path is None: uses MCTS self-play (model vs itself).
    When opponent_path is set: uses single-pass play (model vs pool opponent).

    Returns:
        (board_tensors, policy_indices, policy_counts, value_targets, game_ids,
         decisive, total_games)
    """
    if workers is None:
        workers = max(1, os.cpu_count() - 2)

    n = len(boards)
    chunks = [boards[i * n // workers:(i + 1) * n // workers] for i in range(workers)]
    chunks = [c for c in chunks if c]

    print(f"  Using {workers} workers, {len(chunks)} chunks")

    os.environ.setdefault("OMP_NUM_THREADS", "1")
    os.environ.setdefault("MKL_NUM_THREADS", "1")

    ctx = mp.get_context("spawn")
    pool = ctx.Pool(
        workers,
        initializer=_worker_init,
        initargs=(model_path, temperature, noise_weight, gold_victory,
                  opponent_path, n_simulations, c_puct, gamma),
    )

    stop_event = threading.Event()
    timer = threading.Thread(target=_timer_thread, args=(stop_event,), daemon=True)
    timer.start()

    try:
        results = pool.map(_worker_play_boards,
                           [(chunk, games_per_board, augment) for chunk in chunks])
    finally:
        stop_event.set()
        timer.join(timeout=1.0)
        pool.close()
        pool.join()

    # Merge results
    all_encoded = []
    all_pol_idx = []
    all_pol_cnt = []
    all_val_tgt = []
    all_game_ids = []
    total_decisive = 0
    total_draws = 0
    total_games = 0
    game_id_offset = 0

    for bt, pi, pc, vt, gids, decisive, draws, total in results:
        if bt is not None and len(bt) > 0:
            all_encoded.append(bt)
            all_pol_idx.append(pi)
            all_pol_cnt.append(pc)
            all_val_tgt.append(vt)
            all_game_ids.extend([gid + game_id_offset for gid in gids])
        total_decisive += decisive
        total_draws += draws
        total_games += total
        if gids:
            game_id_offset += max(gids) + 1

    if not all_encoded:
        print(f"\nWarning: no training data generated ({total_games} games)")
        return None, None, None, None, None, 0, 0

    # Pad policy arrays to the same width before concatenating
    max_width = max(a.shape[1] for a in all_pol_idx)
    padded_pi = []
    padded_pc = []
    for pi, pc in zip(all_pol_idx, all_pol_cnt):
        w = pi.shape[1]
        if w < max_width:
            pi = np.pad(pi, ((0, 0), (0, max_width - w)), constant_values=-1)
            pc = np.pad(pc, ((0, 0), (0, max_width - w)), constant_values=0)
        padded_pi.append(pi)
        padded_pc.append(pc)

    board_tensors = np.concatenate(all_encoded, axis=0)
    policy_indices = np.concatenate(padded_pi, axis=0)
    policy_counts = np.concatenate(padded_pc, axis=0)
    value_targets = np.concatenate(all_val_tgt, axis=0)
    game_ids = np.array(all_game_ids, dtype=np.int32)

    print(f"\nGenerated {len(board_tensors)} samples from {total_games} games "
          f"({total_decisive} decisive, {total_draws} draws)")

    return board_tensors, policy_indices, policy_counts, value_targets, game_ids, total_decisive, total_games


# ---------------------------------------------------------------------------
# Persistence
# ---------------------------------------------------------------------------

def save_training_data(filepath, board_tensors, policy_indices, policy_counts,
                       value_targets, game_ids=None):
    """Save training data to a .npz file."""
    arrays = dict(
        boards=board_tensors,
        policy_indices=policy_indices,
        policy_counts=policy_counts,
        value_targets=value_targets,
    )
    if game_ids is not None:
        arrays["game_ids"] = game_ids
    np.savez_compressed(filepath, **arrays)
    print(f"Saved {len(board_tensors)} samples to {filepath}")


def load_training_data(filepath):
    """Load training data from a .npz file."""
    data = np.load(filepath)
    game_ids = data["game_ids"] if "game_ids" in data else None
    return (data["boards"], data["policy_indices"], data["policy_counts"],
            data["value_targets"], game_ids)
