using System;

namespace Stellar.CooldownBar;

/// <summary>
/// Whole-px geometry of a tile footer WITH the gold count chip (seconds + chip side by side), Unity-free so the fit is
/// unit-tested. The pair must fit the tile's inner width with at least 2 px to spare on each side for the WORST case:
/// the widest seconds label CdTimeFormat produces ("99m" / "16m") next to the widest chip ("99+", ChipText caps there).
/// <para>
/// Widths come from the real font: the chip-arm texts are TMP bold = the framework's shipped StellarUIThaiBold.ttf
/// (Noto Sans Bold, 1000 upm), measured with fontTools (advance widths, 2026-11-13):
/// every digit and '+' = 0.572 em, "99m" = "16m" = 2.126 em, "9.8s" = 1.926, "57s" = 1.641, "2h" = 1.229, "99+" = 1.716,
/// "99" = 1.144. "∞" is NOT in that face (TMP draws it from a fallback face) — it is a single glyph, far below 2.126 em
/// in any font, and the seconds box is right-aligned, so even a wide fallback ∞ grows LEFT (into the free margin),
/// never into the chip or past the tile's right edge.
/// </para>
/// At 100 % the old footer (legacy 14 px seconds in a 28 px box + gap 2 + a hugging chip) needed 28 + 2 + 17 = 47 px of
/// a 44 px cell for a two-digit count, and there is no width for a 14 px seconds label beside "99+" at all — so with a
/// chip the seconds drop to a smaller TMP bold size that fits (<see cref="SecsFont"/>), and a 3-character chip uses a
/// smaller font than 1–2 characters (<see cref="ChipFont3"/>) inside one FIXED chip width, so the chip never changes
/// size as the count changes.
/// </summary>
internal readonly struct TileFooterFit
{
    public const float DigitEm      = 0.572f;   // every digit and '+', Noto Sans Bold
    public const float WidestSecsEm = 2.126f;   // "99m" / "16m" — the widest CdTimeFormat label (no '*' fallback mark)

    public readonly float Margin, Gap, ChipPad, ChipW, ChipLabelW, SecsW;
    public readonly int   ChipFont2, ChipFont3, SecsFont;

    /// <summary><paramref name="s"/> = tile size (1 = 100 %), <paramref name="innerW"/> = the footer cell width,
    /// <paramref name="chipH"/> = the chip height (a 1–2 digit chip is never narrower than it is tall: a circle).</summary>
    public TileFooterFit(float s, float innerW, float chipH)
    {
        Margin  = MathF.Max(2f, Px(2f * s));
        Gap     = MathF.Max(1f, Px(1f * s));
        ChipPad = MathF.Max(1f, Px(1f * s));
        ChipFont2 = (int)Px(10f * s);
        ChipFont3 = (int)Px(8f * s);
        // The label box fits "99" at ChipFont2 and "99+" at ChipFont3; the chip = box + padding, at least a circle.
        float label = MathF.Ceiling(MathF.Max(2f * DigitEm * ChipFont2, 3f * DigitEm * ChipFont3));
        ChipW      = MathF.Max(label + 2f * ChipPad, chipH);
        ChipLabelW = ChipW - 2f * ChipPad;
        // The seconds take every px that is left, and the largest whole font whose widest label fits it (never larger
        // than the chip-less 12·s, so the seconds only ever get smaller when a chip appears).
        SecsW    = innerW - 2f * Margin - Gap - ChipW;
        SecsFont = Math.Min((int)Px(12f * s), (int)MathF.Floor(SecsW / WidestSecsEm));
    }

    /// <summary>The chip text: exact 1–99, "99+" from 100 (the widest label the fixed chip is sized for).</summary>
    public static string ChipText(int n)
        => n >= 100 ? "99+" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static float Px(float v) => MathF.Round(v, MidpointRounding.AwayFromZero);
}
