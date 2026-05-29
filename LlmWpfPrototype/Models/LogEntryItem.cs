namespace LlmWpfPrototype.Models;

public sealed class LogEntryItem
{
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string Level { get; set; } = "Info";

    public string Category { get; set; } = "System";

    public string Action { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string Exception { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string DisplayText
    {
        get
        {
            var actionPart = string.IsNullOrWhiteSpace(Action) ? string.Empty : $" [{Action}]";
            var sourcePart = string.IsNullOrWhiteSpace(Source) ? string.Empty : $" ({Source})";
            var exceptionPart = string.IsNullOrWhiteSpace(Exception) ? string.Empty : $"{Environment.NewLine}Exception: {Exception}";
            return $"[{Timestamp:HH:mm:ss}] [{Level}] [{Category}]{actionPart} {Message}{sourcePart}{exceptionPart}";
        }
    }
}
