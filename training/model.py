"""Evaluation network v1 (NNUE-style): sparse features -> shared accumulator per perspective ->
two small dense layers -> one output, the win-probability logit for the side to move.

The engine's eval scale is recovered as  eval = logit * K  (K fitted on the training data,
see train.py), so the Rust integration can keep its search margins in eval units.
"""
import torch
import torch.nn as nn

from features import NUM_DENSE


class EvalNet(nn.Module):
    def __init__(self, num_features, hidden=256, l2=32, l3=32):
        super().__init__()
        self.config = dict(num_features=num_features, hidden=hidden, l2=l2, l3=l3)
        self.ft = nn.EmbeddingBag(num_features, hidden, mode="sum")
        self.ft_bias = nn.Parameter(torch.zeros(hidden))
        self.l1 = nn.Linear(2 * hidden + NUM_DENSE, l2)
        self.l2 = nn.Linear(l2, l3)
        self.out = nn.Linear(l3, 1)
        # ~25-40 active features per perspective: keep the summed accumulator in the active range
        nn.init.normal_(self.ft.weight, std=0.1 / 30 ** 0.5)
        nn.init.constant_(self.ft_bias, 0.5)

    def forward(self, us_idx, us_off, them_idx, them_off, dense):
        us = torch.clamp(self.ft(us_idx, us_off) + self.ft_bias, 0.0, 1.0)
        them = torch.clamp(self.ft(them_idx, them_off) + self.ft_bias, 0.0, 1.0)
        x = torch.cat([us, them, dense], dim=1)
        x = torch.clamp(self.l1(x), 0.0, 1.0)
        x = torch.clamp(self.l2(x), 0.0, 1.0)
        return self.out(x).squeeze(1)

    def param_count(self):
        return sum(p.numel() for p in self.parameters())
