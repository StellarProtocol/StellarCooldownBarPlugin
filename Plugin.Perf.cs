using System.IO;

namespace Stellar.CooldownBar;

// [CdPerf] probe wiring (CdPerf.cs) + an in-session "CooldownBar off" control for A/B measurement: while the probe is
// on, the presence of game_mini\stellar_cdbar_off.flag (checked once per second) skips the data refresh and hides the
// bar, so the same session yields an on/off baseline without a restart. Probe off → none of this runs and _perfOff
// stays false (the ShouldRender / OnUpdate checks are a single bool read).
public sealed partial class Plugin
{
    private bool   _perfOff;
    private float  _perfOffCheckAccum;
    private string _perfOffFlagPath = "";
    private System.Action<string>? _perfLog;   // cached: a method-group conversion per frame would allocate

    // Before BuildRoot: CdPerf.Fn decides at build time whether the bar Funcs get a timing wrapper.
    private void InitPerf()
    {
        CdPerf.Init();
        if (!CdPerf.On) return;
        _perfLog = _services.Log.Info;
        // Same base dir the framework resolves stellar_perf.flags against (PerfControls: the game's working dir = game_mini).
        _perfOffFlagPath = Path.Combine(Directory.GetCurrentDirectory(), "stellar_cdbar_off.flag");
        _services.Log.Info($"[CdPerf] probe ON — 10 s lines; create '{_perfOffFlagPath}' to switch CooldownBar off in-session");
    }

    // Plugin.Bar.cs wraps every overlay Func in this (short name keeps BuildRoot readable). Off → returns f unchanged.
    private static System.Func<T> P<T>(System.Func<T> f) => CdPerf.Fn(f);

    // Tail of OnUpdate, only while CdPerf.On. Closes the "update" bucket opened at the top of OnUpdate.
    private void PerfTick(float dt, CdPerf.Mark updateMark)
    {
        CdPerf.End(CdPerf.Update, updateMark);
        CdPerf.Frame(_tileCount);

        _perfOffCheckAccum += dt;
        if (_perfOffCheckAccum >= 1f)
        {
            _perfOffCheckAccum = 0f;
            bool off;
            try { off = File.Exists(_perfOffFlagPath); } catch { off = false; }
            if (off != _perfOff)
            {
                _perfOff = off;
                _services.Log.Info($"[CdPerf] CooldownBar control switch: {(off ? "OFF (refresh skipped, bar hidden)" : "ON")}");
            }
        }
        CdPerf.MaybeFlush(_perfLog!, _bar.IsShown, _perfOff);
    }
}
