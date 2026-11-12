using System;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.CooldownBar;

// The layout editor shows inactive placed tiles FADED (~45 %). Every tile colour takes the fade multiplier
// (Plugin.Bar.Tile.cs), but GameTextureElement has no tint, so the icon is dimmed on the Unity side: the RawImage(s)
// under the window root get alpha 0.45 while the tile is a sample in the editor, and 1 again afterwards. The framework's
// GameTexture binding writes only texture + uvRect, never the colour, so the alpha stays put. (Not a CanvasGroup on the
// root: a fading CanvasGroup over the icon's stencil Mask is a known IL2CPP flicker combo — RoundedTextureBaker.cs — and
// it would fade the name tag too.)
// Cost: idle (one bool + one count test) outside the layout editor once nothing is faded. While editing, ≤ 4 Hz, and a
// window's hierarchy is re-scanned (GetComponentsInChildren — allocates) only when its state flips or once a second (an
// icon that loads late, or a size switch, builds a new RawImage). The root is found once per mount (GameObject.Find
// sees active objects only — placed windows are all shown while editing).
public sealed partial class Plugin
{
    private const long FadeTickMs = 250, FadeRescanMs = 1000;
    private long _fadeTickAt;
    private int  _fadeApplied;   // placed windows whose icon is currently faded

    private void TickPlacedFade()
    {
        bool editing = _services.Windows.IsLayoutEditing;
        if (!editing && _fadeApplied == 0) return;
        long now = Environment.TickCount64;
        if (now < _fadeTickAt) return;
        _fadeTickAt = now + FadeTickMs;
        for (int s = 0; s < MaxPlaced; s++)
        {
            var p = _placed[s];
            if (p == null) continue;
            bool want = editing && PlacedSample(p);
            if (want == p.FadeApplied && !(want && now >= p.FadeScanAt)) continue;
            if (!ApplyIconAlpha(p, want ? SampleFade : 1f)) continue;   // not found yet → retry next tick
            if (want != p.FadeApplied) _fadeApplied += want ? 1 : -1;
            p.FadeApplied = want;
            p.FadeScanAt = now + FadeRescanMs;
        }
    }

    private bool ApplyIconAlpha(PlacedEffect p, float a)
    {
        var root = p.Root;
        if (!ReferenceEquals(root, null) && root == null) root = p.Root = null;   // destroyed (remount) → look it up again
        if (ReferenceEquals(root, null))
        {
            var go = GameObject.Find(PlacedWindowId(p));
            if (go == null) return false;
            root = p.Root = go.GetComponent<RectTransform>();
            if (root == null) return false;
        }
        foreach (var img in root.GetComponentsInChildren<RawImage>(true))
        {
            var c = img.color;
            if (c.a != a) img.color = new Color(c.r, c.g, c.b, a);
        }
        return true;
    }
}
