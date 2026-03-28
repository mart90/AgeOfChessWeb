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


class PolicyOnlyWrapper(nn.Module):
    """Wraps PolicyNetwork to export only the policy output (for ONNX / game server)."""
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, x):
        p, _ = self.model(x)
        return p


def _policy_loss(policy_logits, policy_indices, policy_counts):
    """Cross-entropy from visit-count distribution targets."""
    batch_size = policy_logits.size(0)
    num_actions = policy_logits.size(1)

    # Build soft target distribution from sparse visit counts
    target = torch.zeros(batch_size, num_actions, device=policy_logits.device)
    valid = policy_indices >= 0                              # (B, K)
    safe_idx = policy_indices.clone()
    safe_idx[~valid] = 0                                     # avoid negative index
    target.scatter_add_(1, safe_idx, policy_counts.float() * valid.float())
    row_sums = target.sum(dim=1, keepdim=True).clamp(min=1e-8)
    target = target / row_sums

    log_probs = F.log_softmax(policy_logits, dim=1)
    return -(target * log_probs).sum(dim=1).mean()


def train_epoch(model, dataloader, optimizer, device):
    model.train()
    total_loss = 0
    total_correct = 0
    total_samples = 0
    large_grad_count = 0
    nan_count = 0

    stop_event = threading.Event()
    timer = threading.Thread(target=_timer_thread, args=(stop_event,), daemon=True)
    timer.start()

    for boards, pol_idx, pol_cnt, val_tgt in dataloader:
        boards   = boards.to(device)
        pol_idx  = pol_idx.to(device)
        pol_cnt  = pol_cnt.to(device)
        val_tgt  = val_tgt.to(device)

        policy_logits, value_pred = model(boards)

        p_loss = _policy_loss(policy_logits, pol_idx, pol_cnt)
        v_loss = F.mse_loss(value_pred, val_tgt)
        loss = p_loss + v_loss  # λ=1.0

        if not torch.isfinite(loss):
            print(f"    WARNING: Non-finite loss detected, skipping batch")
            nan_count += 1
            continue

        optimizer.zero_grad()
        loss.backward()

        grad_norm = torch.nn.utils.clip_grad_norm_(model.parameters(), max_norm=20.0)
        if grad_norm > 20.0:
            large_grad_count += 1

        optimizer.step()

        n = boards.size(0)
        total_loss += loss.item() * n

        # Accuracy: predicted top move vs most-visited move
        best_col = pol_cnt.argmax(dim=1, keepdim=True)
        top_target = pol_idx.gather(1, best_col).squeeze(1)   # (B,)
        preds = policy_logits.argmax(dim=1)
        valid_mask = top_target >= 0
        if valid_mask.any():
            total_correct += (preds[valid_mask] == top_target[valid_mask]).sum().item()
            total_samples += valid_mask.sum().item()

    stop_event.set()
    timer.join()

    if large_grad_count > 0:
        print(f"    Clipped large gradients in {large_grad_count} batches")
    if nan_count > 0:
        print(f"    Skipped {nan_count} batches due to NaN/inf loss")

    acc = total_correct / total_samples if total_samples > 0 else 0.0
    avg_loss = total_loss / total_samples if total_samples > 0 else 0.0
    return acc, avg_loss


def evaluate(model, dataloader, device):
    model.eval()
    total_loss = 0
    total_correct = 0
    total_samples = 0

    with torch.no_grad():
        for boards, pol_idx, pol_cnt, val_tgt in dataloader:
            boards   = boards.to(device)
            pol_idx  = pol_idx.to(device)
            pol_cnt  = pol_cnt.to(device)
            val_tgt  = val_tgt.to(device)

            policy_logits, value_pred = model(boards)

            p_loss = _policy_loss(policy_logits, pol_idx, pol_cnt)
            v_loss = F.mse_loss(value_pred, val_tgt)
            loss = p_loss + v_loss

            n = boards.size(0)
            total_loss += loss.item() * n

            best_col = pol_cnt.argmax(dim=1, keepdim=True)
            top_target = pol_idx.gather(1, best_col).squeeze(1)
            preds = policy_logits.argmax(dim=1)
            valid_mask = top_target >= 0
            if valid_mask.any():
                total_correct += (preds[valid_mask] == top_target[valid_mask]).sum().item()
                total_samples += valid_mask.sum().item()

    acc = total_correct / total_samples if total_samples > 0 else 0.0
    avg_loss = total_loss / total_samples if total_samples > 0 else 0.0
    return avg_loss, acc


def export_onnx(model, filepath, device):
    """Export policy-only output to ONNX (game server doesn't use value head)."""
    wrapper = PolicyOnlyWrapper(model)
    wrapper.eval()
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


