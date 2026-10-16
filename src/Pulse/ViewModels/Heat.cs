using Pulse.Services;

namespace Pulse.ViewModels;

/// <summary>How close a card's main reading is to its limit: drives the color of the card's border.</summary>
public enum HeatLevel
{
    None,
    Warm,
    Hot,
}

/// <summary>
/// The heat rules, shared with the alerts (step 4): a reading at or over its alert limit is Hot, within
/// <see cref="WarmBand"/> of it is Warm. The limits come from the user's alert rules, so raising the CPU
/// alert to 100 °C also moves where the Processor card turns red.
///
/// A small margin keeps a reading that hovers right at a boundary from flickering the border.
/// </summary>
public static class Heat
{
    /// <summary>Warm this far below the limit (°C for temperatures).</summary>
    public const double WarmBand = 10;

    /// <summary>How far back below a boundary a reading must go before the border calms down.</summary>
    public const double Margin = 2;

    public const string CpuRule = AlertRule.BuiltInPrefix + "cpu";
    public const string GpuRule = AlertRule.BuiltInPrefix + "gpu-hotspot";
    public const string DriveRule = AlertRule.BuiltInPrefix + "drives";
    public const string PumpRule = AlertRule.BuiltInPrefix + "pump";

    /// <summary>The limit of a built-in alert rule (on or off: it's still the user's idea of too hot).</summary>
    public static double? Limit(IEnumerable<AlertRule> rules, string ruleId) =>
        rules.FirstOrDefault(r => r.Id == ruleId)?.Threshold;

    /// <summary>For readings where higher is worse (temperatures, power): Hot at the limit, Warm within <paramref name="band"/>.</summary>
    public static HeatLevel Above(double? value, double? limit, HeatLevel current, double band = WarmBand, double margin = Margin)
    {
        if (value is not { } v || limit is not { } hot)
            return HeatLevel.None;
        var warm = hot - band;

        // Leaving a level takes going a little further back than entering it did.
        var hotEdge = current == HeatLevel.Hot ? hot - margin : hot;
        var warmEdge = current >= HeatLevel.Warm ? warm - margin : warm;
        return v >= hotEdge ? HeatLevel.Hot : v >= warmEdge ? HeatLevel.Warm : HeatLevel.None;
    }

    /// <summary>For readings where lower is worse (pump speed): Hot below the limit.</summary>
    public static HeatLevel Below(double? value, double? limit, HeatLevel current, double margin) =>
        value is not { } v || limit is not { } low ? HeatLevel.None
        : v < (current == HeatLevel.Hot ? low + margin : low) ? HeatLevel.Hot
        : HeatLevel.None;
}
