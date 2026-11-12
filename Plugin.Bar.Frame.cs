using System;
using Stellar.Abstractions.Domain;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.CooldownBar;

// Frame A: the bar's rounded theme panel (Panel token, radius 6, opacity = the Background slider) filling the WHOLE
// window rect at any resize size — the redesign's replacement for the black WindowSpec.BackgroundOpacity box.
// How: the Borderless chrome's root GameObject already carries a full-rect Image (its click blocker — the very Image
// BackgroundOpacity painted black, WindowBuilder.Chrome.cs). The toolkit has no themed/rounded fill for a window root and
// a Bubble in the content would sit inside the 12 px chrome padding, so the plugin gives that Image a baked rounded
// 9-slice sprite and the live Panel colour itself. BackgroundOpacity is no longer set, so nothing else writes that Image.
//  • The window is destroyed on hide and remounted on show (menus, loading): BarRt() hands back a NEW RectTransform
//    after a remount, and the style is re-applied to it. Checked every tick while the bar is shown (a reference compare).
//  • The colour is re-derived every tick (theme-live) and written only when it changed.
//  • Sprite = the framework's own BubbleBg recipe (WindowThemeAssets: size 2r+4, 9-slice border r+1, 4× supersampled
//    corners, 100 px/unit) so its corners match every other radius-6 card on screen.
public sealed partial class Plugin
{
    private const int FrameRadius = 6;

    private Texture2D?     _frameTex;
    private Sprite?        _frameSprite;
    private RectTransform? _frameRt;     // the root we last styled (a remount brings a new one)
    private Image?         _frameImg;
    private ColorRgba      _frameCol;
    private bool           _frameColSet;

    // OnUpdate, while the bar is shown.
    private void TickBarFrame()
    {
        var rt = BarRt();
        if (rt == null) return;
        if (!ReferenceEquals(rt, _frameRt))
        {
            var img = rt.GetComponent<Image>();
            if (img == null) return;
            img.sprite = FrameSprite();
            img.type = Image.Type.Sliced;
            _frameRt = rt; _frameImg = img; _frameColSet = false;
        }
        var c = PanelColor() with { A = _bgOpacity };
        if (_frameColSet && c.Equals(_frameCol)) return;
        _frameImg!.color = new Color(c.R, c.G, c.B, c.A);
        _frameCol = c; _frameColSet = true;
    }

    private Sprite FrameSprite()
    {
        if (_frameSprite != null) return _frameSprite;
        int size = FrameRadius * 2 + 4;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp,
        };
        var px = new Color[size * size];
        const int SS = 4;
        float r = FrameRadius;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int covered = 0;
            for (int sy = 0; sy < SS; sy++)
            for (int sx = 0; sx < SS; sx++)
            {
                float fx = x + (sx + 0.5f) / SS, fy = y + (sy + 0.5f) / SS;
                // Distance outside the rounded rect's corner circles (inside the straight band → covered).
                float cx = fx < r ? r : fx > size - r ? size - r : fx;
                float cy = fy < r ? r : fy > size - r ? size - r : fy;
                float dx = fx - cx, dy = fy - cy;
                if (dx * dx + dy * dy <= r * r) covered++;
            }
            px[y * size + x] = new Color(1f, 1f, 1f, covered / (float)(SS * SS));
        }
        tex.SetPixels(px);
        tex.Apply(true);
        _frameTex = tex;
        _frameSprite = Sprite.Create(tex, new UnityEngine.Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f),
            100f, 0, SpriteMeshType.FullRect, new Vector4(FrameRadius + 1, FrameRadius + 1, FrameRadius + 1, FrameRadius + 1));
        _frameSprite.hideFlags = HideFlags.HideAndDontSave;
        return _frameSprite;
    }

    private void DisposeBarFrame()
    {
        _frameRt = null; _frameImg = null;
        if (_frameSprite != null) UnityEngine.Object.Destroy(_frameSprite);
        if (_frameTex != null) UnityEngine.Object.Destroy(_frameTex);
        _frameSprite = null; _frameTex = null;
    }

    // ── saved-height migration (tile T1 is 70 px tall, the old tile 62) ──────────────────────────────────────────
    // A bar resized under the old tiles keeps its saved height. Clamping into the new 128–280 range alone would drop a
    // row for some sizes (an old 2-row bar of 198–203 px now fits 1 row; an old 3-row bar, 266–270, fits 2). So once,
    // map the saved height to the OLD row count (the old ComputeRowsVisible formula) and resize to exactly the new
    // height of that many rows — the user keeps their row count. Runs once the bar has been shown for a second (the
    // saved rect is applied after the first mount), then records "bar.tile_layout" = 2 so it never runs again. SetRect
    // persists through the framework (the current layout slot).
    private const int   TileLayoutVersion = 2;
    private const float OldBaseH = 130f, OldRowPitch = 62f + 6f;   // the pre-redesign BaseH / TileRowH + TileVGap
    private float _migrateShownS = -1f;                            // < 0 = done (or nothing to do)

    private void InitBarMigration()
    {
        if (_cfg.Get("bar.tile_layout", 0) < TileLayoutVersion) _migrateShownS = 0f;
    }

    private void TickBarMigration(float dt)
    {
        if (_migrateShownS < 0f) return;
        _migrateShownS += dt;
        if (_migrateShownS < 1f) return;
        _migrateShownS = -1f;
        if (_bar == null) return;
        var rect = _bar.Rect;
        float h = BarHeight;
        if (h > 1f && rect.Width > 1f)
        {
            int oldRows = Math.Min(MaxRows, Math.Max(1, (int)((h - OldBaseH) / OldRowPitch) + 1));
            float target = BarHeightFor(oldRows);
            if (Math.Abs(h - target) > 0.5f)
            {
                _bar.SetRect(new WindowRect(rect.X, rect.Y, rect.Width, target));
                _services.Log.Info($"[CooldownBar] tile redesign: bar height {h:F0} → {target:F0} ({oldRows} row(s) kept)");
            }
        }
        _cfg.Set("bar.tile_layout", TileLayoutVersion);
        _cfg.Save();
    }
}
