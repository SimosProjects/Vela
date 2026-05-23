using Microsoft.EntityFrameworkCore.Design;

namespace TradeFlow.Worker.Data;

public class TradeFlowDbContextFactory : IDesignTimeDbContextFactory<TradeFlowDbContext>
{
    public TradeFlowDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TradeFlowDbContext>()
            .UseNpgsql("Host=localhost;Database=tradeflow;Username=tradeflow_user;Password=tradeflow_dev")
            .Options;

        return new TradeFlowDbContext(options);
    }
}