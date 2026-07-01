namespace Vela.Worker.Data;

/// <summary>
/// Represents a trade alert ingested from Xtrades, along with all relevant metadata
/// for risk assessment and tracking through the trade lifecycle.
/// </summary>
public class AlertEntity
{
    // Primary key
    public string Id { get; set; } = string.Empty;

    // -- Trader --
    public string? UserId   { get; set; }
    public string? UserName { get; set; }
    public double? XScore   { get; set; }

    // -- Instrument --
    public string? Symbol              { get; set; }
    // "commons" or "options"
    public string? Type                { get; set; }
    // "call", "put", or "none"
    public string? Direction           { get; set; }
    public decimal? Strike             { get; set; }
    public string? Expiration          { get; set; }
    public string? OptionsContractSymbol { get; set; }
    public string? ContractDescription { get; set; }

    // -- Trade action --
    // "bto", "stc", "sto", "btc"
    public string? Side   { get; set; }
    public string? Status { get; set; }
    // "win", "loss", "inProgress"
    public string? Result { get; set; }

    // -- Pricing --
    public decimal? ActualPriceAtTimeOfAlert { get; set; }
    public decimal? PricePaid               { get; set; }
    public decimal? PriceAtExit             { get; set; }
    public decimal? LastCheckedPrice        { get; set; }
    public decimal? LastKnownPercentProfit  { get; set; }

    // Computed price target supplied by the alert source. When non-null and above entry price,
    // PositionSizer uses this directly instead of the configured target multiplier.
    // Populated by Spyglass based on setup type; always null for Xtrades alerts.
    public decimal? PriceTarget { get; set; }

    // -- Risk --
    // "standard", "high", "lotto"
    public string? Risk             { get; set; }
    public bool? IsProfitableTrade  { get; set; }
    public bool? CanAverage         { get; set; }

    // -- Timing --
    public DateTimeOffset? TimeOfEntryAlert    { get; set; }
    public DateTimeOffset? TimeOfFullExitAlert { get; set; }
    // "DAY", "SWING", "LT"
    public string? FormattedLength { get; set; }

    // -- Characteristics --
    public bool? IsSwing   { get; set; }
    public bool? IsBullish { get; set; }
    public bool? IsShort   { get; set; }
    public string? Strategy { get; set; }

    // -- Messages --
    public string? OriginalMessage     { get; set; }
    public string? OriginalExitMessage { get; set; }

    // -- Pipeline metadata --
    public DateTimeOffset IngestedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool RiskApproved  { get; set; }
    public string? RiskReason { get; set; }
}