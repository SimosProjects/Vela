using Microsoft.EntityFrameworkCore;

namespace TradeFlow.Worker.Data;

/// <summary>
/// Entity Framework Core DbContext for the TradeFlow application,
/// representing the database session and providing access to the Alerts and TradeMetrics tables.
/// </summary>
public class TradeFlowDbContext : DbContext
{
    public TradeFlowDbContext(DbContextOptions<TradeFlowDbContext> options)
        : base(options) { }

    public DbSet<AlertEntity> Alerts { get; set; }
    public DbSet<TradeMetric> TradeMetrics { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlertEntity>(entity =>
        {
            entity.ToTable("alerts");
            entity.HasKey(a => a.Id);

            entity.HasIndex(a => a.UserName)
                  .HasDatabaseName("idx_alerts_username");

            entity.HasIndex(a => a.Symbol)
                  .HasDatabaseName("idx_alerts_symbol");

            entity.HasIndex(a => a.TimeOfEntryAlert)
                  .HasDatabaseName("idx_alerts_time_of_entry");

            entity.HasIndex(a => new { a.Side, a.TimeOfEntryAlert })
                  .HasDatabaseName("idx_alerts_side_time");

            entity.Property(a => a.Id).HasColumnName("id");
            entity.Property(a => a.UserId).HasColumnName("user_id");
            entity.Property(a => a.UserName).HasColumnName("user_name");
            entity.Property(a => a.XScore).HasColumnName("xscore");
            entity.Property(a => a.Symbol).HasColumnName("symbol");
            entity.Property(a => a.Type).HasColumnName("type");
            entity.Property(a => a.Direction).HasColumnName("direction");
            entity.Property(a => a.Strike).HasColumnName("strike");
            entity.Property(a => a.Expiration).HasColumnName("expiration");
            entity.Property(a => a.OptionsContractSymbol).HasColumnName("options_contract_symbol");
            entity.Property(a => a.ContractDescription).HasColumnName("contract_description");
            entity.Property(a => a.Side).HasColumnName("side");
            entity.Property(a => a.Status).HasColumnName("status");
            entity.Property(a => a.Result).HasColumnName("result");
            entity.Property(a => a.ActualPriceAtTimeOfAlert).HasColumnName("actual_price_at_time_of_alert");
            entity.Property(a => a.PricePaid).HasColumnName("price_paid");
            entity.Property(a => a.PriceAtExit).HasColumnName("price_at_exit");
            entity.Property(a => a.LastCheckedPrice).HasColumnName("last_checked_price");
            entity.Property(a => a.LastKnownPercentProfit).HasColumnName("last_known_percent_profit");
            entity.Property(a => a.Risk).HasColumnName("risk");
            entity.Property(a => a.IsProfitableTrade).HasColumnName("is_profitable_trade");
            entity.Property(a => a.CanAverage).HasColumnName("can_average");
            entity.Property(a => a.TimeOfEntryAlert).HasColumnName("time_of_entry_alert");
            entity.Property(a => a.TimeOfFullExitAlert).HasColumnName("time_of_full_exit_alert");
            entity.Property(a => a.FormattedLength).HasColumnName("formatted_length");
            entity.Property(a => a.IsSwing).HasColumnName("is_swing");
            entity.Property(a => a.IsBullish).HasColumnName("is_bullish");
            entity.Property(a => a.IsShort).HasColumnName("is_short");
            entity.Property(a => a.Strategy).HasColumnName("strategy");
            entity.Property(a => a.OriginalMessage).HasColumnName("original_message");
            entity.Property(a => a.OriginalExitMessage).HasColumnName("original_exit_message");
            entity.Property(a => a.IngestedAt).HasColumnName("ingested_at");
            entity.Property(a => a.RiskApproved).HasColumnName("risk_approved");
            entity.Property(a => a.RiskReason).HasColumnName("risk_reason");
        });

        modelBuilder.Entity<TradeMetric>(entity =>
        {
            entity.ToTable("trade_metrics");
            entity.HasKey(m => m.Id);

            // Most analytics queries filter or group by trader, symbol, or time
            entity.HasIndex(m => m.TraderName)
                  .HasDatabaseName("idx_trade_metrics_trader");

            entity.HasIndex(m => m.Symbol)
                  .HasDatabaseName("idx_trade_metrics_symbol");

            entity.HasIndex(m => m.AlertReceivedAt)
                  .HasDatabaseName("idx_trade_metrics_received_at");

            // Allows efficient open-only queries (closed_at IS NULL)
            entity.HasIndex(m => m.ClosedAt)
                  .HasDatabaseName("idx_trade_metrics_closed_at");

            entity.Property(m => m.Id).HasColumnName("id");
            entity.Property(m => m.AlertId).HasColumnName("alert_id");
            entity.Property(m => m.TraderName).HasColumnName("trader_name");
            entity.Property(m => m.Symbol).HasColumnName("symbol");
            entity.Property(m => m.TradeType).HasColumnName("trade_type");
            entity.Property(m => m.Direction).HasColumnName("direction");
            entity.Property(m => m.OptionsContract).HasColumnName("options_contract");
            entity.Property(m => m.IsAverage).HasColumnName("is_average");
            entity.Property(m => m.AlertReceivedAt).HasColumnName("alert_received_at");
            entity.Property(m => m.OrderSubmittedAt).HasColumnName("order_submitted_at");
            entity.Property(m => m.OrderFilledAt).HasColumnName("order_filled_at");
            entity.Property(m => m.LatencyMs).HasColumnName("latency_ms");
            entity.Property(m => m.AlertedPrice).HasColumnName("alerted_price");
            entity.Property(m => m.FillPrice).HasColumnName("fill_price");
            entity.Property(m => m.SlippagePct).HasColumnName("slippage_pct");
            entity.Property(m => m.Quantity).HasColumnName("quantity");
            entity.Property(m => m.EntryAmount).HasColumnName("entry_amount");
            entity.Property(m => m.StopPrice).HasColumnName("stop_price");
            entity.Property(m => m.TargetPrice).HasColumnName("target_price");
            entity.Property(m => m.AccountBalanceAtEntry).HasColumnName("account_balance_at_entry");
            entity.Property(m => m.OpenPositionsValueAtEntry).HasColumnName("open_positions_value_at_entry");
            entity.Property(m => m.ExposurePct).HasColumnName("exposure_pct");
            entity.Property(m => m.ExitPrice).HasColumnName("exit_price");
            entity.Property(m => m.ExitAmount).HasColumnName("exit_amount");
            entity.Property(m => m.Outcome).HasColumnName("outcome");
            entity.Property(m => m.ClosedAt).HasColumnName("closed_at");
            entity.Property(m => m.PnL).HasColumnName("pnl");
            entity.Property(m => m.PnLPct).HasColumnName("pnl_pct");
        });
    }
}
