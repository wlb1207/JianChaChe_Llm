using System.Globalization;

namespace LlmWpfPrototype.Services;

internal static class DimensionReadbackVerifier
{
    // SolidWorks system length units are metres; tolerance = 0.00001 mm.
    internal static bool Matches(object? raw, double expected, out double actual)
    {
        actual = double.NaN;
        if (raw is null || !double.IsFinite(expected)) return false;
        try
        {
            var value = raw is Array values && values.Length == 1 ? values.GetValue(0) : raw;
            actual = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch { return false; }
        return double.IsFinite(actual) && Math.Abs(actual - expected) <= 1e-8;
    }
}
