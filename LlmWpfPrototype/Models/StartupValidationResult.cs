namespace LlmWpfPrototype.Models;

public sealed class StartupValidationResult
{
    public bool IsValid { get; init; }

    public string Message { get; init; } = string.Empty;

    public static StartupValidationResult Valid(string message)
    {
        return new StartupValidationResult
        {
            IsValid = true,
            Message = message
        };
    }

    public static StartupValidationResult Invalid(string message)
    {
        return new StartupValidationResult
        {
            IsValid = false,
            Message = message
        };
    }
}
