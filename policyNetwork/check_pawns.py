"""Check if training data contains pawn placements."""
import numpy as np
from encode import index_to_move

# Load training data
data = np.load('checkpoints/iter1_data.npz')
moves = data['moves']

# Count pawn placements
pawn_count = 0
total_moves = len(moves)

for idx in moves:
    move = index_to_move(idx)
    # move format: (M, from_pos, to_pos) or (P, piece_type, dest_pos, is_white)
    if move[0] == 'P' and move[1] == 'p':  # Placement of pawn
        pawn_count += 1

print(f"Total moves: {total_moves}")
print(f"Pawn placements: {pawn_count}")
print(f"Percentage: {100 * pawn_count / total_moves:.2f}%")

if pawn_count > 0:
    print("\n⚠️  PROBLEM: Training data contains pawn placements!")
    print("The pawn cap isn't working during self-play.")
else:
    print("\n✓ OK: No pawn placements in training data.")
    print("The model learned to place pawns from a previous training run.")
