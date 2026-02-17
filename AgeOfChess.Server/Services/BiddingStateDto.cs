using AgeOfChess.Server.GameLogic;

namespace AgeOfChess.Server.Services;

/// <summary>
/// Snapshot of the bidding phase sent to both clients.
/// Bid amounts are null until both players have submitted (then they are revealed simultaneously).
/// </summary>
public record BiddingStateDto(
    long StartedAtUnixMs,
    int InitialMs,
    bool CreatorBidPlaced,
    int CreatorFrozenMs,
    bool JoinerBidPlaced,
    int JoinerFrozenMs,
    /// <summary>Null until both players have bid.</summary>
    int? RevealedCreatorBid,
    /// <summary>Null until both players have bid.</summary>
    int? RevealedJoinerBid
);

public static class BiddingStateDtoBuilder
{
    public static BiddingStateDto Build(BiddingSession b) =>
        new(
            b.StartedAtUnixMs,
            b.InitialMs,
            b.CreatorBid.HasValue,
            b.CreatorFrozenMs,
            b.JoinerBid.HasValue,
            b.JoinerFrozenMs,
            b.BothBid ? b.CreatorBid : null,
            b.BothBid ? b.JoinerBid : null
        );
}
