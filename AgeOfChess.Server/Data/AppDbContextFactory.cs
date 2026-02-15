using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgeOfChess.Server.Data;

/// <summary>
/// Used only by the EF Core design-time tools (dotnet ef migrations add / database update).
/// Specifying a fixed MySQL version avoids the live connection that ServerVersion.AutoDetect
/// requires, so migrations can be generated without a running database.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var connStr = config.GetConnectionString("Default")
            ?? "Server=localhost;Database=age_of_chess;User=root;Password=;";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connStr, new MySqlServerVersion(new Version(8, 0)))
            .Options;

        return new AppDbContext(options);
    }
}
