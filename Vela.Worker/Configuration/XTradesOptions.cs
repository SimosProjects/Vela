using System.ComponentModel.DataAnnotations;

namespace Vela.Worker.Configuration;

public class XtradesOptions
{
    public const string SectionName = "Xtrades";

    [Range(1, 100, ErrorMessage = "PageSize must be between 1 and 100.")]
    public int PageSize { get; set; } = 10;

    public string DateSpec { get; set; } = "Today";
    public string OrderBy { get; set; } = "TimeOfEntryAlertEpoch desc";
}