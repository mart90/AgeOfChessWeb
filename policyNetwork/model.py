import torch
import torch.nn as nn
import torch.nn.functional as F
from encode import NUM_PLANES, NUM_ACTIONS, BOARD_SIZE


class ResBlock(nn.Module):
    def __init__(self, channels):
        super().__init__()
        self.conv1 = nn.Conv2d(channels, channels, 3, padding=1)
        self.bn1 = nn.BatchNorm2d(channels)
        self.conv2 = nn.Conv2d(channels, channels, 3, padding=1)
        self.bn2 = nn.BatchNorm2d(channels)

    def forward(self, x):
        residual = x
        x = F.relu(self.bn1(self.conv1(x)))
        x = self.bn2(self.conv2(x))
        return F.relu(x + residual)


class PolicyNetwork(nn.Module):
    def __init__(self, in_channels=NUM_PLANES, board_size=BOARD_SIZE,
                 num_actions=NUM_ACTIONS, num_filters=256, num_res_blocks=15):
        super().__init__()
        self.board_size = board_size

        self.conv_in = nn.Sequential(
            nn.Conv2d(in_channels, num_filters, 3, padding=1),
            nn.BatchNorm2d(num_filters),
            nn.ReLU(),
        )
        self.res_blocks = nn.Sequential(
            *[ResBlock(num_filters) for _ in range(num_res_blocks)]
        )
        self.conv_out = nn.Sequential(
            nn.Conv2d(num_filters, 32, 1),
            nn.ReLU(),
        )

        flat_size = 32 * board_size * board_size
        self.fc = nn.Sequential(
            nn.Linear(flat_size, 256),
            nn.ReLU(),
            nn.Linear(256, num_actions),
        )

    def forward(self, x):
        """
        Args:
            x: (batch, in_channels, board_size, board_size)
        Returns:
            logits: (batch, num_actions) — raw logits, apply mask before softmax
        """
        x = self.conv_in(x)
        x = self.res_blocks(x)
        x = self.conv_out(x)
        x = x.view(x.size(0), -1)
        return self.fc(x)

    def param_count(self):
        return sum(p.numel() for p in self.parameters())


if __name__ == "__main__":
    model = PolicyNetwork()
    print(f"Parameters: {model.param_count():,}")

    # Test forward pass
    dummy = torch.randn(1, NUM_PLANES, BOARD_SIZE, BOARD_SIZE)
    out = model(dummy)
    print(f"Input shape:  {dummy.shape}")
    print(f"Output shape: {out.shape}")
    print(f"Expected:     (1, {NUM_ACTIONS})")
