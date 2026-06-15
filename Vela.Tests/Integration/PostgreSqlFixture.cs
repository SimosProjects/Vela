using Testcontainers.PostgreSql;

namespace Vela.Tests.Integration;

/// <summary>
/// Manages a real PostgreSQL Docker container for integration tests.
/// The container starts before the first test and stops after the last.
/// IAsyncLifetime ensures proper async startup and teardown.
/// </summary>
public class PostgreSqlFixture : IAsyncLifetime
{
    // Testcontainers builds and manages the Docker container
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("vela_test")
        .WithUsername("test_user")
        .WithPassword("test_password")
        .Build();

    // Connection string exposed to tests after the container starts
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        // Starts the Docker container — takes a few seconds on first run
        // (image pulled), near-instant on subsequent runs (cached)
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        // Stops and removes the container after all tests complete
        await _container.DisposeAsync();
    }
}