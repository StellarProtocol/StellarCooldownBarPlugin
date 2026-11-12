using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.CooldownBar;

// Diagnostics-only self-timing for CooldownBar (raid perf, 2026-11-12) — the before/after yardstick for the plugin's own
// main-thread cost. Same shape as RaidManager's McPerf/[MechPerf]: calls / total / max ticks + managed bytes allocated
// (GC.GetAllocatedBytesForCurrentThread delta) per bucket, one [CdPerf] line every 10 s.
// Armed ONCE at construction by env STELLAR_HITCH=1, a HITCH line in game_mini\stellar_perf.flags (both via
// PerfControls.Flag), or StellarDiagnostics. Disarmed cost: a static bool read per call site, no Stopwatch read —
// and the bar's Funcs are not even wrapped (Fn returns the original delegate), so the off path is byte-identical work.
// Main thread, except the Harmony postfix bucket (Interlocked adds; max is best-effort).
internal static class CdPerf
{
    public const int Update = 0, Refresh = 1, Funcs = 2, Postfix = 3;
    private const int SlotCount = 4;

    public static bool On { get; private set; }

    internal static void Init() =>
        On = PerfControls.Flag("HITCH") || StellarDiagnostics.IsEnabled;

    private static readonly long[] _ticks = new long[SlotCount], _max = new long[SlotCount], _bytes = new long[SlotCount];
    private static readonly long[] _calls = new long[SlotCount];
    private static long _postfixFull, _frames, _tileSum;
    private static long _windowStart;
    private static int  _gc0;
    private static readonly StringBuilder _sb = new(256);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public readonly struct Mark
    {
        public readonly long Ts, Bytes;
        public Mark(long ts, long bytes) { Ts = ts; Bytes = bytes; }
    }

    public static Mark Begin() =>
        On ? new Mark(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread()) : default;

    public static void End(int slot, Mark m)
    {
        if (m.Ts == 0) return;
        long dt = Stopwatch.GetTimestamp() - m.Ts;
        _ticks[slot] += dt;
        _calls[slot]++;
        if (dt > _max[slot]) _max[slot] = dt;
        _bytes[slot] += GC.GetAllocatedBytesForCurrentThread() - m.Bytes;
    }

    /// <summary>Wrap a bar overlay Func in the "funcs" bucket. When the probe is off this returns <paramref name="f"/>
    /// itself — zero added per-frame cost. The wrapper closure is allocated once at BuildRoot, never per frame.</summary>
    public static Func<T> Fn<T>(Func<T> f)
    {
        if (!On) return f;
        return () =>
        {
            var m = Begin();
            try { return f(); }
            finally { End(Funcs, m); }
        };
    }

    /// <summary>Postfix scope: <c>using var _perf = CdPerf.Hook();</c> as the postfix's first line times every call,
    /// early-returned or not. Off → default struct, Dispose is a single compare.</summary>
    public static HookScope Hook() => new(Begin());

    /// <summary>Count a postfix call that got past the demand gate (does the real work).</summary>
    public static void HookFull() { if (On) Interlocked.Increment(ref _postfixFull); }

    public readonly struct HookScope : IDisposable
    {
        private readonly Mark _m;
        public HookScope(Mark m) { _m = m; }
        public void Dispose()
        {
            if (_m.Ts == 0) return;
            long dt = Stopwatch.GetTimestamp() - _m.Ts;
            Interlocked.Add(ref _ticks[Postfix], dt);
            Interlocked.Increment(ref _calls[Postfix]);
            Interlocked.Add(ref _bytes[Postfix], GC.GetAllocatedBytesForCurrentThread() - _m.Bytes);
            if (dt > _max[Postfix]) _max[Postfix] = dt;
        }
    }

    public static void Frame(int tileCount) { _frames++; _tileSum += tileCount; }

    /// <summary>Called once per frame while On: emits one [CdPerf] line per 10 s.</summary>
    public static void MaybeFlush(Action<string> log, bool barShown, bool off)
    {
        long now = Environment.TickCount64;
        if (_windowStart == 0) { Reset(now); return; }
        if (now - _windowStart < 10_000) return;

        double ms = 1000.0 / Stopwatch.Frequency;
        long f = Math.Max(1, _frames);
        var sb = _sb.Clear();
        sb.Append("[CdPerf] ").Append(((now - _windowStart) / 1000.0).ToString("0.0", Inv)).Append("s frames=").Append(_frames)
          .Append(" tiles=").Append(((double)_tileSum / f).ToString("0.0", Inv));
        sb.Append(" | update ms/frame=").Append((_ticks[Update] * ms / f).ToString("0.000", Inv))
          .Append(" max=").Append((_max[Update] * ms).ToString("0.00", Inv))
          .Append(" alloc/frame="); Bytes(sb, _bytes[Update] / f);
        long rn = Math.Max(1, _calls[Refresh]);
        sb.Append(" | refresh n=").Append(_calls[Refresh])
          .Append(" avg=").Append((_ticks[Refresh] * ms / rn).ToString("0.000", Inv)).Append("ms")
          .Append(" max=").Append((_max[Refresh] * ms).ToString("0.00", Inv))
          .Append(" alloc/call="); Bytes(sb, _bytes[Refresh] / rn);
        sb.Append(" | funcs n=").Append(_calls[Funcs])
          .Append(" ms/frame=").Append((_ticks[Funcs] * ms / f).ToString("0.000", Inv))
          .Append(" max=").Append((_max[Funcs] * ms).ToString("0.00", Inv))
          .Append(" alloc/frame="); Bytes(sb, _bytes[Funcs] / f);
        sb.Append(" | postfix n=").Append(Interlocked.Read(ref _calls[Postfix])).Append(" full=").Append(Interlocked.Read(ref _postfixFull))
          .Append(" totalMs=").Append((Interlocked.Read(ref _ticks[Postfix]) * ms).ToString("0.00", Inv))
          .Append(" max=").Append((_max[Postfix] * ms).ToString("0.000", Inv))
          .Append(" alloc="); Bytes(sb, Interlocked.Read(ref _bytes[Postfix]));
        sb.Append(" | bar=").Append(barShown ? "shown" : "hidden").Append(" off=").Append(off ? '1' : '0')
          .Append(" gc0+").Append(GC.CollectionCount(0) - _gc0);
        log(sb.ToString());
        Reset(now);
    }

    private static void Bytes(StringBuilder sb, long b)
    {
        if (b < 1024) sb.Append(b).Append('B');
        else sb.Append((b / 1024.0).ToString("0.0", Inv)).Append("KB");
    }

    private static void Reset(long now)
    {
        _windowStart = now;
        Array.Clear(_ticks); Array.Clear(_max); Array.Clear(_bytes); Array.Clear(_calls);
        Interlocked.Exchange(ref _postfixFull, 0);
        _frames = 0; _tileSum = 0;
        _gc0 = GC.CollectionCount(0);
    }
}
