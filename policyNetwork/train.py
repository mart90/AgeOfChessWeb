import argparse
import os
import threading
import time
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
from torch.utils.data import TensorDataset, DataLoader

from model import PolicyNetwork
from encode import NUM_PLANES, BOARD_SIZE
from self_play import generate_training_data, save_training_data, load_training_data
from generate_boards import fetch_boards


def _timer_thread(stop_event):
    """Background thread that prints elapsed time every minute."""
    start_time = time.time()
    print("    0", end='', flush=True)
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


def train_epoch(model, dataloader, optimizer, device, train_value=True):
    model.train()
    total_policy_loss = 0
    total_value_loss = 0
    total_correct = 0
    total_decisive = 0
    total_samples = 0
    large_grad_count = 0
    nan_count = 0

    stop_event = threading.Event()
    timer = threading.Thread(target=_timer_thread, args=(stop_event,), daemon=True)
    timer.start()

    for boards, moves, outcomes in dataloader:
        boards = boards.to(device)
        moves = moves.to(device)
        outcomes = outcomes.to(device)

        policy_logits, value = model(boards)

        # Policy loss only on decisive positions (outcome != 0)
        decisive_mask = outcomes != 0
        if decisive_mask.any():
            policy_loss = F.cross_entropy(policy_logits[decisive_mask], moves[decisive_mask])
            if train_value:
                value_loss = F.mse_loss(value.squeeze(1), outcomes)
                loss = policy_loss + value_loss
            else:
                value_loss = torch.tensor(0.0, device=device)
                loss = policy_loss
        else:
            if not train_value:
                continue
            policy_loss = torch.tensor(0.0, device=device)
            value_loss = F.mse_loss(value.squeeze(1), outcomes)
            loss = value_loss

        # Check for NaN/inf loss
        if not torch.isfinite(loss):
            print(f"    WARNING: Non-finite loss detected, skipping batch")
            nan_count += 1
            continue

        optimizer.zero_grad()
        loss.backward()

        # Clip gradients and log when large
        grad_norm = torch.nn.utils.clip_grad_norm_(model.parameters(), max_norm=20.0)
        if grad_norm > 20.0:
            large_grad_count += 1
            print(f"      Batch with grad_norm={grad_norm:.1f} (clipped to 20.0)")

        optimizer.step()

        n = boards.size(0)
        nd = decisive_mask.sum().item()
        total_policy_loss += policy_loss.item() * nd
        total_value_loss += value_loss.item() * n
        preds = policy_logits.argmax(dim=1)
        total_correct += (preds[decisive_mask] == moves[decisive_mask]).sum().item()
        total_decisive += nd
        total_samples += n

    stop_event.set()
    timer.join()

    if large_grad_count > 0:
        print(f"    Clipped large gradients in {large_grad_count} batches")
    if nan_count > 0:
        print(f"    Skipped {nan_count} batches due to NaN/inf loss")

    policy_acc = total_correct / total_decisive if total_decisive > 0 else 0.0
    return (policy_acc,
            total_policy_loss / total_decisive if total_decisive > 0 else 0.0,
            total_value_loss / total_samples)


def evaluate(model, dataloader, device):
    model.eval()
    total_loss = 0
    total_policy_loss = 0
    total_value_loss = 0
    total_correct = 0
    total_decisive = 0
    total_samples = 0
    value_sign_correct = 0

    with torch.no_grad():
        for boards, moves, outcomes in dataloader:
            boards = boards.to(device)
            moves = moves.to(device)
            outcomes = outcomes.to(device)

            policy_logits, value = model(boards)
            value_loss = F.mse_loss(value.squeeze(1), outcomes)

            decisive_mask = outcomes != 0
            if decisive_mask.any():
                policy_loss = F.cross_entropy(policy_logits[decisive_mask], moves[decisive_mask])
                loss = policy_loss + value_loss
            else:
                policy_loss = torch.tensor(0.0, device=device)
                loss = value_loss

            n = boards.size(0)
            nd = decisive_mask.sum().item()
            total_loss += loss.item() * n
            total_policy_loss += policy_loss.item() * nd
            total_value_loss += value_loss.item() * n
            preds = policy_logits.argmax(dim=1)
            total_correct += (preds[decisive_mask] == moves[decisive_mask]).sum().item()
            total_decisive += nd
            total_samples += n

            if decisive_mask.any():
                v = value.squeeze(1)[decisive_mask]
                o = outcomes[decisive_mask]
                value_sign_correct += (v.sign() == o.sign()).sum().item()

    policy_acc = total_correct / total_decisive if total_decisive > 0 else 0.0
    value_sign_acc = value_sign_correct / total_decisive if total_decisive > 0 else None
    return (total_loss / total_samples,
            policy_acc,
            total_policy_loss / total_decisive if total_decisive > 0 else 0.0,
            total_value_loss / total_samples,
            value_sign_acc)


