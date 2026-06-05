namespace TradeFlow.AlertPoC.Models;

// Flexible response wrapper — Xtrades hasn't published a formal API contract,
// so we cover the three most common root-level list field names seen in practice.
// Once the real shape is confirmed, the unused properties can be removed.
public record AlertsResponse(
    [property: JsonPropertyName("alerts")]     List<Alert>? Alerts,
    [property: JsonPropertyName("data")]       List<Alert>? Data,
    [property: JsonPropertyName("items")]      List<Alert>? Items,
    [property: JsonPropertyName("totalCount")] int          TotalCount
);

// Nested userMeta object, carries the Discord rank display name.
public record AlertUserMeta(
    [property: JsonPropertyName("discordRankDisplayName")] string? DiscordRankDisplayName
);

// Immutable DTO representing a single Xtrades alert off the wire.
// Modelled as a record so value-based equality works for deduplication
// without needing to override Equals/GetHashCode manually.
// Nullable properties reflect the API reality — commons alerts carry no
// strike, expiry, or contract symbol.
public record Alert(
    // Identity
    [property: JsonPropertyName("id")]                               string?  Id,
    [property: JsonPropertyName("userId")]                           string?  UserId,
    [property: JsonPropertyName("userName")]                         string?  UserName,

    // Instrument
    [property: JsonPropertyName("symbol")]                           string?  Symbol,

    // "commons" or "options" — drives downstream classification
    [property: JsonPropertyName("type")]                             string?  Type,

    // "call", "put", or "none" for commons
    [property: JsonPropertyName("direction")]                        string?  Direction,

    [property: JsonPropertyName("strike")]                           decimal? Strike,
    [property: JsonPropertyName("expiration")]                       string?  Expiration,
    [property: JsonPropertyName("optionsContractSymbol")]            string?  OptionsContractSymbol,
    [property: JsonPropertyName("contractDescription")]              string?  ContractDescription,

    // Trade action
    // "bto", "stc", "sto", "btc"
    [property: JsonPropertyName("side")]                             string?  Side,
    [property: JsonPropertyName("status")]                           string?  Status,

    // "win", "loss", "inProgress"
    [property: JsonPropertyName("result")]                           string?  Result,

    // Pricing
    [property: JsonPropertyName("actualPriceAtTimeOfAlert")]         decimal? ActualPriceAtTimeOfAlert,
    [property: JsonPropertyName("actualPriceAtTimeOfExit")]          decimal? ActualPriceAtTimeOfExit,
    [property: JsonPropertyName("pricePaid")]                        decimal? PricePaid,
    [property: JsonPropertyName("priceAtExit")]                      decimal? PriceAtExit,
    [property: JsonPropertyName("highestPriceAfterAlertBeforeExit")] decimal? HighestPrice,
    [property: JsonPropertyName("lowestPriceAfterAlertBeforeExit")]  decimal? LowestPrice,
    [property: JsonPropertyName("lastCheckedPrice")]                 decimal? LastCheckedPrice,

    // Risk & performance
    // "standard", "high", "lotto"
    [property: JsonPropertyName("risk")]                             string?  Risk,
    [property: JsonPropertyName("lastKnownPercentProfit")]           decimal? LastKnownPercentProfit,
    [property: JsonPropertyName("isProfitableTrade")]                bool?    IsProfitableTrade,
    [property: JsonPropertyName("xscore")]                           double?  XScore,
    [property: JsonPropertyName("canAverage")]                       bool?    CanAverage,

    // Timing
    // Stored as string to preserve timezone offset — parse to DateTimeOffset downstream
    [property: JsonPropertyName("timeOfEntryAlert")]                 string?  TimeOfEntryAlert,
    [property: JsonPropertyName("timeOfFullExitAlert")]              string?  TimeOfFullExitAlert,

    // "DAY", "SWING", "LT" — useful for position sizing rules later
    [property: JsonPropertyName("formattedLength")]                  string?  FormattedLength,

    // Trade characteristics
    [property: JsonPropertyName("isSwing")]                          bool?    IsSwing,
    [property: JsonPropertyName("isBullish")]                        bool?    IsBullish,
    [property: JsonPropertyName("isShort")]                          bool?    IsShort,
    [property: JsonPropertyName("strategy")]                         string?  Strategy,

    // Messages
    [property: JsonPropertyName("originalMessage")]                  string?  OriginalMessage,
    [property: JsonPropertyName("originalExitMessage")]              string?  OriginalExitMessage,

    // Nested user metadata, source for DiscordRank after normalization
    [property: JsonPropertyName("userMeta")]                         AlertUserMeta? UserMeta = null,
    string? DiscordRank = null
);