namespace AgeOfChess.Server.GameLogic;

/// <summary>
/// Tracks the state of the pre-game bidding phase.
/// Creator = the player who created the game (holds the white token initially).
/// Joiner  = the player who joined via invite link (holds the black token initially).
/// </summary>
public class BiddingSession
{
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    /// <summary>Both players' starting clock time for the bidding phase (startTime * 1.1).</summary>
    public int InitialMs { get; init; }

    public int? CreatorBid      { get; set; }
    public int  CreatorFrozenMs { get; set; }

    public int? JoinerBid      { get; set; }
    public int  JoinerFrozenMs { get; set; }

    public bool BothBid => CreatorBid.HasValue && JoinerBid.HasValue;

    public long StartedAtUnixMs =>
        new DateTimeOffset(StartedAt, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>
    /// Computes how many milliseconds remain on a player's bid clock right now.
    /// Returns the frozen value if they have already submitted.
    /// </summary>
    public int GetCreatorRemainingMs()
    {
        if (CreatorBid.HasValue) return CreatorFrozenMs;
        return Math.Max(0, InitialMs - (int)(DateTime.UtcNow - StartedAt).TotalMilliseconds);
    }

    public int GetJoinerRemainingMs()
    {
        if (JoinerBid.HasValue) return JoinerFrozenMs;
        return Math.Max(0, InitialMs - (int)(DateTime.UtcNow - StartedAt).TotalMilliseconds);
    }
}
