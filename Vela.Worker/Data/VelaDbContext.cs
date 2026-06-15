using Microsoft.EntityFrameworkCore;

namespace Vela.Worker.Data;

/// <summary>
/// Entity Framework Core DbContext for the Vela application,
/// representing the database session and providing access to the Alerts, TradeMetrics,
/// OpenPositions, SystemState, and WorkerLogs tables.
/// </summary>
public class VelaDbContext : DbContext
{
    public VelaDbContext(DbContextOptions<VelaDbContext> options)
        : base(options) { }

    public DbSet<AlertEntity> Alerts { get; set; }
    public DbSet<TradeMetric> TradeMetrics { get; set; }
    public DbSet<OpenPosition> OpenPositions { get; set; }
    public DbSet<SystemState> SystemState { get; set; }
    /// <summary>
    /// Dashboard-saved risk config overrides. Single row (id = 1).
    /// Populated by POST /api/config/risk; read by GET /api/config/risk.
    /// </summary>
    public DbSet<RiskConfigOverride> RiskConfigOverrides { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlertEntity>(entity =>
        {
            entity.ToTable("alerts");
            entity.HasKey(a => a.Id);

            entity.HasIndex(a => a.UserName).HasDatabaseName("idx_alerts_username");
            entity.HasIndex(a => a.Symbol).HasDatabaseName("idx_alerts_symbol");
            entity.HasIndex(a => a.TimeOfEntryAlert).HasDatabaseName("idx_alerts_time_of_entry");
            entity.HasIndex(a => new { a.Side, a.TimeOfEntryAlert }).HasDatabaseName("idx_alerts_side_time");

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

            entity.HasIndex(m => m.TraderName).HasDatabaseName("idx_trade_metrics_trader");
            entity.HasIndex(m => m.Symbol).HasDatabaseName("idx_trade_metrics_symbol");
            entity.HasIndex(m => m.AlertReceivedAt).HasDatabaseName("idx_trade_metrics_received_at");
            entity.HasIndex(m => m.ClosedAt).HasDatabaseName("idx_trade_metrics_closed_at");

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
            entity.Property(m => m.ExitLatencyMs).HasColumnName("exit_latency_ms");
            entity.Property(m => m.ExitSlippagePct).HasColumnName("exit_slippage_pct");
        });

        modelBuilder.Entity<OpenPosition>(entity =>
        {
            entity.ToTable("open_positions");
            entity.HasKey(p => p.OrderId);

            entity.Property(p => p.OrderId).HasColumnName("order_id");
            entity.Property(p => p.StopOrderId).HasColumnName("stop_order_id");
            entity.Property(p => p.TargetOrderId).HasColumnName("target_order_id");
            entity.Property(p => p.AlertId).HasColumnName("alert_id");
            entity.Property(p => p.UserName).HasColumnName("user_name");
            entity.Property(p => p.Symbol).HasColumnName("symbol");
            entity.Property(p => p.TradeType).HasColumnName("trade_type");
            entity.Property(p => p.OptionsContract).HasColumnName("options_contract");
            entity.Property(p => p.Direction).HasColumnName("direction");
            entity.Property(p => p.Strike).HasColumnName("strike");
            entity.Property(p => p.Expiration).HasColumnName("expiration");
            entity.Property(p => p.Quantity).HasColumnName("quantity");
            entity.Property(p => p.EntryPrice).HasColumnName("entry_price");
            entity.Property(p => p.EntryAmount).HasColumnName("entry_amount");
            entity.Property(p => p.StopPrice).HasColumnName("stop_price");
            entity.Property(p => p.TargetPrice).HasColumnName("target_price");
            entity.Property(p => p.OpenedAt).HasColumnName("opened_at");
            entity.Property(p => p.IsAverage).HasColumnName("is_average");
            entity.Property(p => p.HasAveraged).HasColumnName("has_averaged");
        });

        modelBuilder.Entity<SystemState>(entity =>
        {
            entity.ToTable("system_state");
            entity.HasKey(s => s.Id);

            entity.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(s => s.RegimeTier).HasColumnName("regime_tier");
            entity.Property(s => s.SizingMultiplier).HasColumnName("sizing_multiplier");
            entity.Property(s => s.BlockCalls).HasColumnName("block_calls");
            entity.Property(s => s.SpyPrice).HasColumnName("spy_price");
            entity.Property(s => s.Ma20).HasColumnName("ma20");
            entity.Property(s => s.Ma50).HasColumnName("ma50");
            entity.Property(s => s.Ma200).HasColumnName("ma200");
            entity.Property(s => s.Vix).HasColumnName("vix");
            entity.Property(s => s.VixDelta).HasColumnName("vix_delta");
            entity.Property(s => s.ChopScore).HasColumnName("chop_score");
            entity.Property(s => s.IsPaused).HasColumnName("is_paused");
            entity.Property(s => s.IbkrConnected).HasColumnName("ibkr_connected");
            entity.Property(s => s.WorkerHeartbeat).HasColumnName("worker_heartbeat");
            entity.Property(s => s.AccountBalance).HasColumnName("account_balance");
            entity.Property(s => s.OpenValue).HasColumnName("open_value");
            entity.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<WorkerLog>(entity =>
        {
            // worker_logs is created and maintained by WorkerLogSink, not EF migrations.
            entity.ToTable("worker_logs", t => t.ExcludeFromMigrations());
            entity.HasKey(l => l.Id);

            entity.Property(l => l.Id).HasColumnName("id");
            entity.Property(l => l.LoggedAt).HasColumnName("logged_at");
            entity.Property(l => l.Level).HasColumnName("level");
            entity.Property(l => l.Message).HasColumnName("message");
            entity.Property(l => l.Exception).HasColumnName("exception");
        });

        modelBuilder.Entity<RiskConfigOverride>(entity =>
        {
            entity.ToTable("risk_config_overrides");
            entity.HasKey(r => r.Id);

            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.ConfigJson).HasColumnName("config_json");
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        });
    }
}