using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Implementations;
using Xunit;

namespace PlexRequests.Tests;

public sealed class CatalogRolloutPreferencesTests
{
    [Fact]
    public async Task Catalog_rollout_modes_are_disabled_by_default_and_persist_independently()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var service = new DownloadPreferencesService(db);
            var defaults = await service.GetAsync();
            Assert.False(defaults.UseCatalogForSearch);
            Assert.False(defaults.UseCatalogForMonitoring);

            defaults.UseCatalogForSearch = true;
            Assert.True(await service.UpdateAsync(defaults));
        }

        await using (var db = new AppDbContext(options))
        {
            var saved = await new DownloadPreferencesService(db).GetAsync();
            Assert.True(saved.UseCatalogForSearch);
            Assert.False(saved.UseCatalogForMonitoring);
        }
    }
}
