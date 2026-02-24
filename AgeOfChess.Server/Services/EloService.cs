using AgeOfChess.Server.GameLogic;

namespace AgeOfChess.Server.Services;

public enum TimeControlCategory { Bullet, Blitz, Rapid, Slow }

public static class EloService
{
    /// <summary>
    /// K-factor starts at 100 and decreases by 5 per game in the category, floored at 32.
    /// K = max(32, 100 - gamesPlayed × 5)
    /// </summary>
    public static int GetKFactor(int gamesPlayedInCategory)
        => Math.Max(32, 100 - gamesPlayedInCategory * 5);

    /// <summary>
    /// Maps game settings to one of the four rating categories:
    ///   Slow   — no time control, or ≥ 30 minutes starting time   (30+15, 60+30, no timer)
    ///   Rapid  — 10 or 15 minutes starting time                    (10+5, 15+10)
    ///   Blitz  — 3 to 9 minutes starting time                      (3+0, 3+2, 5+0, 5+3)
    ///   Bullet — &lt; 3 minutes starting time                          (1+0, 1+1)
    /// </summary>
    public static TimeControlCategory GetCategory(GameSettings s) =>
        !s.TimeControlEnabled       ? TimeControlCategory.Slow   :
        s.StartTimeMinutes >= 30    ? TimeControlCategory.Slow   :
        s.StartTimeMinutes >= 10    ? TimeControlCategory.Rapid  :
        s.StartTimeMinutes >= 3     ? TimeControlCategory.Blitz  :
                                      TimeControlCategory.Bullet;

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
