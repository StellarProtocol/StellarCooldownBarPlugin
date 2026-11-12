using System;

namespace Stellar.CooldownBar;

public sealed class BuffTrackEntry
{
    public int    BuffUuid     { get; }
    public int    BuffBaseId   { get; internal set; }
    public string BuffName     { get; internal set; } = "";
    public int?   Visible      { get; internal set; }
    public int?   BuffType     { get; internal set; }   // 0=Debuff 1=Gain 2=GainRecovery 3=Item
    public bool   IsClientBuff { get; internal set; }
    internal bool Hidden;   // true = internal/hidden buff (empty game Name OR empty Icon); off-mode bar skips these
    public int    Layer        { get; internal set; }
    public int    Level        { get; internal set; }
    public int    SkillId      { get; internal set; }
    public string SkillName    { get; internal set; } = "";
    public long   Duration     { get; internal set; }   // ms; 0 = permanent

    internal long  CreateTime;   // game time (ms) at (re)application; changes when a stack refreshes the buff
    internal long  SnapTick;
    internal float SnapRemain;

    public float RemainSec => Duration == 0 ? -1f
        : MathF.Max(0f, SnapRemain - (Environment.TickCount64 - SnapTick) / 1000f);

    internal BuffTrackEntry(int buffUuid) { BuffUuid = buffUuid; }
}
