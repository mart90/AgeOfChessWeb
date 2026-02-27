namespace AgeOfChess.Server.GameLogic;

public enum SquareType
{
    Dirt,
    DirtRocks,
    DirtMine,
    DirtTrees,
    Grass,
    GrassRocks,
    GrassMine,
    GrassTrees
}

public enum SquareTypeGrouped
{
    Empty,
    Rocks,
    Mine,
    Trees
}

public enum SquareColor
{
    None,
    Blue,
    Red,
    Purple,
    Orange,
    Green
}

public enum Direction
{
    North = 0,
    NorthEast = 1,
    East = 2,
    SouthEast = 3,
    South = 4,
    SouthWest = 5,
    West = 6,
    NorthWest = 7
}

public enum GameState
{
    Default,
    PlacingPiece,
    Bidding,
    WaitingForOpponentBid
}
