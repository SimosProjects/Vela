using Vela.Api.Models;

namespace Vela.Api;

/// <summary>
/// Registers all alert-related endpoints on the application.
/// This class serves as a central place to define and organize all 
/// API routes related to alerts, making it easier to maintain and extend in the future. 
/// </summary>
public static class AlertEndpoints
{
    public static void MapAlertEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/alerts")
            .WithTags("Alerts");

        group.MapGet("/", GetAlerts)
            .WithName("GetAlerts")
            .WithSummary("Returns a paginated list of ingested alerts.")
            .CacheOutput("alerts");

        group.MapGet("/{id}", GetAlertById)
            .WithName("GetAlertById")
            .WithSummary("Returns a specific ingested alert by Xtrades ID.");
    }

    /// <summary>
    /// Returns a paginated list of ingested alerts, with optional filtering 
    /// by user name, symbol, side, and risk approval status.
    /// </summary>
    /// <param name="db"></param>
    /// <param name="userName"></param>
    /// <param name="symbol"></param>
    /// <param name="side"></param>
    /// <param name="riskApproved"></param>
    /// <param name="page"></param>
    /// <param name="pageSize"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private static async Task<IResult> GetAlerts(
        VelaDbContext db,
        string? userName = null,
        string? symbol = null,
        string? side = null,
        bool? riskApproved = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        // Basic validation for pagination parameters
        if (page < 1)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["page"] = ["Page number must be greater than 0."]
                }
            );
        }

        if (pageSize < 1 || pageSize > 100)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["pageSize"] = ["Page size must be between 1 and 100."]
                }
            );
        }

        // Build the query with optional filters
        IQueryable<AlertEntity> query = db.Alerts;

        if (userName is not null)
        {
            query = query.Where(a => a.UserName == userName);
        }

        if (symbol is not null)
        {
            query = query.Where(a => a.Symbol == symbol);
        }

        if (side is not null)
        {
            query = query.Where(a => a.Side == side);
        }

        if (riskApproved is not null)
        {
            query = query.Where(a => a.RiskApproved == riskApproved);
        }

        // Get total count before pagination
        var totalAlerts = await query.CountAsync(cancellationToken);

        // Apply pagination and project to AlertResponse
        var alerts = await query
            .OrderByDescending(a => a.TimeOfEntryAlert)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AlertResponse(
                a.Id,
                a.UserName,
                a.XScore,
                a.Symbol,
                a.Side,
                a.Direction,
                a.Type,
                a.Risk,
                a.Strike,
                a.Expiration,
                a.ContractDescription,
                a.PricePaid,
                a.LastCheckedPrice,
                a.LastKnownPercentProfit,
                a.Result,
                a.FormattedLength,
                a.OriginalMessage,
                a.TimeOfEntryAlert,
                a.RiskApproved,
                a.RiskReason,
                a.IngestedAt
            ))
            .ToListAsync(cancellationToken);

        return Results.Ok(new
        {
            totalAlerts,
            page,
            pageSize,
            data = alerts
        });
    }

    /// <summary>
    /// Returns a specific ingested alert by Xtrades ID. If the alert is not found,
    /// returns a 404 Not Found response with a message indicating the alert was not found.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="db"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private static async Task<IResult> GetAlertById(
        string id,
        VelaDbContext db,
        CancellationToken cancellationToken = default)
    {
        var alert = await db.Alerts
            .Where(a => a.Id == id)
            .Select(a => new AlertResponse(
                a.Id,
                a.UserName,
                a.XScore,
                a.Symbol,
                a.Side,
                a.Direction,
                a.Type,
                a.Risk,
                a.Strike,
                a.Expiration,
                a.ContractDescription,
                a.PricePaid,
                a.LastCheckedPrice,
                a.LastKnownPercentProfit,
                a.Result,
                a.FormattedLength,
                a.OriginalMessage,
                a.TimeOfEntryAlert,
                a.RiskApproved,
                a.RiskReason,
                a.IngestedAt
            ))
            .FirstOrDefaultAsync(cancellationToken);

        return alert is null
            ? Results.NotFound(new { message = $"Alert with ID '{id}' not found." })
            : Results.Ok(alert);
    }
}