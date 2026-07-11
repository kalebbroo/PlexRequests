using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PlexRequestsHosted.Infrastructure.Data;

/// <summary>
/// Design-time factory used by `dotnet ef` (migrations/scaffolding) so the tooling can build the
/// context without executing the application's Program.cs startup. The connection string here is a
/// placeholder for the provider only — migrations don't touch a real database.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=app.db")
            .Options;
        return new AppDbContext(options);
    }
}
