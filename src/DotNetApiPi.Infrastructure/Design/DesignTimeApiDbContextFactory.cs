using DotNetApiPi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DotNetApiPi.Infrastructure.Design;

/// <summary>
/// Supplies an <see cref="ApiDbContext"/> to EF Core design-time tooling
/// (<c>dotnet ef migrations add ...</c>) without booting the application host.
/// The command only needs the model (the internal design-time constructor is
/// enough); the connection string mirrors the default in
/// <c>appsettings.json</c> so the context can also be inspected live.
/// </summary>
public sealed class DesignTimeApiDbContextFactory : IDesignTimeDbContextFactory<ApiDbContext>
{
    /// <inheritdoc />
    public ApiDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApiDbContext>()
            .UseSqlite("Data Source=dotnet_api_pi.db")
            .Options;

        return new ApiDbContext(options);
    }
}
