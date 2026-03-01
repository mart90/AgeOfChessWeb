import argparse
import os
import time
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
from torch.utils.data import TensorDataset, DataLoader

from model import PolicyNetwork
from encode import NUM_PLANES, NUM_ACTIONS, BOARD_SIZE
from self_play import generate_training_data, save_training_data, load_training_data
from generate_boards import fetch_boards


def train_epoch(model, dataloader, optimizer, device):
    model.train()
    total_loss = 0
    total_correct = 0
    total_samples = 0

    for boards, moves in dataloader:
        boards = boards.to(device)
        moves = moves.to(device)

        logits = model(boards)
        loss = F.cross_entropy(logits, moves)

        optimizer.zero_grad()
        loss.backward()
        optimizer.step()

        total_loss += loss.item() * boards.size(0)
        preds = logits.argmax(dim=1)
        total_correct += (preds == moves).sum().item()
        total_samples += boards.size(0)

    avg_loss = total_loss / total_samples
    accuracy = total_correct / total_samples
    return avg_loss, accuracy


def evaluate(model, dataloader, device):
    model.eval()
    total_loss = 0
    total_correct = 0
    total_samples = 0

    with torch.no_grad():
        for boards, moves in dataloader:
            boards = boards.to(device)
            moves = moves.to(device)

            logits = model(boards)
            loss = F.cross_entropy(logits, moves)

            total_loss += loss.item() * boards.size(0)
            preds = logits.argmax(dim=1)
            total_correct += (preds == moves).sum().item()
            total_samples += boards.size(0)

    avg_loss = total_loss / total_samples
    accuracy = total_correct / total_samples
    return avg_loss, accuracy


def export_onnx(model, filepath, device):
    model.eval()
    dummy = torch.randn(1, NUM_PLANES, BOARD_SIZE, BOARD_SIZE, device=device)
    torch.onnx.export(
        model, dummy, filepath,
        input_names=["board"],
        output_names=["policy"],
        dynamic_axes={"board": {0: "batch"}, "policy": {0: "batch"}},
        opset_version=18,
        dynamo=False,
    )
    size_mb = os.path.getsize(filepath) / (1024 * 1024)
    print(f"Exported ONNX model to {filepath} ({size_mb:.1f} MB)")


def run_training(board_tensors, move_indices, device, model=None,
                 epochs=20, batch_size=256, lr=1e-3, patience=2,
                 save_path=None):
    """
    Train a policy network on the given data with early stopping.

    Returns (model, best_val_loss) with the best model weights loaded.
    """
    boards_t = torch.from_numpy(board_tensors)
    moves_t = torch.from_numpy(move_indices)

    n = len(boards_t)
    val_size = max(1, n // 10)
    indices = torch.randperm(n)
    train_idx = indices[:n - val_size]
    val_idx = indices[n - val_size:]

    train_ds = TensorDataset(boards_t[train_idx], moves_t[train_idx])
    val_ds = TensorDataset(boards_t[val_idx], moves_t[val_idx])

    train_dl = DataLoader(train_ds, batch_size=batch_size, shuffle=True)
    val_dl = DataLoader(val_ds, batch_size=batch_size)

    if model is None:
        model = PolicyNetwork().to(device)
    print(f"Model parameters: {model.param_count():,}")

    optimizer = torch.optim.Adam(model.parameters(), lr=lr)

    best_val_loss = float("inf")
    best_state = None
    patience_counter = 0

    for epoch in range(1, epochs + 1):
        t0 = time.time()
        train_loss, train_acc = train_epoch(model, train_dl, optimizer, device)
        val_loss, val_acc = evaluate(model, val_dl, device)
        elapsed = time.time() - t0

        print(f"Epoch {epoch:3d}/{epochs} | "
              f"train_loss={train_loss:.4f} train_acc={train_acc:.4f} | "
              f"val_loss={val_loss:.4f} val_acc={val_acc:.4f} | {elapsed:.1f}s")

        if val_loss < best_val_loss:
            best_val_loss = val_loss
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

    # Load best weights back into model
    if best_state is not None:
        model.load_state_dict(best_state)

    return model, best_val_loss


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

    # Load or generate training data
    if args.data_file and os.path.exists(args.data_file):
        print(f"Loading training data from {args.data_file}")
        board_tensors, move_indices = load_training_data(args.data_file)
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
        board_tensors, move_indices = generate_training_data(
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
        save_training_data(data_path, board_tensors, move_indices)

    print(f"Training data: {len(board_tensors)} samples")

    # Load existing model if specified
    model = PolicyNetwork().to(device)
    if args.model_file and os.path.exists(args.model_file):
        model.load_state_dict(torch.load(args.model_file, map_location=device))
        print(f"Loaded weights from {args.model_file}")

    save_path = os.path.join(args.save_dir, f"phase{args.phase}_best.pt")
    model, best_val_loss = run_training(
        board_tensors, move_indices, device,
        model=model,
        epochs=args.epochs,
        batch_size=args.batch_size,
        lr=args.lr,
        patience=args.patience,
        save_path=save_path,
    )

    # Export ONNX from best model
    onnx_path = os.path.join(args.save_dir, "policy_net.onnx")
    export_onnx(model, onnx_path, device)


if __name__ == "__main__":
    main()
