using DotNetApiPi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DotNetApiPi.Api.Integration.Tests;

/// <summary>
/// Hosts the real application pipeline (composition root, middleware,
/// controllers) over an in-process test server, using a private SQLite
/// database file so tests never touch the developer's default database.
/// <para>
/// All test-time configuration is applied by overriding
/// <see cref="ConfigureWebHost"/> (the factory's documented extension point
/// for the <c>WebApplicationBuilder</c> path); settings applied through
/// <c>WithWebHostBuilder</c> in the constructor are applied too early and are
/// clobbered by the factory's own defaults.
/// </para>
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// The hosting environment the app should run under.
    /// </summary>
    private readonly string _environment;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiFactory"/> class.
    /// </summary>
    /// <param name="environment">
    /// The hosting environment to run the app under (defaults to
    /// Development, which also disables the production rate limiter).
    /// </param>
    public ApiFactory(string environment = "Development")
    {
        _environment = environment;

        DatabaseFile = Path.Combine(
            Path.GetTempPath(),
            $"dotnet-api-pi-tests-{Guid.NewGuid():N}.db");
    }

    /// <summary>
    /// Gets the private SQLite database file used by this factory.
    /// </summary>
    public string DatabaseFile { get; }

    /// <summary>
    /// Applies the test configuration: hosting environment and a private
    /// SQLite database for the storage provider.
    /// </summary>
    /// <param name="builder">The web host builder for the application under test.</param>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseEnvironment(_environment);
        builder.UseSetting(
            $"{PersistenceOptions.SectionName}:SqliteConnectionString",
            $"Data Source={DatabaseFile}");
        builder.UseSetting(
            $"{PersistenceOptions.SectionName}:Provider",
            StorageProvider.Sqlite.ToString());
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && File.Exists(DatabaseFile))
        {
            try
            {
                File.Delete(DatabaseFile);
            }
            catch (IOException)
            {
                // Best effort: the file is in a temp directory and will be
                // cleaned up by the OS.
            }
        }
    }
}
