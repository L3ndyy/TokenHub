namespace TokenHub.Services;

/// <summary>
/// The dark/light colour set as plain hex strings. It lives outside <see cref="Theme"/>
/// on purpose: the window knows how to apply it, a test can read it, and both then talk
/// about the same numbers. Nothing here touches WPF.
/// </summary>
public static class Palette
{
    public static IReadOnlyList<(string Key, string Hex)> For(bool dark, string accent) => new[]
    {
        ("Br.Layer", dark ? "#59000000" : "#F2F4F8F8"),
        ("Br.Popup", dark ? "#FF1B1B1F" : "#FFFFFFFF"),
        ("Br.Card", dark ? "#10FFFFFF" : "#E6FFFFFF"),
        ("Br.CardBorder", dark ? "#12FFFFFF" : "#0E000000"),
        ("Br.Text", dark ? "#FFFFFFFF" : "#E61A1A1F"),
        ("Br.TextSecondary", dark ? "#B0FFFFFF" : "#EE5F6368"),
        ("Br.Control", dark ? "#14FFFFFF" : "#FFF2F4F9"),
        ("Br.ControlHover", dark ? "#22FFFFFF" : "#FFFFFFFF"),
        ("Br.ControlBorder", dark ? "#1AFFFFFF" : "#12000000"),
        ("Br.Accent", accent),
        ("Br.OnAccent", dark ? "#FF0A0A0A" : "#FFFFFFFF"),
        ("Br.AccentBorder", dark ? "#33FFFFFF" : "#1A000000"),
        ("Br.Success", dark ? "#FF6CCB5F" : "#FF0F7B0F"),
        ("Br.Danger", dark ? "#FFFF99A4" : "#FFC42B1C"),
        ("Br.DangerSoft", dark ? "#33C42B1C" : "#1AC42B1C"),
        ("Br.DangerBorder", dark ? "#66C42B1C" : "#4DC42B1C"),
        ("Br.Muted", dark ? "#8AFFFFFF" : "#8F000000"),
        ("Br.ScrollTrack", "#00000000"),
        ("Br.ScrollThumb", dark ? "#33FFFFFF" : "#33000000"),
        ("Br.ScrollThumbHover", dark ? "#66FFFFFF" : "#55000000"),
    };
}
