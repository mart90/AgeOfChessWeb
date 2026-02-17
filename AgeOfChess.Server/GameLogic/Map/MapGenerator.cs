using AgeOfChess.Server.GameLogic.PlaceableObjects;
using AgeOfChess.Server.GameLogic.PlaceableObjects.GaiaObjects;
using AgeOfChess.Server.GameLogic.PlaceableObjects.Pieces;

namespace AgeOfChess.Server.GameLogic.Map;

public class MapGenerator
{
    private Map _map = null!;

    public Map GenerateRandom(int width, int height)
    {
        if (width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException("Map dimensions must be even.");

        if (width < 6 || height < 6 || width > 20 || height > 20)
            throw new ArgumentException("Map size must be between 6x6 and 20x20.");

        _map = new Map(width, height);

        MakeBaseSquares();

        AddRandomlyGeneratedSquares(SquareType.DirtRocks, 0.02);
        AddRandomlyGeneratedSquares(SquareType.GrassRocks, 0.02);
        AddRandomlyGeneratedSquares(SquareType.DirtTrees, 0.02);
        AddRandomlyGeneratedSquares(SquareType.GrassTrees, 0.02);
        AddRandomlyGeneratedSquares(SquareType.DirtMine, 0.02);
        AddRandomlyGeneratedSquares(SquareType.GrassMine, 0.02);

        if (width == 12 || width == 10)
        {
            AddRandomlyGeneratedSquares(SquareType.GrassMine, 0.00001);
            AddRandomlyGeneratedSquares(SquareType.GrassRocks, 0.00001);
            AddRandomlyGeneratedSquares(SquareType.GrassTrees, 0.00001);
        }

        AddRandomlyGeneratedGaiaObjects<Treasure>(0.02);

        SpawnKings();

        MirrorGeneratedHalf();

        _map.SetSeed();

        return _map;
    }

    public Map GenerateFromSeed(string seed)
    {
        string[] wxh = seed.Split('_')[0].Split('x');
        _map = new Map(int.Parse(wxh[0]), int.Parse(wxh[1])) { Seed = seed };

        MakeBaseSquares();

        int currentSquareId = 0;

        foreach (char c in seed.Split('_')[1])
        {
            if (int.TryParse(c.ToString(), out int emptySquares))
            {
                currentSquareId += emptySquares;
                continue;
            }

            Square currentSquare = _map.Squares.Single(e => e.Id == currentSquareId);

            if (c == 'k')
                currentSquare.SetObject(new WhiteKing());
            else if (c == 't')
                currentSquare.SetObject(new Treasure());
            else if (c == 'm')
                currentSquare.SetType(currentSquare.Type == SquareType.Dirt ? SquareType.DirtMine : SquareType.GrassMine);
            else if (c == 'r')
                currentSquare.SetType(currentSquare.Type == SquareType.Dirt ? SquareType.DirtRocks : SquareType.GrassRocks);
            else if (c == 'f')
                currentSquare.SetType(currentSquare.Type == SquareType.Dirt ? SquareType.DirtTrees : SquareType.GrassTrees);

            currentSquareId++;
        }

        MirrorGeneratedHalf();

        return _map;
    }

    public bool ValidateSeed(string seed)
    {
        if (!seed.Contains('k')) return false;

        try
        {
            GenerateFromSeed(seed);
            _map = null!;
        }
        catch
        {
            return false;
        }

        return true;
    }

    public void SpawnKings()
    {
        Square whiteKingSquare = _map.GetRandomEmptySquare(0, _map.Height * _map.Width / 2 - 1);

        // Prevent kings from spawning within striking distance of each other
        while ((whiteKingSquare.X == _map.Width / 2 || whiteKingSquare.X == _map.Width / 2 - 1) && whiteKingSquare.Y == _map.Height / 2 - 1)
            whiteKingSquare = _map.GetRandomEmptySquare(0, _map.Height * _map.Width / 2 - 1);

        whiteKingSquare.SetObject(new WhiteKing());

        Square blackKingSquare = _map.Squares.Single(e => e.Id == _map.Height * _map.Width - 1 - whiteKingSquare.Id);
        blackKingSquare.SetObject(new BlackKing());
    }

    public void AddRandomlyGeneratedSquares(SquareType squareType, double fractionOfMap)
    {
        int amountToAdd = (int)Math.Round(fractionOfMap * _map.Squares.Count * 0.5);
        if (amountToAdd == 0) amountToAdd = 1;

        var unoccupiedType = squareType is SquareType.DirtRocks or SquareType.DirtMine or SquareType.DirtTrees
            ? SquareType.Dirt
            : SquareType.Grass;

        for (int i = 0; i < amountToAdd; i++)
            _map.GetRandomSquareOfType(unoccupiedType, 0, _map.Height * _map.Width / 2 - 1).SetType(squareType);
    }

    public void AddRandomlyGeneratedGaiaObjects<T>(double fractionOfEmptySquares) where T : GaiaObject, new()
    {
        var emptySquares = _map.GetSquaresByType(SquareType.Dirt).Concat(_map.GetSquaresByType(SquareType.Grass));
        int amountToAdd = (int)Math.Round(fractionOfEmptySquares * emptySquares.Count());
        if (amountToAdd < 2) amountToAdd = 2;

        for (int i = 0; i < amountToAdd; i++)
            _map.GetRandomEmptySquare(0, _map.Height * _map.Width / 2 - 1).SetObject(new T());
    }

    private void MakeBaseSquares()
    {
        for (int y = 0; y < _map.Height; y++)
        {
            for (int x = 0; x < _map.Width; x++)
            {
                var square = new Square(x, y, y * _map.Width + x);

                SquareType squareType = y % 2 == 0
                    ? (x % 2 == 0 ? SquareType.Grass : SquareType.Dirt)
                    : (x % 2 == 0 ? SquareType.Dirt : SquareType.Grass);

                square.SetType(squareType);
                _map.Squares.Add(square);
            }
        }
    }

    private void MirrorGeneratedHalf()
    {
        foreach (Square square in _map.Squares.Where(e => e.Id < _map.Height * _map.Width / 2 - 1))
        {
            Square mirrorSquare = _map.Squares.Single(e => e.Id == _map.Height * _map.Width - 1 - square.Id);
            mirrorSquare.SetType(square.Type);

            if (square.Object is GaiaObject)
                mirrorSquare.SetObject(Activator.CreateInstance(square.Object.GetType()) as GaiaObject);
            else if (square.Object is King)
                mirrorSquare.SetObject(new BlackKing());
        }
    }
}
