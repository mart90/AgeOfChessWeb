using AgeOfChess.Server.GameLogic.PlaceableObjects;
using AgeOfChess.Server.GameLogic.PlaceableObjects.Pieces;

namespace AgeOfChess.Server.GameLogic.Map;

public class Map
{
    public List<Square> Squares { get; }
    public int Width { get; }
    public int Height { get; }
    public string Seed { get; set; } = string.Empty;
    public bool IsMirrored { get; set; }

    private readonly Random _random = new();

    public Map(int width, int height, bool isMirrored)
    {
        Width = width;
        Height = height;
        Squares = new List<Square>();
        IsMirrored = isMirrored;
    }

    public Square? SelectedSquare => Squares.SingleOrDefault(e => e.IsSelected);

    public Square? GetSquareByCoordinates(int x, int y) => Squares.SingleOrDefault(e => e.X == x && e.Y == y);

    public IEnumerable<Square> GetSquaresByType(SquareType squareType) => Squares.Where(e => e.Type == squareType);

    public IEnumerable<Square> GetMines() =>
        GetSquaresByType(SquareType.DirtMine).Concat(GetSquaresByType(SquareType.GrassMine));

    public Square GetRandomSquareOfType(SquareType squareType, int startingSquare = 0, int endingSquare = 0)
    {
        List<Square> squares = GetSquaresByType(squareType).ToList();

        if (startingSquare != 0)
            squares.RemoveAll(e => e.Id < startingSquare);
        if (endingSquare != 0)
            squares.RemoveAll(e => e.Id > endingSquare);

        return squares[_random.Next(0, squares.Count - 1)];
    }

    public Square GetRandomEmptySquare(int startingSquare = 0, int endingSquare = 0)
    {
        var randomEmptyType = _random.Next(0, 2) == 0 ? SquareType.Dirt : SquareType.Grass;
        return GetRandomSquareOfType(randomEmptyType, startingSquare, endingSquare);
    }

    public IEnumerable<Square> FindLegalDestinationsForPiece(Piece piece, Square sourceSquare) =>
        new PathFinder(this).FindLegalDestinationSquares(piece, sourceSquare);

    public void SetSeed()
    {
        string seedString = IsMirrored ? "m" : "r";
        seedString += $"_{Width}x{Height}_";
        int consecutiveEmptySquares = 0;

        var squares = IsMirrored ? Squares.Where(e => e.Id < Width * Height / 2) : Squares;

        foreach (Square square in squares.OrderBy(e => e.Id))
        {
            if (square.Object == null && (square.Type == SquareType.Dirt || square.Type == SquareType.Grass))
            {
                consecutiveEmptySquares++;
                if (consecutiveEmptySquares == 9)
                {
                    seedString += "9";
                    consecutiveEmptySquares = 0;
                }
                continue;
            }
            else if (consecutiveEmptySquares != 0)
            {
                seedString += consecutiveEmptySquares.ToString();
                consecutiveEmptySquares = 0;
            }

            if (square.Object != null)
                seedString += square.Object is King ? "k" : "t";
            else if (square.Type == SquareType.GrassMine || square.Type == SquareType.DirtMine)
                seedString += "m";
            else if (square.Type == SquareType.DirtRocks || square.Type == SquareType.GrassRocks)
                seedString += "r";
            else if (square.Type == SquareType.DirtTrees || square.Type == SquareType.GrassTrees)
                seedString += "f";
        }

        if (consecutiveEmptySquares != 0)
            seedString += consecutiveEmptySquares.ToString();

        Seed = seedString;
    }
}
