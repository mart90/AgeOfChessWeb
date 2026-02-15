using AgeOfChess.Server.GameLogic.PlaceableObjects;

namespace AgeOfChess.Server.GameLogic.Map;

public class Square
{
    public int X { get; }
    public int Y { get; }
    public int Id { get; }
    public SquareType Type { get; private set; }
    public bool IsSelected { get; set; }
    public PlaceableObject? Object { get; private set; }
    public SquareColor TemporaryColor { get; private set; }

    public Square(int x, int y, int id)
    {
        X = x;
        Y = y;
        Id = id;
    }

    public void SetType(SquareType squareType)
    {
        Type = squareType;
    }

    public void SetObject(PlaceableObject? obj)
    {
        Object = obj;
    }

    public void ClearObject()
    {
        Object = null;
    }

    public void SetTemporaryColor(SquareColor color)
    {
        TemporaryColor = color;
    }

    public void ClearTemporaryColor()
    {
        TemporaryColor = SquareColor.None;
    }
}
