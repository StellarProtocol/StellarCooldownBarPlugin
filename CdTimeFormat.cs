namespace Stellar.CooldownBar;

/// <summary>
/// The tile's remaining-time label (approved redesign 2026-11-13), Unity-free so it is unit-tested:
/// <c>&lt; 10 s</c> → tenths "9.8s" · <c>&lt; 100 s</c> → whole seconds "57s" · <c>&lt; 100 min</c> → whole minutes "2m"
/// (124 s → "2m", 999 s → "16m") · else whole hours "1h" · permanent "∞" · a '*' prefix marks the fallback clock.
/// Every step FLOORS, so a countdown never shows a value above the real remainder and never flashes "10.0s" on the way
/// from 10 s down to 9.9 s. <see cref="Key"/> is the integer that drives the text: the bar's per-slot cache
/// (Plugin.Bar.Cache.cs) re-formats only when it changes (≤ 10 times a second under 10 s, once a second below 100 s).
/// </summary>
internal static class CdTimeFormat
{
    private const float TenthsBelow  = 10f;
    private const float SecondsBelow = 100f;
    private const float MinutesBelow = 6000f;   // 100 min

    /// <summary>Which band the value falls in and the floored number shown, packed into one long (+ the '*' bit).</summary>
    public static long Key(float secs, bool permanent, bool fallback)
    {
        long key;
        if (permanent) key = 1;
        else if (secs < TenthsBelow)  key = 2_000_000L + (long)(secs * 10f);
        else if (secs < SecondsBelow) key = 3_000_000L + (long)secs;
        else if (secs < MinutesBelow) key = 4_000_000L + (long)(secs / 60f);
        else                          key = 5_000_000L + (long)(secs / 3600f);
        return key * 2 + (fallback && !permanent ? 1 : 0);
    }

    /// <summary>The label text. Allocates — call only when <see cref="Key"/> changed.</summary>
    public static string Format(float secs, bool permanent, bool fallback)
    {
        if (permanent) return "∞";
        if (secs < 0f) secs = 0f;
        string time;
        if (secs < TenthsBelow)
        {
            int tenths = (int)(secs * 10f);
            time = (tenths / 10) + "." + (tenths % 10) + "s";   // culture-free: always a '.' (matches "9.8s" everywhere)
        }
        else if (secs < SecondsBelow) time = (int)secs + "s";
        else if (secs < MinutesBelow) time = (int)(secs / 60f) + "m";
        else                          time = (int)(secs / 3600f) + "h";
        return fallback ? "*" + time : time;
    }
}