def export_onnx(model, filepath, device):
    """Export only the policy head (value head not needed for deployment)."""
    model.eval()

    class PolicyOnly(nn.Module):
        def __init__(self, m):
            super().__init__()
            self.m = m
        def forward(self, x):
            policy, _ = self.m(x)
            return policy

    wrapper = PolicyOnly(model).to(device)
    dummy = torch.randn(1, NUM_PLANES, BOARD_SIZE, BOARD_SIZE, device=device)
    torch.onnx.export(
        wrapper, dummy, filepath,
        input_names=["board"],
        output_names=["policy"],
        dynamic_axes={"board": {0: "batch"}, "policy": {0: "batch"}},
        opset_version=18,
        dynamo=False,
    )
    size_mb = os.path.getsize(filepath) / (1024 * 1024)
    print(f"Exported ONNX model to {filepath} ({size_mb:.1f} MB)")


def run_training(board_tensors, move_indices, device, outcome_labels=None,
                 game_ids=None, model=None, epochs=20, batch_size=256, lr=1e-3,
                 patience=2, save_path=None):
    """
    Train a policy+value network on the given data with early stopping.

    Returns (model, best_val_loss) with the best model weights loaded.
    """
    boards_t = torch.from_numpy(board_tensors)
    moves_t = torch.from_numpy(move_indices)

    if outcome_labels is not None:
        outcomes_t = torch.from_numpy(outcome_labels).float()
    else:
        outcomes_t = torch.zeros(len(boards_t))

    if game_ids is not None:
        # Split by game: hold out ~10% of games for validation
        unique_games = np.unique(game_ids)
        np.random.shuffle(unique_games)
        val_game_count = max(1, len(unique_games) // 10)
        val_games = set(unique_games[:val_game_count].tolist())
        val_mask = np.array([gid in val_games for gid in game_ids])
        train_mask = ~val_mask
        train_idx = np.where(train_mask)[0]
        val_idx = np.where(val_mask)[0]
    else:
        n = len(boards_t)
        val_size = max(1, n // 10)
        indices = torch.randperm(n).numpy()
        train_idx = indices[:n - val_size]
        val_idx = indices[n - val_size:]

    train_ds = TensorDataset(boards_t[train_idx], moves_t[train_idx], outcomes_t[train_idx])
    val_ds = TensorDataset(boards_t[val_idx], moves_t[val_idx], outcomes_t[val_idx])

    train_dl = DataLoader(train_ds, batch_size=batch_size, shuffle=True)
    val_dl = DataLoader(val_ds, batch_size=batch_size)

    if model is None:
        model = PolicyNetwork().to(device)
    print(f"Model parameters: {model.param_count():,}")

    optimizer = torch.optim.Adam(model.parameters(), lr=lr)

    best_val_ploss = float("inf")
    best_val_vloss = float("inf")
    best_state = None
    best_value_state = None
    ploss_patience_counter = 0
    vloss_patience_counter = 0
    train_value = True

    for epoch in range(1, epochs + 1):
        t0 = time.time()
        _, train_ploss, train_vloss = train_epoch(model, train_dl, optimizer, device, train_value)
        _, val_acc, val_ploss, val_vloss, val_vacc = evaluate(model, val_dl, device)
        elapsed = time.time() - t0

        vacc_str = f"{100*val_vacc:.0f}%" if val_vacc is not None else "N/A"
        frozen_str = "  [value frozen]" if not train_value else ""
        print(f"Epoch {epoch:3d}/{epochs} | "
              f"policy={train_ploss:.4f}/{val_ploss:.4f}  "
              f"value={train_vloss:.4f}/{val_vloss:.4f}  "
              f"vacc={vacc_str}  "
              f"pacc={100*val_acc:.1f}%  |  {elapsed:.1f}s{frozen_str}")

        # Value head: freeze when val_vloss stops improving
        if train_value:
            if val_vloss < best_val_vloss:
                best_val_vloss = val_vloss
                vloss_patience_counter = 0
                best_value_state = {k: v.clone() for k, v in model.state_dict().items()
                                    if 'value_' in k}
            else:
                vloss_patience_counter += 1
                if vloss_patience_counter >= patience:
                    # Restore best value head weights, then hard-freeze
                    if best_value_state is not None:
                        model.load_state_dict(best_value_state, strict=False)
                    for name, param in model.named_parameters():
                        if 'value_' in name:
                            param.requires_grad = False
                    print(f"  Freezing value head at best val_vloss={best_val_vloss:.4f}")
                    train_value = False

        # Policy head: save best and early stop
        if val_ploss < best_val_ploss:
            best_val_ploss = val_ploss
            best_state = {k: v.clone() for k, v in model.state_dict().items()}
            ploss_patience_counter = 0
            if save_path:
                torch.save(best_state, save_path)
            print(f"  Saved best model (val_ploss={val_ploss:.4f})")
        else:
            ploss_patience_counter += 1
            if ploss_patience_counter >= patience:
                print(f"  Early stopping after {epoch} epochs (patience={patience})")
                break

    if best_state is not None:
        model.load_state_dict(best_state)

    return model, best_val_ploss


def main():
    parser = argparse.ArgumentParser(description="Train policy network")
    parser.add_argument("--phase", type=int, default=1, help="Training phase (1=random, 2+=policy)")
    parser.add_argument("--boards", type=int, default=500, help="Number of boards to generate")
    parser.add_argument("--games-per-board", type=int, default=10, help="Games per board")
    parser.add_argument("--epochs", type=int, default=20, help="Training epochs")
    parser.add_argument("--batch-size", type=int, default=256, help="Batch size")
    parser.add_argument("--lr", type=float, default=1e-3, help="Learning rate")
    parser.add_argument("--patience", type=int, default=2, help="Early stopping patience")
    parser.add_argument("--data-file", type=str, default=None, help="Load existing training data")
    parser.add_argument("--model-file", type=str, default=None, help="Load existing model weights")
    parser.add_argument("--save-dir", type=str, default="checkpoints", help="Directory to save models")
    parser.add_argument("--placement-bias", type=float, default=1.0, help="Placement bias for random play")
    args = parser.parse_args()

    device = torch.device("cpu")
    if torch.cuda.is_available():
        device = torch.device("cuda")
        print(f"Using CUDA: {torch.cuda.get_device_name()}")
    else:
        print("Using CPU")

    os.makedirs(args.save_dir, exist_ok=True)

    if args.data_file and os.path.exists(args.data_file):
        print(f"Loading training data from {args.data_file}")
        board_tensors, move_indices, outcome_labels, game_ids = load_training_data(args.data_file)
    else:
        print(f"Generating training data: {args.boards} boards x {args.games_per_board} games")

        batch_size = min(args.boards, 100)
        all_boards = []
        remaining = args.boards
        while remaining > 0:
            fetch_count = min(remaining, batch_size)
            print(f"  Fetching {fetch_count} boards from server...")
            all_boards.extend(fetch_boards(amount=fetch_count))
            remaining -= fetch_count

        print(f"  Playing {args.boards * args.games_per_board} games...")
        t0 = time.time()
        board_tensors, move_indices, outcome_labels, game_ids = generate_training_data(
            all_boards,
            games_per_board=args.games_per_board,
            placement_bias=args.placement_bias,
        )
        elapsed = time.time() - t0
        print(f"  Self-play took {elapsed:.1f}s")

        if board_tensors is None:
            print("No training data generated. Exiting.")
            return

        data_path = os.path.join(args.save_dir, f"phase{args.phase}_data.npz")
        save_training_data(data_path, board_tensors, move_indices, outcome_labels, game_ids)

    print(f"Training data: {len(board_tensors)} samples")

    model = PolicyNetwork().to(device)
    if args.model_file and os.path.exists(args.model_file):
        model.load_state_dict(torch.load(args.model_file, map_location=device))
        print(f"Loaded weights from {args.model_file}")

    save_path = os.path.join(args.save_dir, f"phase{args.phase}_best.pt")
    model, _ = run_training(
        board_tensors, move_indices, device,
        outcome_labels=outcome_labels,
        game_ids=game_ids,
        model=model,
        epochs=args.epochs,
        batch_size=args.batch_size,
        lr=args.lr,
        patience=args.patience,
        save_path=save_path,
    )

    onnx_path = os.path.join(args.save_dir, "policy_net.onnx")
    export_onnx(model, onnx_path, device)


if __name__ == "__main__":
    main()
