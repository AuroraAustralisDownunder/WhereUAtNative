namespace WhereUAtNative.Services;

/// <summary>
/// Local-only display prefs for the self map pin (alias + colour). Not uploaded.
/// </summary>
public static class UserDisplayPreferences
{
    public const string AliasPreferenceKey = "self_alias_name";
    public const string PinColorPreferenceKey = "self_pin_color";

    public const string DefaultAlias = "You";
    /// <summary>Light purple — calm default (not aggressive green/red).</summary>
    public const string DefaultPinColor = "#B794F6";

    /// <summary>Simple preset hex colours offered in Settings.</summary>
    public static readonly (string Name, string Hex)[] PinColorChoices =
    {
        ("Light purple", "#B794F6"),
        ("Soft blue", "#64B5F6"),
        ("Soft teal", "#4DB6AC"),
        ("Soft pink", "#F48FB1"),
        ("Soft orange", "#FFB74D"),
        ("Muted green", "#81C784"),
        ("Soft indigo", "#7986CB"),
        ("Soft coral", "#E57373"),
        ("Soft cyan", "#4DD0E1"),
        ("Soft amber", "#FFD54F"),
        ("Soft lilac", "#CE93D8"),
        ("Soft lime", "#DCE775"),
        ("Soft taupe", "#A1887F"),
        ("Soft slate", "#90A4AE"),
        ("Soft peach", "#FF8A65"),
        ("Soft mint", "#AED581"),
        ("Soft sky", "#4FC3F7"),
    };

    public static string GetAlias()
    {
        try
        {
            var value = Preferences.Default.Get(AliasPreferenceKey, DefaultAlias);
            value = (value ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(value))
                return DefaultAlias;
            // Keep labels short for the map chip above the pin.
            return value.Length > 24 ? value[..24].Trim() : value;
        }
        catch
        {
            return DefaultAlias;
        }
    }

    public static void SetAlias(string? alias)
    {
        try
        {
            var value = (alias ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(value))
                value = DefaultAlias;
            if (value.Length > 24)
                value = value[..24].Trim();
            Preferences.Default.Set(AliasPreferenceKey, value);
        }
        catch
        {
            // Preferences unavailable — keep in-memory only at call sites.
        }
    }

    public static string GetPinColor()
    {
        try
        {
            var value = Preferences.Default.Get(PinColorPreferenceKey, DefaultPinColor);
            if (string.IsNullOrWhiteSpace(value) || !IsAllowedColor(value))
                return DefaultPinColor;
            return NormalizeHex(value);
        }
        catch
        {
            return DefaultPinColor;
        }
    }

    public static void SetPinColor(string? hex)
    {
        try
        {
            var value = NormalizeHex(hex);
            if (!IsAllowedColor(value))
                value = DefaultPinColor;
            Preferences.Default.Set(PinColorPreferenceKey, value);
        }
        catch
        {
            // ignore
        }
    }

    public static bool IsAllowedColor(string? hex)
    {
        var n = NormalizeHex(hex);
        foreach (var (_, h) in PinColorChoices)
        {
            if (string.Equals(n, NormalizeHex(h), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static string NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return DefaultPinColor;
        var t = hex.Trim();
        if (!t.StartsWith('#'))
            t = "#" + t;
        return t.ToUpperInvariant();
    }
}
