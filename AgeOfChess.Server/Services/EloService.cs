using AgeOfChess.Server.GameLogic;

namespace AgeOfChess.Server.Services;

public enum TimeControlCategory { Blitz, Rapid, Slow }

public static class EloService
{
    /// <summary>
    /// K-factor starts at 100 and decreases by 5 per game in the category, floored at 32.
    /// K = max(32, 100 - gamesPlayed × 5)
    /// </summary>
    public static int GetKFactor(int gamesPlayedInCategory)
        => Math.Max(32, 100 - gamesPlayedInCategory * 5);

    /// <summary>
    /// Maps game settings to one of the three rating categories:
    ///   Blitz  — time-controlled with ≤ 5 minutes starting time
    ///   Rapid  — time-controlled with > 5 minutes starting time
    ///   Slow   — no time control
    /// </summary>
    public static TimeControlCategory GetCategory(GameSettings s) =>
        !s.TimeControlEnabled       ? TimeControlCategory.Slow  :
        s.StartTimeMinutes <= 5     ? TimeControlCategory.Blitz :
                                      TimeControlCategory.Rapid;

    /// <summary>
    /// Returns updated (whiteElo, blackElo) after a decisive result.
    /// Uses the standard Elo formula with per-player K-factors.
    /// </summary>
    public static (int newWhiteElo, int newBlackElo) Calculate(
        int whiteElo, int blackElo,
        int whiteGamesInCategory, int blackGamesInCategory,
        bool whiteWon)
    {
        // Expected score for white (probability of winning from white's perspective)
        var expected = 1.0 / (1.0 + Math.Pow(10.0, (blackElo - whiteElo) / 400.0));
        var actual   = whiteWon ? 1.0 : 0.0;

        var kWhite = GetKFactor(whiteGamesInCategory);
        var kBlack = GetKFactor(blackGamesInCategory);

        var newWhite = (int)Math.Round(whiteElo + kWhite * (actual - expected));
        var newBlack = (int)Math.Round(blackElo + kBlack * ((1.0 - actual) - (1.0 - expected)));

        return (newWhite, newBlack);
    }
}
