namespace Vela.Api;

/// <summary>
/// Endpoint filter that validates the Bearer token on all Spyglass routes.
/// Reads the expected token from <c>Spyglass:ApiToken</c> in configuration.
/// Returns 401 Unauthorized when the header is absent or the token does not match.
/// </summary>
public class SpyglassApiKeyFilter : IEndpointFilter
{
    private readonly string? _expectedToken;

    public SpyglassApiKeyFilter(IConfiguration configuration)
    {
        _expectedToken = configuration["Spyglass:ApiToken"];
    }

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var authHeader = context.HttpContext.Request.Headers.Authorization.ToString();

        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : null;

        if (string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(_expectedToken) ||
            !string.Equals(token, _expectedToken, StringComparison.Ordinal))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }
}