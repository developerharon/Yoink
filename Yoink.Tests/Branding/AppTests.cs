using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Yoink;
using Yoink.Models;

namespace Yoink.Tests.Branding;

public class ToThemeVariantTests
{
    [Theory]
    [InlineData(ThemePreference.System, "Default")]
    [InlineData(ThemePreference.Light, "Light")]
    [InlineData(ThemePreference.Dark, "Dark")]
    public void MapsEachPreferenceToTheExpectedThemeVariant(ThemePreference preference, string expectedName)
    {
        var variant = Yoink.App.ToThemeVariant(preference);
        var expected = expectedName switch
        {
            "Default" => ThemeVariant.Default,
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => throw new System.ArgumentOutOfRangeException(nameof(expectedName)),
        };

        Assert.Equal(expected, variant);
    }
}

/// <summary>
/// App.ApplyAccent is the one thing every accent swatch in Views.SettingsView ends up calling — these
/// verify its actual observable effect on Application.Current's resources for every preset in
/// BRANDING.md, against the real App class via [AvaloniaFact] (see TestAppBuilder).
/// </summary>
public class ApplyAccentTests
{
    [AvaloniaTheory]
    [InlineData(AccentColor.Blue, "#2F6FED")]
    [InlineData(AccentColor.Orange, "#E95420")]
    [InlineData(AccentColor.Purple, "#8B5CF6")]
    [InlineData(AccentColor.Green, "#22A06B")]
    [InlineData(AccentColor.Red, "#E5484D")]
    public void SetsSystemAccentColor_ToThePresetsBaseHex(AccentColor accent, string expectedHex)
    {
        Yoink.App.ApplyAccent(accent);

        var color = Assert.IsType<Color>(Application.Current!.Resources["SystemAccentColor"]);
        Assert.Equal(Color.Parse(expectedHex), color);
    }

    [AvaloniaFact]
    public void AccentBrush_MatchesSystemAccentColor()
    {
        Yoink.App.ApplyAccent(AccentColor.Purple);

        var accentColor = Assert.IsType<Color>(Application.Current!.Resources["SystemAccentColor"]);
        var accentBrush = Assert.IsType<SolidColorBrush>(Application.Current.Resources["AccentBrush"]);
        Assert.Equal(accentColor, accentBrush.Color);
    }

    [AvaloniaFact]
    public void OnAccentBrush_IsAlwaysWhite_RegardlessOfPreset()
    {
        foreach (var accent in System.Enum.GetValues<AccentColor>())
        {
            Yoink.App.ApplyAccent(accent);
            var onAccent = Assert.IsType<SolidColorBrush>(Application.Current!.Resources["OnAccentBrush"]);
            Assert.Equal(Colors.White, onAccent.Color);
        }
    }

    [AvaloniaFact]
    public void CurrentIcon_IsSetAfterApplyingAnAccent()
    {
        Yoink.App.ApplyAccent(AccentColor.Green);

        Assert.NotNull(Yoink.App.CurrentIcon);
    }
}
