"""Evaluation network v1 (NNUE-style): sparse features -> shared accumulator per perspective ->
two small dense layers -> one output, the win-probability logit for the side to move.
Optional moves-left head (moves_left=True): a small branch on the first dense layer's outputs
that predicts the plies until the game ends, / ML_SCALE (so the search can prefer faster wins).

The engine's eval scale is recovered as  eval = logit * K  (K fitted on the training data,
see train.py), so the Rust integration can keep its search margins in eval units.
"""
import torch
import torch.nn as nn

ML_SCALE = 100.0   # moves-left head output unit, in plies (engine/src/nnue.rs ML_SCALE)

class EvalNet(nn.Module):
    # num_dense: 4 = v1 (gold, mines), 8 = v2 (+ gold race); old checkpoints have no num_dense -> 4.
    # race: fixed gold-race term added to the output, in logit units (hand-written eval's race weight
    # / K): logit = network + race * (1/(t_us+1) - 1/(t_them+1)), t = dense inputs 4 and 5. The
    # network then only learns the rest of the game. 0 = off. Needs num_dense = 8.
    def __init__(self, num_features, hidden=256, l2=32, l3=32, num_dense=4, race=0.0, moves_left=False):
        super().__init__()
        assert race == 0.0 or num_dense >= 6, "the race term needs the v2 race inputs"
        self.config = dict(num_features=num_features, hidden=hidden, l2=l2, l3=l3, num_dense=num_dense, race=race,
                           moves_left=moves_left)
        self.num_dense = num_dense
        self.race = race
        self.moves_left = moves_left
        self.ft = nn.EmbeddingBag(num_features, hidden, mode="sum")
        self.ft_bias = nn.Parameter(torch.zeros(hidden))
        self.l1 = nn.Linear(2 * hidden + num_dense, l2)
        self.l2 = nn.Linear(l2, l3)
        self.out = nn.Linear(l3, 1)
        if moves_left:
            self.ml1 = nn.Linear(l2, l3)
            self.ml2 = nn.Linear(l3, 1)
        # ~25-40 active features per perspective: keep the summed accumulator in the active range
        nn.init.normal_(self.ft.weight, std=0.1 / 30 ** 0.5)
        nn.init.constant_(self.ft_bias, 0.5)

    def forward(self, us_idx, us_off, them_idx, them_off, dense, with_ml=False):
        """Win-probability logit; with with_ml=True also the moves-left prediction (plies / ML_SCALE)."""
        us = torch.clamp(self.ft(us_idx, us_off) + self.ft_bias, 0.0, 1.0)
        them = torch.clamp(self.ft(them_idx, them_off) + self.ft_bias, 0.0, 1.0)
        x = torch.cat([us, them, dense], dim=1)
        y1 = torch.clamp(self.l1(x), 0.0, 1.0)
        x = torch.clamp(self.l2(y1), 0.0, 1.0)
        out = self.out(x).squeeze(1)
        if self.race:
            out = out + self.race * (dense[:, 4] - dense[:, 5])
        if not with_ml:
            return out
        ml = self.ml2(torch.clamp(self.ml1(y1), 0.0, 1.0)).squeeze(1) if self.moves_left else None
        return out, ml

    def trunk_parameters(self):
        """Everything except the moves-left head."""
        return [p for n, p in self.named_parameters() if not n.startswith("ml")]

    def param_count(self):
        return sum(p.numel() for p in self.parameters())
