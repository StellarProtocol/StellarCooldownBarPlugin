using System;

namespace Stellar.CooldownBar;

/// <summary>
/// Whole-px geometry of a tile footer WITH the gold count chip (seconds + chip side by side), Unity-free so the fit is
/// unit-tested. Every width is fixed — Margin + seconds box + Gap + chip + Margin = the footer cell — so the chip's right
/// edge always sits Margin (≥ 2 px) inside the cell, whatever the labels are.
/// <para>
/// The seconds keep the NORMAL tile seconds style beside a chip (user 2026-11-13: same size as a chip-less tile, the
/// bar's shadowed legacy 14 px included — never shrunk). They are right-aligned in their box, so a label wider than the
/// box overflows LEFT (NoWrap = HorizontalWrapMode.Overflow), into the free margin and the card padding, never into the
/// chip. That is what fixes "∞" + a two-digit chip (it used to push the chip past the tile's right edge): "∞" is one
/// glyph (≤ <see cref="InfinityMaxEm"/> in any face; it is NOT in the TMP face, TMP draws it from a fallback), so it
/// fits the box whole and simply sits left of centre beside the pinned chip. A wide time label ("99m" at the bar's
/// legacy 14 px) may overhang the cell's left edge by a few px; accepted (rare, still on the card).
/// </para>
/// Chip font sizes come from the real font: TMP bold = the framework's StellarUIThaiBold.ttf (Noto Sans Bold, 1000 upm),
/// every digit and '+' = 0.572 em (fontTools, 2026-11-13). One chip width serves every count: "1".."99" at
/// <see cref="ChipFont2"/>, "99+" at the smaller <see cref="ChipFont3"/>, so the chip never changes size.
/// </summary>
internal readonly struct TileFooterFit
{
    public const float DigitEm       = 0.572f;   // every digit and '+', Noto Sans Bold
    public const float InfinityMaxEm = 1.0f;     // generous bound for a single fallback-face "∞" glyph

    public readonly float Margin, Gap, ChipPad, ChipW, ChipLabelW, SecsW;
    public readonly int   ChipFont2, ChipFont3;

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
        // The seconds box takes every px that is left; the text in it keeps its normal size and grows left if wider.
        SecsW = innerW - 2f * Margin - Gap - ChipW;
    }

    /// <summary>The chip text: exact 1–99, "99+" from 100 (the widest label the fixed chip is sized for).</summary>
    public static string ChipText(int n)
        => n >= 100 ? "99+" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static float Px(float v) => MathF.Round(v, MidpointRounding.AwayFromZero);
}
