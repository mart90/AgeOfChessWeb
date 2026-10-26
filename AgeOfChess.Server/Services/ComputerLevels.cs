namespace AgeOfChess.Server.Services;

/// <summary>
/// Computer opponent strengths. The engine runs in the player's browser with a fixed node
/// budget per move (same strength on every device). Elo values are estimates calibrated with
/// engine-vs-engine matches (see docs/engine.md). Keep in sync with client/src/lib/engine/levels.js.
///
/// Weak levels make deliberate mistakes: with probability MistakeRate they play the engine's
/// second-best move, as long as it is at most MistakeMaxLoss gold worse than the best.
/// </summary>
public static class ComputerLevels
{
    public record Level(int Number, int Nodes, int Elo, double MistakeRate = 0, double MistakeMaxLoss = 0);

    public static readonly IReadOnlyList<Level> All =
    [
        new(1,   1_000,  800, MistakeRate: 0.6, MistakeMaxLoss: 5),
        new(2,   1_000, 1000),
        new(3,   2_400, 1200),
        new(4,   6_000, 1400),
        new(5,  12_000, 1600),
        new(6,  60_000, 1800),
        new(7, 600_000, 2000),
    ];

    public static Level? Get(int? number) => All.FirstOrDefault(l => l.Number == number);

    public static string Name(int number) => $"Computer (Level {number})";
}
