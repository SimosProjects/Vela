
namespace Vela.Worker.Configuration;

/// <summary>
/// Enforces cross-field invariants on <see cref="RiskEngineOptions"/> that
/// DataAnnotations cannot express. Registered as IValidateOptions and executed
/// at startup via ValidateOnStart, so an invalid configuration fails the boot
/// instead of surfacing mid-session.
/// </summary>
public class RiskEngineOptionsValidator : IValidateOptions<RiskEngineOptions>
{
    /// <summary>
    /// Validates budget ordering, post-fill trail tightness, and loss limit signs.
    /// </summary>
    public ValidateOptionsResult Validate(string? name, RiskEngineOptions options)
    {
        var failures = new List<string>();

        // Budget ordering: lotto sits below average, average sits below initial.
        if (options.OptionsLottoBudget > options.OptionsInitialBudget)
            failures.Add(
                $"OptionsLottoBudget ({options.OptionsLottoBudget}) must not exceed " +
                $"OptionsInitialBudget ({options.OptionsInitialBudget}).");

        if (options.OptionsLottoAverageBudget > options.OptionsAverageBudget)
            failures.Add(
                $"OptionsLottoAverageBudget ({options.OptionsLottoAverageBudget}) must not exceed " +
                $"OptionsAverageBudget ({options.OptionsAverageBudget}).");

        if (options.OptionsAverageBudget > options.OptionsInitialBudget)
            failures.Add(
                $"OptionsAverageBudget ({options.OptionsAverageBudget}) must not exceed " +
                $"OptionsInitialBudget ({options.OptionsInitialBudget}).");
 
        if (options.OptionsHighBudget > options.OptionsInitialBudget)
            failures.Add(
                $"OptionsHighBudget ({options.OptionsHighBudget}) must not exceed " +
                $"OptionsInitialBudget ({options.OptionsInitialBudget}).");
 
        if (options.OptionsHighBudget < options.OptionsLottoBudget)
            failures.Add(
                $"OptionsHighBudget ({options.OptionsHighBudget}) must not be below " +
                $"OptionsLottoBudget ({options.OptionsLottoBudget}).");
 
        if (options.OptionsHighAverageBudget > options.OptionsAverageBudget)
            failures.Add(
                $"OptionsHighAverageBudget ({options.OptionsHighAverageBudget}) must not exceed " +
                $"OptionsAverageBudget ({options.OptionsAverageBudget}).");
 
        if (options.OptionsHighAverageBudget < options.OptionsLottoAverageBudget)
            failures.Add(
                $"OptionsHighAverageBudget ({options.OptionsHighAverageBudget}) must not be below " +
                $"OptionsLottoAverageBudget ({options.OptionsLottoAverageBudget}).");

        if (options.StockAverageBudget > options.StockInitialBudget)
            failures.Add(
                $"StockAverageBudget ({options.StockAverageBudget}) must not exceed " +
                $"StockInitialBudget ({options.StockInitialBudget}).");

        // Post-fill tightening only makes sense if the tightened trail is at least
        // as tight as the tier trails it replaces. Both values use 0 as disabled.
        if (options.PostFillSlippageWarningPct > 0 && options.HighSlippageTrailPct > 0)
        {
            if (options.HighSlippageTrailPct > options.OptionsStandardTrailPct)
                failures.Add(
                    $"HighSlippageTrailPct ({options.HighSlippageTrailPct}) must not be looser than " +
                    $"OptionsStandardTrailPct ({options.OptionsStandardTrailPct}).");

            if (options.HighSlippageTrailPct > options.OptionsHighTrailPct)
                failures.Add(
                    $"HighSlippageTrailPct ({options.HighSlippageTrailPct}) must not be looser than " +
                    $"OptionsHighTrailPct ({options.OptionsHighTrailPct}).");

            if (options.HighSlippageTrailPct > options.OptionsLottoTrailPct)
                failures.Add(
                    $"HighSlippageTrailPct ({options.HighSlippageTrailPct}) must not be looser than " +
                    $"OptionsLottoTrailPct ({options.OptionsLottoTrailPct}).");
        }

        // Loss limits activate on negative values and disable at 0. A positive
        // value is a misconfiguration that would silently never trigger.
        if (options.DailyLossLimit > 0)
            failures.Add(
                $"DailyLossLimit ({options.DailyLossLimit}) must be negative to activate or 0 to disable.");

        if (options.ChopDailyLossLimit > 0)
            failures.Add(
                $"ChopDailyLossLimit ({options.ChopDailyLossLimit}) must be negative to activate or 0 to disable.");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}