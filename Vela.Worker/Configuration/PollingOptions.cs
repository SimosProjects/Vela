using System.ComponentModel.DataAnnotations;

namespace Vela.Worker.Configuration;

public class PollingOptions
{
    public const string SectionName = "Polling";

    [Range(5, 300, ErrorMessage = "IntervalSeconds must be between 5 and 300.")]
    public int IntervalSeconds { get; init; } = 30;
}