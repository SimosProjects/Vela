using System.Text.Json;
using Microsoft.Extensions.Options;
using Vela.Api.Models;
using Vela.Worker.Configuration;

namespace Vela.Api;

public static class ConfigEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static void MapConfigEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/config").WithTags("Config");

        group.MapGet("/risk",  GetRiskConfig).WithName("GetRiskConfig")
             .WithSummary("Returns current risk config — DB save if present, otherwise appsettings baseline.");

        group.MapPost("/risk", SaveRiskConfig).WithName("SaveRiskConfig")
             .WithSummary("Persists risk config overrides to DB. Applied on next Worker restart.");
    }

    private static async Task<IResult> GetRiskConfig(
        VelaDbContext db,
        IOptions<RiskEngineOptions> riskOptions,
        CancellationToken ct)
    {
        var baseline = RiskConfigDto.FromOptions(riskOptions.Value);

        var saved = await db.RiskConfigOverrides
            .FirstOrDefaultAsync(r => r.Id == 1, ct);

        if (saved is null || saved.ConfigJson == "{}")
            return Results.Ok(baseline);

        try
        {
            var dto = JsonSerializer.Deserialize<RiskConfigDto>(saved.ConfigJson, JsonOpts);
            return Results.Ok(dto ?? baseline);
        }
        catch
        {
            return Results.Ok(baseline);
        }
    }

    private static async Task<IResult> SaveRiskConfig(
        RiskConfigDto body,
        VelaDbContext db,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        var now  = DateTimeOffset.UtcNow;

        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO risk_config_overrides (id, config_json, updated_at)
            VALUES (1, {json}, {now})
            ON CONFLICT (id) DO UPDATE
              SET config_json = EXCLUDED.config_json,
                  updated_at  = EXCLUDED.updated_at
            """,
            ct);

        return Results.Ok(body);
    }
}