using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.CooldownBar;

/// <summary>
/// Opening the Settings window, house style (Stellar Design Language §5 "Open = OpenCentred"; TargetLens
/// Plugin.Settings.Open.cs port): show → <c>BringToFront()</c> → centre for a short burst of ticks. F8 and the bar's gear
/// centre it on the BAR (the bar is the opener; hidden — Placed mode, a menu — falls back to the screen); the launcher
/// tile opens from outside the plugin → centred on the SCREEN.
/// Always fronted: the help popover is fronted on every open, and a fronted opener keeps every open path consistent
/// (house habit; framework 2.30 stacking is non-sticky, CLAUDE.md Rule 5 exception).
/// The burst: a SetRect right after SetVisible(true) is ignored on the first open (no token yet) and the mount then
/// restores the saved rect, so re-centre every tick for <see cref="CentreTicks"/> ticks — but stop for good the moment
/// the window is no longer where we left it (the user dragged it), or the burst snaps the first drag back.
/// </summary>
public sealed partial class Plugin
{
    private const int CentreTicks = 30;   // plugin ticks (≥ 1 s at 30 Hz) — covers mount + deferred apply + layout settle

    // One centring burst for one window. `anchor` = the window to centre on (null / hidden → the screen).
    private sealed class CentreBurst
    {
        private readonly IWindowControl _win;
        private readonly IFramework _fw;
        private readonly float _fallbackH;   // until the window reports its laid-out height
        private Func<IWindowControl?> _anchor = () => null;
        private int _left;
        private (float X, float Y)? _placed;   // where we last left the MOUNTED window (null = not placed yet)

        public CentreBurst(IWindowControl win, IFramework fw, float fallbackH)
        { _win = win; _fw = fw; _fallbackH = fallbackH; }

        public void Start(Func<IWindowControl?> anchor) { _anchor = anchor; _placed = null; _left = CentreTicks; Step(); }
        public void Stop() => _left = 0;

        public void Tick()
        {
            if (_left <= 0) return;   // the common case: one compare per frame
            _left--;
            if (_win.IsShown && _placed is { } p)
            {
                var cur = _win.Rect;
                if (Math.Abs(cur.X - p.X) >= 0.5f || Math.Abs(cur.Y - p.Y) >= 0.5f) { _left = 0; return; }   // user's now
            }
            Step();
        }

        // Centre on the anchor's rect (or the screen), canvas units, clamped on-screen and floored to whole units.
        // SetRect only when off target; read the live pose back (SetRect clamps), so a drag is detected reliably.
        private void Step()
        {
            if (!_win.IsShown) return;   // not mounted yet: Rect is the DefaultRect, SetRect would be ignored
            float cw = _fw.CanvasWidth, ch = _fw.CanvasHeight;
            if (cw <= 1f || ch <= 1f) return;
            var r = _win.Rect;
            float h = r.Height > 1f ? r.Height : _fallbackH;
            if (r.Width <= 1f || h <= 1f) return;   // not laid out yet — try again next tick
            float cx = cw / 2f, cy = ch / 2f;
            var a = _anchor();
            if (a != null && a.IsShown) { var ar = a.Rect; cx = ar.X + ar.Width / 2f; cy = ar.Y + ar.Height / 2f; }
            float x = MathF.Floor(MathF.Max(0f, MathF.Min(cx - r.Width / 2f, cw - r.Width)));
            float y = MathF.Floor(MathF.Max(0f, MathF.Min(cy - h / 2f, ch - h)));
            if (Math.Abs(r.X - x) >= 0.5f || Math.Abs(r.Y - y) >= 0.5f) _win.SetRect(new WindowRect(x, y, r.Width, r.Height));
            var after = _win.Rect;
            _placed = (after.X, after.Y);
        }
    }

    private CentreBurst? _settingsCentre;
    private Func<IWindowControl?>? _barAnchorFn, _screenAnchorFn;   // bound once (no delegate per open)

    // F8 / the bar's gear: toggle; opening centres on the bar. Hiding also drops the help tip, the size popover and a
    // pending confirm.
    private void ToggleSettings()
    {
        if (_settings.IsShown) CloseSettings();
        else OpenSettings(_barAnchorFn ??= () => _bar);
    }

    // The launcher tile: centred on the screen.
    private void OpenSettingsFromLauncher() => OpenSettings(_screenAnchorFn ??= () => null);

    private void OpenSettings(Func<IWindowControl?> anchor)
    {
        _settings.SetVisible(true);
        _settings.BringToFront();
        (_settingsCentre ??= new CentreBurst(_settings, _services.Framework, SettingsWindowH)).Start(anchor);
    }

    private void CloseSettings()
    {
        _settings.SetVisible(false);
        _settingsCentre?.Stop();
        CancelUntickAll();
        CloseHelp();
        CloseSizePopover();   // Plugin.Settings.Placed.cs
    }

    // OnUpdate: the help popover re-anchor, the centring burst and the confirm timeout. Each is a single compare when idle.
    private void TickSettingsUi()
    {
        HelpRepositionTick();
        SizePopRepositionTick();
        _settingsCentre?.Tick();
        AdvanceUntickConfirm();   // Plugin.Settings.Feedback.cs
    }
}
