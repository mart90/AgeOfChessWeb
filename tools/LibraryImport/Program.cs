// Imports games into a user's library ("My library").
//
//   cd AgeOfChess.Server   (uses its appsettings.Development.json for the database)
//   dotnet run -c Release --project ../tools/LibraryImport -- --user mart --input games.json [--dry-run] [--dump packed.json]
//
// games.json: [{ "title": "...", "seed": "m_10x10_...", "moves": ["e2-e4", "c3=N", ...],
//                "result": "b+g" (optional, checked against the replay), "blackGold": 6 (optional) }]
// Moves use the server/engine notation. Each game is replayed with the server's GameLogic and
// stored as positions in the client's packed tree format (client/src/lib/gameTransfer.js).

using System.Text.Json;
using System.Text.Json.Nodes;
using AgeOfChess.Server.Data;
using AgeOfChess.Server.Data.Models;
using AgeOfChess.Server.GameLogic;
using AgeOfChess.Server.GameLogic.PlaceableObjects.Pieces;
using AgeOfChess.Server.Services;
using Microsoft.EntityFrameworkCore;

string? Arg(string name)
{
    int i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var username = Arg("user") ?? throw new ArgumentException("--user NAME");
var input    = Arg("input") ?? throw new ArgumentException("--input FILE");
bool dryRun  = args.Contains("--dry-run");

var games = JsonSerializer.Deserialize<List<ImportGame>>(File.ReadAllText(input),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

var entries = new List<LibraryGame>();
foreach (var g in games)
{
    var (states, result, error) = Replay(g);
    if (error != null)
    {
        Console.Error.WriteLine($"SKIP \"{g.Title}\": {error}");
        continue;
    }
    if (g.Result != null && result != g.Result)
        Console.Error.WriteLine($"WARN \"{g.Title}\": replay result {result ?? "none"}, expected {g.Result}");
    entries.Add(new LibraryGame
    {
        Title    = g.Title.Length > 200 ? g.Title[..200] : g.Title,
        MapSize  = (int)Math.Round(Math.Sqrt(states[0].Squares.Count)),
        MapSeed  = g.Seed,
        PlyCount = states.Count - 1,
        Result   = result,
        DataJson = Pack(states).ToJsonString(new JsonSerializerOptions { MaxDepth = 100_000 }),
    });
    Console.WriteLine($"ok   \"{g.Title}\" ({states.Count - 1} plies, {result})");
}

if (Arg("dump") is string dump)   // write the packed entries (for checking them with the client code)
    File.WriteAllText(dump, "[" + string.Join(",", entries.Select(e => e.DataJson)) + "]");

if (dryRun)
{
    Console.WriteLine($"dry run: {entries.Count} games not saved");
    return;
}

using var db = new AppDbContextFactory().CreateDbContext([]);
var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username)
    ?? throw new InvalidOperationException($"No user '{username}'");
foreach (var e in entries) e.UserId = user.Id;
db.LibraryGames.AddRange(entries);
await db.SaveChangesAsync();
Console.WriteLine($"saved {entries.Count} games to {username}'s library");

// ── Replay with the server rules (same steps as GameController's replay endpoint) ──
static (List<GameStateDto> states, string? result, string? error) Replay(ImportGame g)
{
    var settings = new GameSettings { MapSeed = g.Seed, TimeControlEnabled = false, BiddingEnabled = false };
    var game = new ServerGame(0, settings, "w", "b", g.White ?? "White", g.Black ?? "Black");
    if (g.BlackGold is int bg) game.Black.Gold = bg;
    var states = new List<GameStateDto> { GameStateDtoBuilder.Build(game) };
    foreach (var notation in g.Moves)
    {
        if (game.GameEnded) return (states, game.Result, $"moves continue after the game ended (ply {states.Count})");
        var clean = notation.TrimEnd('+', '#');
        bool applied;
        if (clean.Contains('='))
        {
            int eq = clean.IndexOf('=');
            var (x, y) = Square(clean[..eq]);
            Type? piece = clean[(eq + 1)..].ToLower() switch
            {
                "q" => typeof(Queen), "r" => typeof(Rook), "b" => typeof(Bishop),
                "n" => typeof(Knight), "p" => typeof(Pawn), _ => null,
            };
            applied = piece != null && game.TryPlacePiece(x, y, piece);
        }
        else
        {
            char sep = clean.Contains('x') ? 'x' : '-';
            int i = clean.IndexOf(sep);
            var (fx, fy) = Square(clean[..i]);
            var (tx, ty) = Square(clean[(i + 1)..]);
            applied = game.TryMovePiece(fx, fy, tx, ty);
        }
        if (!applied) return (states, null, $"illegal move '{notation}' at ply {states.Count}");
        game.EndTurn();
        game.StartNewTurn();
        states.Add(GameStateDtoBuilder.Build(game));
    }
    return (states, game.Result, null);
}

static (int x, int y) Square(string sq) => (sq[0] - 'a', int.Parse(sq[1..]) - 1);

// ── Packed tree (a single mainline), see packTree in client/src/lib/gameTransfer.js ──
static JsonObject Pack(List<GameStateDto> states)
{
    var terrain = new JsonArray();
    foreach (var s in states[0].Squares) terrain.Add(new JsonArray(s.X, s.Y, s.Type));
    var index = states[0].Squares.Select((s, i) => (s, i)).ToDictionary(t => (t.s.X, t.s.Y), t => t.i);

    JsonObject? root = null, parent = null;
    for (int ply = 0; ply < states.Count; ply++)
    {
        var st = states[ply];
        var p = new JsonArray();
        var o = new JsonArray();
        foreach (var s in st.Squares)
        {
            int i = index[(s.X, s.Y)];
            if (s.Piece != null) p.Add(new JsonArray(i, s.Piece.Type, s.Piece.IsWhite ? 1 : 0));
            if (s.MineOwner != null) o.Add(new JsonArray(i, s.MineOwner == "white" ? 1 : 0));
        }
        var node = new JsonObject
        {
            ["san"] = ply == 0 ? null : st.Moves[ply - 1],
            ["s"] = new JsonObject { ["p"] = p, ["o"] = o, ["wg"] = st.White.Gold, ["bg"] = st.Black.Gold, ["wa"] = st.White.IsActive ? 1 : 0 },
            ["m"] = 1,
            ["c"] = new JsonArray(),
        };
        if (parent == null) root = node;
        else ((JsonArray)parent["c"]!).Add(node);
        parent = node;
    }
    return new JsonObject { ["terrain"] = terrain, ["tree"] = root };
}

record ImportGame(string Title, string Seed, List<string> Moves, string? Result, int? BlackGold, string? White, string? Black);
