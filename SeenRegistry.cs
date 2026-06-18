using System.Collections.Generic;

namespace Stellar.CooldownBar;

/// <summary>
/// Records each distinct (kind, id) observed this session, in first-seen order, for the settings picker.
/// Session-scoped (never shrinks); Unity-free → unit-testable.
/// </summary>
internal sealed class SeenRegistry
{
    private readonly HashSet<long> _seen = new();
    private readonly List<(TileKind Kind, int Id)> _order = new();

    /// <summary>Record (kind,id); returns true if it was newly added (false if already seen).</summary>
    public bool Observe(TileKind kind, int id)
    {
        long key = ((long)kind << 32) | (uint)id;
        if (!_seen.Add(key)) return false;
        _order.Add((kind, id));
        return true;
    }

    public IReadOnlyList<(TileKind Kind, int Id)> Entries => _order;
}
