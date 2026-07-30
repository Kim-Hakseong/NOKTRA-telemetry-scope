using Avalonia.Media;

namespace Ts.App.Theme;

/// <summary>
/// The single definition of the Noktra palette, shared across the product line.
///
/// The XAML token dictionary reads these through <c>x:Static</c> and the chart renderer reads them
/// directly, so a colour exists in exactly one place. Duplicating hex values into a resource file
/// would guarantee the two drift apart the first time one is tuned.
///
/// Semantics, not names, decide what goes where:
/// <list type="bullet">
/// <item><see cref="Accent"/> is the only accent in the chrome. If a second colour starts meaning
/// "look here", the first one stops meaning it.</item>
/// <item><see cref="Ink"/> backgrounds are reserved for elements that <em>name</em> something -
/// channel ids, status badges, statistic labels.</item>
/// <item><see cref="Alert"/> and <see cref="Warn"/> are desaturated on purpose so a warning does
/// not shout louder than the data.</item>
/// </list>
/// </summary>
public static class NoktraPalette
{
    // --- surfaces
    public static readonly Color CanvasTop = Color.FromRgb(0xBC, 0xBF, 0xC0);
    public static readonly Color CanvasBottom = Color.FromRgb(0xA9, 0xAD, 0xAE);
    public static readonly Color Panel = Color.FromRgb(0xF3, 0xF4, 0xF5);
    public static readonly Color PanelSunk = Color.FromRgb(0xE7, 0xE9, 0xEA);
    public static readonly Color PanelEdge = Color.FromRgb(0xFF, 0xFF, 0xFF);
    public static readonly Color LegalBar = Color.FromRgb(0xEF, 0xEE, 0xE9);

    // --- ink
    public static readonly Color Ink = Color.FromRgb(0x0E, 0x11, 0x13);
    public static readonly Color InkSoft = Color.FromRgb(0x2B, 0x30, 0x33);
    public static readonly Color Muted = Color.FromRgb(0x7E, 0x85, 0x88);
    public static readonly Color Line = Color.FromRgb(0xCD, 0xD1, 0xD3);
    public static readonly Color LineFaint = Color.FromRgb(0xDE, 0xE1, 0xE2);

    // --- the one accent
    public static readonly Color Accent = Color.FromRgb(0x1E, 0x7C, 0x8C);
    public static readonly Color AccentBright = Color.FromRgb(0x31, 0xA9, 0xBC);
    public static readonly Color AccentWash = Color.FromRgb(0xD8, 0xE8, 0xEA);

    // --- states
    public static readonly Color Warn = Color.FromRgb(0xB9, 0x86, 0x2F);
    public static readonly Color Alert = Color.FromRgb(0xA8, 0x41, 0x2F);
    public static readonly Color Ok = Color.FromRgb(0x2F, 0x7D, 0x5C);
    public static readonly Color Reserved = Color.FromRgb(0x9A, 0xA1, 0xA4);

    // --- on dark chips
    public static readonly Color OnInk = Color.FromRgb(0xF3, 0xF4, 0xF5);
    public static readonly Color OnInkMuted = Color.FromRgb(0x8B, 0x92, 0x95);

    /// <summary>
    /// Trace colours for the scope, in assignment order.
    ///
    /// This is the one place the "single accent" rule is extended, and deliberately: a scope with
    /// eight traces has to tell them apart, and a legend swatch is the only honest way to do it.
    /// The ramp stays inside the same desaturated register as the rest of the palette so no trace
    /// out-shouts the data, and the selected trace is what gets the accent plus a heavier stroke —
    /// so "which one am I reading" is still answered by one colour.
    /// </summary>
    public static readonly Color[] Traces =
    {
        Color.FromRgb(0x1E, 0x7C, 0x8C), // accent teal
        Color.FromRgb(0xA8, 0x41, 0x2F), // alert clay
        Color.FromRgb(0x4A, 0x5B, 0x8C), // indigo
        Color.FromRgb(0xB9, 0x86, 0x2F), // warn ochre
        Color.FromRgb(0x2F, 0x7D, 0x5C), // ok green
        Color.FromRgb(0x7E, 0x5A, 0x8C), // plum
        Color.FromRgb(0x31, 0xA9, 0xBC), // accent bright
        Color.FromRgb(0x2B, 0x30, 0x33), // ink soft
    };

    public static Color TraceColor(int index) => Traces[((index % Traces.Length) + Traces.Length) % Traces.Length];

    // Frozen brushes: the chart hands these to the renderer many times per frame, and a new
    // SolidColorBrush per read would allocate on every repaint.
    public static readonly IBrush AccentBrush = Frozen(Accent);
    public static readonly IBrush AccentBrightBrush = Frozen(AccentBright);
    public static readonly IBrush AccentWashBrush = Frozen(AccentWash);
    public static readonly IBrush InkBrush = Frozen(Ink);
    public static readonly IBrush InkSoftBrush = Frozen(InkSoft);
    public static readonly IBrush MutedBrush = Frozen(Muted);
    public static readonly IBrush LineBrush = Frozen(Line);
    public static readonly IBrush LineFaintBrush = Frozen(LineFaint);
    public static readonly IBrush PanelBrush = Frozen(Panel);
    public static readonly IBrush PanelSunkBrush = Frozen(PanelSunk);
    public static readonly IBrush WarnBrush = Frozen(Warn);
    public static readonly IBrush AlertBrush = Frozen(Alert);
    public static readonly IBrush OkBrush = Frozen(Ok);
    public static readonly IBrush ReservedBrush = Frozen(Reserved);
    public static readonly IBrush OnInkBrush = Frozen(OnInk);

    public static IBrush Frozen(Color color) => new SolidColorBrush(color).ToImmutable();
}
