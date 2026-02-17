using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using AgeOfChess.Server.Data;
using AgeOfChess.Server.Data.Models;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace AgeOfChess.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(AppDbContext db, IConfiguration config) : ControllerBase
{
    public record RegisterRequest(
        string Username,
        string Password,
        string? DisplayName,
        bool DisplayNameSameAsUsername = false
    );

    public record LoginRequest(string Username, string Password);
    public record GoogleAuthRequest(string Credential);

    // POST /api/auth/register
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Length < 3)
            return BadRequest("Username must be at least 3 characters.");

        if (req.Username.Length > 20)
            return BadRequest("Username must be at most 20 characters.");

        if (!Regex.IsMatch(req.Username, @"^[a-zA-Z0-9_\-]+$"))
            return BadRequest("Username may only contain letters, numbers, underscores and hyphens.");

        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
            return BadRequest("Password must be at least 6 characters.");

        if (await db.Users.AnyAsync(u => u.Username == req.Username))
            return Conflict("Username already taken.");

        string? displayName = req.DisplayNameSameAsUsername || string.IsNullOrWhiteSpace(req.DisplayName)
            ? null
            : req.DisplayName!.Trim();

        var user = new User
        {
            Username     = req.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
            DisplayName  = displayName,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return Ok(new { token = GenerateJwt(user), user = MapUser(user) });
    }

    // POST /api/auth/login
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == req.Username);

        if (user == null || user.PasswordHash == null
                         || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
            return Unauthorized("Invalid credentials.");

        return Ok(new { token = GenerateJwt(user), user = MapUser(user) });
    }

    // POST /api/auth/google
    [HttpPost("google")]
    public async Task<IActionResult> GoogleSignIn([FromBody] GoogleAuthRequest req)
    {
        var clientId = config["Google:ClientId"];
        if (string.IsNullOrEmpty(clientId))
            return StatusCode(501, "Google sign-in is not configured on this server.");

        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(
                req.Credential,
                new GoogleJsonWebSignature.ValidationSettings { Audience = new[] { clientId } });
        }
        catch
        {
            return Unauthorized("Invalid Google credential.");
        }

        // Find an existing Google-linked account
        var user = await db.Users.FirstOrDefaultAsync(u => u.GoogleId == payload.Subject);

        if (user == null)
        {
            // Auto-create account from Google profile
            var baseUsername = SanitizeUsername(payload.Name ?? payload.Email ?? "user");
            var username     = await EnsureUniqueUsername(baseUsername);

            user = new User
            {
                Username    = username,
                GoogleId    = payload.Subject,
                Email       = payload.Email,
                DisplayName = payload.Name?.Trim(),
            };

            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        return Ok(new { token = GenerateJwt(user), user = MapUser(user) });
    }

    // GET /api/auth/me — returns the current user's profile
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user   = await db.Users.FindAsync(userId);
        if (user == null) return NotFound();
        return Ok(MapUser(user));
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    internal static object MapUser(User user) => new
    {
        user.Id,
        user.Username,
        displayName       = user.DisplayName,   // null → client falls back to username
        user.EloBlitz,
        user.EloRapid,
        user.EloSlow,
        user.BlitzGamesPlayed,
        user.RapidGamesPlayed,
        user.SlowGamesPlayed,
        user.GamesPlayed,
        hasPassword       = user.PasswordHash != null,
        hasGoogle         = user.GoogleId != null,
    };

    private string GenerateJwt(User user)
    {
        var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
        var token = new JwtSecurityToken(
            claims: new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username)
            },
            expires: DateTime.UtcNow.AddDays(30),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        );
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string SanitizeUsername(string raw)
    {
        var clean = Regex.Replace(raw, @"[^a-zA-Z0-9_\-]", "_").Trim('_');
        if (clean.Length < 3) clean = "user_" + clean;
        if (clean.Length > 20) clean = clean[..20];
        return clean;
    }

    private async Task<string> EnsureUniqueUsername(string baseUsername)
    {
        if (!await db.Users.AnyAsync(u => u.Username == baseUsername))
            return baseUsername;

        for (int i = 2; ; i++)
        {
            var candidate = $"{baseUsername[..Math.Min(baseUsername.Length, 17)]}_{i}";
            if (!await db.Users.AnyAsync(u => u.Username == candidate))
                return candidate;
        }
    }
}
