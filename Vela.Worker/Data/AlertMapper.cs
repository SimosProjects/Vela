namespace Vela.Worker.Data;

/// <summary>
/// Maps Alert DTOs and their associated risk evaluation results to AlertEntity instances
/// for database storage. Keeps mapping logic in one place so changes to either the DTO
/// or entity shape only require updates here.
/// </summary>
public static class AlertMapper
{
    /// <summary>
    /// Converts a normalized Alert DTO and its risk evaluation result into an AlertEntity
    /// suitable for database storage.
    /// </summary>
    public static AlertEntity ToEntity(Alert alert, RiskResult riskResult) =>
        new()
        {
            Id = alert.Id!,
            UserId = alert.UserId,
            UserName = alert.UserName,
            XScore = alert.XScore,
            Symbol = alert.Symbol,
            Type = alert.Type,
            Direction = alert.Direction,
            Strike = alert.Strike,
            Expiration = alert.Expiration,
            OptionsContractSymbol = alert.OptionsContractSymbol,
            ContractDescription = alert.ContractDescription,
            Side = alert.Side,
            Status = alert.Status,
            Result = alert.Result,
            ActualPriceAtTimeOfAlert = alert.ActualPriceAtTimeOfAlert,
            PricePaid = alert.PricePaid,
            PriceAtExit = alert.PriceAtExit,
            LastCheckedPrice = alert.LastCheckedPrice,
            LastKnownPercentProfit = alert.LastKnownPercentProfit,
            PriceTarget = alert.PriceTarget,
            Risk = alert.Risk,
            IsProfitableTrade = alert.IsProfitableTrade,
            CanAverage = alert.CanAverage,
            TimeOfEntryAlert = DateTimeOffset.TryParse(alert.TimeOfEntryAlert, out var entryTime)
                ? entryTime.ToUniversalTime() : null,
            TimeOfFullExitAlert = DateTimeOffset.TryParse(alert.TimeOfFullExitAlert, out var fullExitTime)
                ? fullExitTime.ToUniversalTime() : null,
            FormattedLength = alert.FormattedLength,
            IsSwing = alert.IsSwing,
            IsBullish = alert.IsBullish,
            IsShort = alert.IsShort,
            Strategy = alert.Strategy,
            OriginalMessage = alert.OriginalMessage,
            OriginalExitMessage = alert.OriginalExitMessage,
            IngestedAt = DateTimeOffset.UtcNow,
            RiskApproved = riskResult.Approved,
            RiskReason = riskResult.Reason
        };
}