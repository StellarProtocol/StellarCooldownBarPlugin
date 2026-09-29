using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Stellar.CooldownBar.Tests;

/// <summary>
/// Real StarResonanceData rows (SkillTable / BuffTable subset, Fixtures/game-tables-subset.json) and the two
/// framework lookups DebuffAttribution is injected with, modelled on the framework's documented rules:
/// BuffTable.SkillId, and IGameDataResonance.GetImagineForSkill = "SlotPositionId contains 7 or 8".
/// </summary>
internal sealed class GameTables
{
    private readonly Dictionary<int, int[]> _slots = new();
    private readonly Dictionary<int, int> _buffSkill = new();
    public readonly Dictionary<int, string> BuffNames = new();
    public readonly Dictionary<int, string> SkillNames = new();

    public static readonly GameTables Load = new();

    private GameTables()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "game-tables-subset.json")));
        foreach (var s in doc.RootElement.GetProperty("skills").EnumerateArray())
        {
            int id = s.GetProperty("Id").GetInt32();
            _slots[id] = s.GetProperty("SlotPositionId").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            SkillNames[id] = s.GetProperty("Name").GetString()!;
        }
        foreach (var b in doc.RootElement.GetProperty("buffs").EnumerateArray())
        {
            int id = b.GetProperty("Id").GetInt32();
            _buffSkill[id] = b.GetProperty("SkillId").GetInt32();
            BuffNames[id] = b.GetProperty("Name").GetString()!;
        }
    }

    public int BuffSkillId(int buffId) => _buffSkill.TryGetValue(buffId, out var s) ? s : 0;

    public int ImagineSkillOf(int skillId) =>
        _slots.TryGetValue(skillId, out var slots) && (slots.Contains(7) || slots.Contains(8)) ? skillId : 0;

    public DebuffAttribution NewAttribution() => new(BuffSkillId, ImagineSkillOf);
}
