using Microsoft.AspNetCore.Mvc;
using Vela.Api.Models;
using Vela.Worker.Data;

namespace Vela.Api;

/// <summary>Registers all Spyglass inbound endpoints on the application.</summary>
public static class SpyglassEndpoints
{
    /// <summary>
    /// Adds Spyglass routes under <c>/api/v1/spyglass</c>.
    /// All routes require a valid Bearer token validated by <see cref="SpyglassApiKeyFilter"/>.
    /// </summary>
    public static void MapSpyglassEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/spyglass")
            .AddEndpointFilter<SpyglassApiKeyFilter>()
            .WithTags("Spyglass");

        group.MapPost("/alerts", HandleSpyglassAlerts)
            .WithName("PostSpyglassAlerts")
            .WithSummary(
                "Accepts a Spyglass scan cycle envelope and persists " +
                "each alert for Worker pickup.");
    }

    // -- Helpers --

    private static async Task<IResult> HandleSpyglassAlerts(
        SpyglassEnvelope envelope,
        [FromServices] IAlertRepository repo,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(nameof(SpyglassEndpoints));

        if (envelope.Alerts.Count == 0)
            return Results.Ok(new { accepted = 0 });

        var entities = envelope.Alerts.Select(item => new AlertEntity
        {
            Id = $"spyglass-{item.Symbol}-{envelope.EmittedAt:yyyyMMddHHmmssfff}-{item.Id}",
            UserName = "SPYGLASS",
            XScore = 100.0,
            Symbol = item.Symbol,
            Type = "commons",
            Direction = null,
            Side = "bto",
            Risk = "standard",
            IsSwing = true,
            FormattedLength = "SWING",
            ActualPriceAtTimeOfAlert = item.CurrentPrice,
            PricePaid = null,
            PriceTarget = item.PriceTarget,
            TimeOfEntryAlert = envelope.EmittedAt.ToUniversalTime(),
            Strategy = string.Join(", ", item.Setups),
            OriginalMessage = $"SPYGLASS: {item.Symbol} {string.Join("+", item.Setups)} score={item.Score:F3}",
            IngestedAt = DateTimeOffset.UtcNow,
            RiskApproved = false,
            RiskReason = "spyglass_pending",
        }).ToList();

        await repo.SaveManyAsync(entities, ct);

        logger.LogInformation(
            "Spyglass sink: {Count} alert(s) received from scan emitted at {EmittedAt}.",
            entities.Count, envelope.EmittedAt);

        return Results.Ok(new { accepted = entities.Count });
    }
}