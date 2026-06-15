namespace Vela.AlertPoC.RiskEngine;

public class RiskEngineService
{
    private readonly IReadOnlyList<IRiskRule> _rules;

    // Rules are injected as a list so order is deterministic - cheaper filter rules (entry check) 
    // run before more expensive ones (e.g. price thresholds) to short-circuit as early as possible
    public RiskEngineService(IEnumerable<IRiskRule> rules)
    {
        _rules = rules.ToList().AsReadOnly();
    }

    /// <summary>
    /// Evaluates the given alert against all configured risk rules.
    /// Returns the first failure encountered, or an acceptance if all rules pass.
    /// </summary>
    /// <param name="alert"></param>
    /// <returns></returns>
    public RiskResult Evaluate(Alert alert)
    {
        foreach (var rule in _rules)
        {
            var result = rule.Evaluate(alert);
            if (!result.Passed)
            {
                return RiskResult.Reject(result.Reason);
            }
        }

        return RiskResult.Accept();
    }
}
