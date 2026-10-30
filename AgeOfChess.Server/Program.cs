using System.Text;
using AgeOfChess.Server.Data;
using AgeOfChess.Server.Hubs;
using AgeOfChess.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Database (MySQL via Pomelo)
var connStr = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connStr, ServerVersion.AutoDetect(connStr)));

// JWT authentication — optional for most routes, required for future ranked features
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is required in configuration.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer = false,
            ValidateAudience = false,
        };
        // Allow JWT via query string for SignalR (browsers can't set headers on WS connections)
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hub"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddControllers();
// Frequent pings let clients notice a dead connection quickly (client/src/lib/hub.js)
builder.Services.AddSignalR(o => o.KeepAliveInterval = TimeSpan.FromSeconds(5));

// In-memory active game store (singleton — lives for the process lifetime)
builder.Services.AddSingleton<GameSessionManager>();
builder.Services.AddSingleton<MatchmakingService>();
builder.Services.AddSingleton<GameFlowService>();
builder.Services.AddScoped<GameCreationService>();
builder.Services.AddScoped<GameResumeService>();
builder.Services.AddHostedService<GameCleanupService>();

var app = builder.Build();

// Reload in-progress slow games from the database before accepting connections
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<GameResumeService>().ResumeSlowGames();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
// index.html must be revalidated on every load: it names the hashed bundles of the current build,
// and a stale copy points at files the next deploy deletes. Vite's hashed bundles
// (/assets/name-HASH.ext) never change, so they can be cached for good.
var staticFileOptions = new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value ?? "";
        var headers = ctx.Context.Response.Headers;
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path == "/")
            headers.CacheControl = "no-cache";
        else if (System.Text.RegularExpressions.Regex.IsMatch(path, @"^/assets/[^/]+-[A-Za-z0-9_-]{8}\.\w+$"))
            headers.CacheControl = "public, max-age=31536000, immutable";
    }
};
app.UseStaticFiles(staticFileOptions);

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<GameHub>("/hub/game");
app.MapHub<MatchmakingHub>("/hub/matchmaking");

// Fallback to index.html for Svelte client-side routing
app.MapFallbackToFile("index.html", staticFileOptions);

app.Run();
