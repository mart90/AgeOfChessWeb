using AgeOfChess.Server.GameLogic.PlaceableObjects;
using AgeOfChess.Server.GameLogic.PlaceableObjects.GaiaObjects;
using AgeOfChess.Server.GameLogic.PlaceableObjects.Pieces;
using AgeOfChess.Server.Services;

namespace AgeOfChess.Server.GameLogic.Map;

public class MapGenerator
{
    private Map _map = null!;    

    private readonly Random _random = new();

    public Map GenerateMirrored(int width, int height)
    {
        if (width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException("Map dimensions must be even.");

        if (width < 6 || height < 6 || width > 16 || height > 16)
            throw new ArgumentException("Map size must be between 6x6 and 16x16.");

        _map = new Map(width, height, true);

        MakeBaseSquares();

        AddRandomlyGeneratedSquares(SquareTypeGrouped.Rocks, 0.04);
        AddRandomlyGeneratedSquares(SquareTypeGrouped.Trees, 0.04);
        AddRandomlyGeneratedSquares(SquareTypeGrouped.Mine, 0.04);

        if (width == 8 || width == 10)
        {
            AddRandomlyGeneratedSquares(SquareTypeGrouped.Mine, 0.000001);
            AddRandomlyGeneratedSquares(SquareTypeGrouped.Rocks, 0.000001);
            AddRandomlyGeneratedSquares(SquareTypeGrouped.Trees, 0.000001);
        }

        if (width == 12)
        {
            AddRandomlyGeneratedSquares(SquareTypeGrouped.Rocks, 0.000001);
            AddRandomlyGeneratedSquares(SquareTypeGrouped.Trees, 0.000001);
        }

        AddRandomlyGeneratedGaiaObjects<Treasure>(0.02);

        SpawnKings();

        MirrorGeneratedHalf();

        _map.SetSeed();

        return _map;
    }

    public Map GenerateFullRandom(int width, int height)
    {
        if (width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException("Map dimensions must be even.");

        if (width < 6 || height < 6 || width > 16 || height > 16)
            throw new ArgumentException("Map size must be between 6x6 and 16x16.");

        _map = new Map(width, height, false);

        MakeBaseSquares();

        int amountToAdd = 2;

        if (width == 8)
        {
            amountToAdd = 4;
        }
        else if (width == 10)
        {
            amountToAdd = 6;
        }
        else if (width == 12)
        {
            amountToAdd = 6;
        }
        else if (width == 14)
        {
            amountToAdd = 8;
        }
        else if (width == 16)
        {
            amountToAdd = 12;
        }

        AddRandomlyGeneratedSquares(SquareTypeGrouped.Rocks, amountToAdd, false);
        AddRandomlyGeneratedSquares(SquareTypeGrouped.Trees, amountToAdd, false);
        AddRandomlyGeneratedSquares(SquareTypeGrouped.Mine, amountToAdd - (width == 16 ? 2 : 0), false);
        
        bool addOne = width == 8 || width == 10 || width == 16;
        AddRandomlyGeneratedGaiaObjects<Treasure>(amountToAdd + (addOne ? 1 : 0), false);

        SpawnKings(false);

        _map.SetSeed();

        return _map;
    }

    public Map GenerateFromSeed(string seed)
    {
        bool isMirrored = seed[0] == 'm';
        string[] wxh = seed.Split('_')[1].Split('x');
        _map = new Map(int.Parse(wxh[0]), int.Parse(wxh[1]), isMirrored) { Seed = seed };

        MakeBaseSquares();

        int currentSquareId = 0;

        foreach (char c in seed.Split('_')[2])
        {
            if (int.TryParse(c.ToString(), out int emptySquares))
            {
                currentSquareId += emptySquares;
                continue;
            }

            Square currentSquare = _map.Squares.Single(e => e.Id == currentSquareId);

            if (c == 'k')
            {
                bool isTopHalf = currentSquareId >= _map.Width * _map.Height / 2;
                currentSquare.SetObject(isTopHalf ? new BlackKing() : new WhiteKing());
            }
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

        if (isMirrored)
        {
            MirrorGeneratedHalf();            
        }

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

    public void SpawnKings(bool isMirrored = true)
    {
        Square whiteKingSquare;

        if (isMirrored)
        {
            whiteKingSquare = _map.GetRandomEmptySquare(0, _map.Height * _map.Width / 2 - 1);
            
            // Prevent kings from spawning in the middle 4 squares
            while ((whiteKingSquare.X == _map.Width / 2 || whiteKingSquare.X == _map.Width / 2 - 1) && whiteKingSquare.Y == _map.Height / 2 - 1)
                whiteKingSquare = _map.GetRandomEmptySquare(0, _map.Height * _map.Width / 2 - 1);
        }
        else
        {
            // Prevent kings from spawning in the column closest to the enemy half
            whiteKingSquare = _map.GetRandomEmptySquare(0, _map.Height * _map.Width / 2 - 1 - _map.Height);
        }

        whiteKingSquare.SetObject(new WhiteKing());

        Square blackKingSquare;

        if (isMirrored) 
        {
            blackKingSquare = _map.Squares.Single(e => e.Id == _map.Height * _map.Width - 1 - whiteKingSquare.Id);
        }
        else // Full random
        {        
            blackKingSquare = _map.GetRandomEmptySquare(_map.Height * _map.Width / 2 - 1 + _map.Height, _map.Height * _map.Width - 1);
        }

        blackKingSquare.SetObject(new BlackKing());
    }

    public void AddRandomlyGeneratedSquares(SquareTypeGrouped squareTypeGrouped, double fractionOfMap, bool isMirrored = true)
    {
        int amountToAdd = (int)Math.Round(fractionOfMap * _map.Squares.Count * (isMirrored ? 0.5 : 1));
        if (amountToAdd == 0) amountToAdd = 1;

        for (int i = 0; i < amountToAdd; i++)
        {
            var isDirt = _random.Next(2) == 0;
            var unoccupiedType = isDirt ? SquareType.Dirt : SquareType.Grass;
            
            SquareType squareType;
            if (squareTypeGrouped == SquareTypeGrouped.Rocks)
            {
                squareType = isDirt ? SquareType.DirtRocks : SquareType.GrassRocks;
            }
            else if (squareTypeGrouped == SquareTypeGrouped.Trees)
            {
                squareType = isDirt ? SquareType.DirtTrees : SquareType.GrassTrees;
            }
            else if (squareTypeGrouped == SquareTypeGrouped.Mine)
            {
                squareType = isDirt ? SquareType.DirtMine : SquareType.GrassMine;
            }
            else
            {
                throw new Exception("Unknown square type");
            }

            if (isMirrored)
            {
                _map.GetRandomSquareOfType(unoccupiedType, 0, _map.Height * _map.Width / 2 - 1).SetType(squareType);                
            }
            else // Full random
            {
                _map.GetRandomSquareOfType(unoccupiedType, 0, _map.Height * _map.Width - 1).SetType(squareType); 
            }
        }
    }

    public void AddRandomlyGeneratedGaiaObjects<T>(double fractionOfEmptySquares, bool isMirrored = true) where T : GaiaObject, new()
    {
        var emptySquares = _map.GetSquaresByType(SquareType.Dirt).Concat(_map.GetSquaresByType(SquareType.Grass));
        int amountToAdd = (int)Math.Round(fractionOfEmptySquares * emptySquares.Count() * (isMirrored ? 1 : 2));

        if (amountToAdd < 2) amountToAdd = 2;

        for (int i = 0; i < amountToAdd; i++)
        {
            if (isMirrored)
            {
                _map.GetRandomEmptySquare(0, _map.Height * _map.Width / 2 - 1).SetObject(new T());             
            }
            else // Full random
            {
                _map.GetRandomEmptySquare(0, _map.Height * _map.Width - 1).SetObject(new T());
            }
        }
    }

    public void AddRandomlyGeneratedSquares(SquareTypeGrouped squareTypeGrouped, int amount, bool isMirrored = true)
    {
        int amountToAdd = (int)Math.Round(amount * (isMirrored ? 0.5 : 1));

        for (int i = 0; i < amountToAdd; i++)
        {
            var isDirt = _random.Next(2) == 0;
            var unoccupiedType = isDirt ? SquareType.Dirt : SquareType.Grass;
            
            SquareType squareType;
            if (squareTypeGrouped == SquareTypeGrouped.Rocks)
            {
                squareType = isDirt ? SquareType.DirtRocks : SquareType.GrassRocks;
            }
            else if (squareTypeGrouped == SquareTypeGrouped.Trees)
            {
                squareType = isDirt ? SquareType.DirtTrees : SquareType.GrassTrees;
            }
            else if (squareTypeGrouped == SquareTypeGrouped.Mine)
            {
                squareType = isDirt ? SquareType.DirtMine : SquareType.GrassMine;
            }
            else
            {
                throw new Exception("Unknown square type");
            }

            if (isMirrored)
            {
                _map.GetRandomSquareOfType(unoccupiedType, 0, _map.Height * _map.Width / 2 - 1).SetType(squareType);                
            }
            else // Full random
            {
                _map.GetRandomSquareOfType(unoccupiedType, 0, _map.Height * _map.Width - 1).SetType(squareType); 
            }
        }
    }

    public void AddRandomlyGeneratedGaiaObjects<T>(int amount, bool isMirrored = true) where T : GaiaObject, new()
    {
        int amountToAdd = (int)Math.Round(amount * (isMirrored ? 0.5 : 1));

        for (int i = 0; i < amountToAdd; i++)
        {
            if (isMirrored)
            {
                _map.GetRandomEmptySquare(0, _map.Height * _map.Width / 2 - 1).SetObject(new T());             
            }
            else // Full random
            {
                _map.GetRandomEmptySquare(0, _map.Height * _map.Width - 1).SetObject(new T());
            }
        }
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
