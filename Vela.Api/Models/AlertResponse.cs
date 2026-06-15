namespace Vela.Api.Models;

/// <summary>
/// Alert data returned by the API. Deliberately separate from the Alert entity to allow 
/// for flexibility in shaping the response without affecting the underlying data model. 
/// Changes to one should not force changes to the other.
/// </summary>
/// <param name="Id"></param>
/// <param name="UserName"></param>
/// <param name="XScore"></param>
/// <param name="Symbol"></param>
/// <param name="Side"></param>
/// <param name="Direction"></param>
/// <param name="Type"></param>
/// <param name="Risk"></param>
/// <param name="Strike"></param>
/// <param name="Expiration"></param>
/// <param name="ContractDescription"></param>
/// <param name="PricePaid"></param>
/// <param name="LastCheckedPrice"></param>
/// <param name="LastKnownPercentProfit"></param>
/// <param name="Result"></param>
/// <param name="FormattedLength"></param>
/// <param name="OriginalMessage"></param>
/// <param name="TimeOfEntryAlert"></param>
/// <param name="RiskApproved"></param>
/// <param name="RiskReason"></param>
/// <param name="IngestedAt"></param>
public record AlertResponse
(
    string? Id,
    string? UserName,
    double? XScore,
    string? Symbol,
    string? Side,
    string? Direction,
    string? Type,
    string? Risk,
    decimal? Strike,
    string? Expiration,
    string? ContractDescription,
    decimal? PricePaid,
    decimal? LastCheckedPrice,
    decimal? LastKnownPercentProfit,
    string? Result,
    string? FormattedLength,
    string? OriginalMessage,
    DateTimeOffset? TimeOfEntryAlert,
    bool RiskApproved,
    string? RiskReason,
    DateTimeOffset? IngestedAt
);