def run_training(board_tensors, policy_indices, policy_counts, value_targets, device,
                 game_ids=None, model=None, epochs=20, batch_size=256, lr=1e-3,
                 patience=2, save_path=None):
    """Train a policy+value network with early stopping.

    Returns (model, best_val_loss, best_val_acc).
    """
    boards_t  = torch.from_numpy(board_tensors)
    pol_idx_t = torch.from_numpy(policy_indices.astype(np.int64))
    pol_cnt_t = torch.from_numpy(policy_counts.astype(np.int64))
    val_t     = torch.from_numpy(value_targets)

    if game_ids is not None:
        unique_games = np.unique(game_ids)
        np.random.shuffle(unique_games)
        val_game_count = max(1, len(unique_games) // 10)
        val_games = set(unique_games[:val_game_count].tolist())
        val_mask = np.array([gid in val_games for gid in game_ids])
        train_idx = np.where(~val_mask)[0]
        val_idx   = np.where(val_mask)[0]
    else:
        n = len(boards_t)
        val_size = max(1, n // 10)
        indices = torch.randperm(n).numpy()
        train_idx = indices[:n - val_size]
        val_idx   = indices[n - val_size:]

    train_ds = TensorDataset(boards_t[train_idx], pol_idx_t[train_idx],
                              pol_cnt_t[train_idx], val_t[train_idx])
    val_ds   = TensorDataset(boards_t[val_idx],   pol_idx_t[val_idx],
                              pol_cnt_t[val_idx],   val_t[val_idx])

    train_dl = DataLoader(train_ds, batch_size=batch_size, shuffle=True)
    val_dl   = DataLoader(val_ds,   batch_size=batch_size)

    if model is None:
        model = PolicyNetwork().to(device)
    print(f"Model parameters: {model.param_count():,}")

    optimizer = torch.optim.Adam(model.parameters(), lr=lr)

    best_val_loss = float("inf")
    best_val_acc  = 0.0
    best_state    = None
    patience_counter = 0

    for epoch in range(1, epochs + 1):
        t0 = time.time()
        train_acc, train_loss = train_epoch(model, train_dl, optimizer, device)
        val_loss, val_acc     = evaluate(model, val_dl, device)
        elapsed = time.time() - t0

        print(f"Epoch {epoch:3d}/{epochs} | "
              f"loss={train_loss:.4f}/{val_loss:.4f}  "
              f"acc={100*train_acc:.1f}%/{100*val_acc:.1f}%  |  {elapsed:.1f}s")

        if val_loss < best_val_loss:
            best_val_loss = val_loss
            best_val_acc  = val_acc
            best_state = {k: v.clone() for k, v in model.state_dict().items()}
            patience_counter = 0
            if save_path:
                torch.save(best_state, save_path)
            print(f"  Saved best model (val_loss={val_loss:.4f})")
        else:
            patience_counter += 1
            if patience_counter >= patience:
                print(f"  Early stopping after {epoch} epochs (patience={patience})")
                break

    if best_state is not None:
        model.load_state_dict(best_state)

    return model, best_val_loss, best_val_acc


def main():
    parser = argparse.ArgumentParser(description="Train policy+value network")
    parser.add_argument("--phase", type=int, default=1)
    parser.add_argument("--boards", type=int, default=500)
    parser.add_argument("--games-per-board", type=int, default=10)
    parser.add_argument("--epochs", type=int, default=20)
    parser.add_argument("--batch-size", type=int, default=256)
    parser.add_argument("--lr", type=float, default=1e-3)
    parser.add_argument("--patience", type=int, default=2)
    parser.add_argument("--data-file", type=str, default=None)
    parser.add_argument("--model-file", type=str, default=None)
    parser.add_argument("--save-dir", type=str, default="checkpoints")
    parser.add_argument("--placement-bias", type=float, default=1.0)
    parser.add_argument("--gamma", type=float, default=0.99)
    args = parser.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Using {device}")
    os.makedirs(args.save_dir, exist_ok=True)

    if args.data_file and os.path.exists(args.data_file):
        print(f"Loading training data from {args.data_file}")
        board_tensors, policy_indices, policy_counts, value_targets, game_ids = \
            load_training_data(args.data_file)
    else:
        print(f"Generating training data: {args.boards} boards x {args.games_per_board} games")
        batch_sz = min(args.boards, 100)
        all_boards = []
        remaining = args.boards
        while remaining > 0:
            fetch_count = min(remaining, batch_sz)
            print(f"  Fetching {fetch_count} boards from server...")
            all_boards.extend(fetch_boards(amount=fetch_count))
            remaining -= fetch_count

        t0 = time.time()
        board_tensors, policy_indices, policy_counts, value_targets, game_ids, _, _ = \
            generate_training_data(
                all_boards,
                games_per_board=args.games_per_board,
                placement_bias=args.placement_bias,
                gamma=args.gamma,
            )
        print(f"  Self-play took {time.time() - t0:.1f}s")

        if board_tensors is None:
            print("No training data generated. Exiting.")
            return

        data_path = os.path.join(args.save_dir, f"phase{args.phase}_data.npz")
        save_training_data(data_path, board_tensors, policy_indices, policy_counts,
                           value_targets, game_ids)

    print(f"Training data: {len(board_tensors)} samples")

    model = PolicyNetwork().to(device)
    if args.model_file and os.path.exists(args.model_file):
        model.load_state_dict(torch.load(args.model_file, map_location=device, weights_only=True))
        print(f"Loaded weights from {args.model_file}")

    save_path = os.path.join(args.save_dir, f"phase{args.phase}_best.pt")
    model, _, _ = run_training(
        board_tensors, policy_indices, policy_counts, value_targets, device,
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
