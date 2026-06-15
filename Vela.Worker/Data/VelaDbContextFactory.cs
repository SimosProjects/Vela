using Microsoft.EntityFrameworkCore.Design;

namespace Vela.Worker.Data;

public class VelaDbContextFactory : IDesignTimeDbContextFactory<VelaDbContext>
{
    public VelaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<VelaDbContext>()
            .UseNpgsql("Host=localhost;Database=vela;Username=vela_user;Password=vela_dev")
            .Options;

        return new VelaDbContext(options);
    }
}