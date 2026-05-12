using MacMonitor.Core.Abstractions;
using MacMonitor.Core.Models;

namespace MacMonitor.Storage;

/// <summary>
/// Pure two-snapshot diff. Given previous and current item lists plus a typed differ,
/// produces the Added/Removed/Changed sets, already filtered against the differ's
/// <see cref="IDiffer{T}.Policy"/>.
/// </summary>
public static class DiffEngine
{
    public static Diff<T> Compute<T>(
        IReadOnlyList<T> previous,
        IReadOnlyList<T> current,
        IDiffer<T> differ)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(differ);

        // Index both snapshots by identity. On duplicate identities (rare; e.g. two
        // processes with identical command+user), the later occurrence wins — the diff
        // is conservative on dupes either way.
        var prevByKey = new Dictionary<string, T>(previous.Count, StringComparer.Ordinal);
        foreach (var item in previous)
        {
            prevByKey[differ.IdentityKey(item)] = item;
        }
        var currByKey = new Dictionary<string, T>(current.Count, StringComparer.Ordinal);
        foreach (var item in current)
        {
            currByKey[differ.IdentityKey(item)] = item;
        }

        var emitAdded = (differ.Policy & DiffEmissionPolicy.Added) != 0;
        var emitRemoved = (differ.Policy & DiffEmissionPolicy.Removed) != 0;
        var emitChanged = (differ.Policy & DiffEmissionPolicy.Changed) != 0;

        var added = new List<T>();
        var changed = new List<DiffChange<T>>();
        foreach (var (key, currItem) in currByKey)
        {
            if (!prevByKey.TryGetValue(key, out var prevItem))
            {
                if (emitAdded)
                {
                    added.Add(currItem);
                }
                continue;
            }
            if (emitChanged)
            {
                var prevHash = differ.ContentHash(prevItem);
                var currHash = differ.ContentHash(currItem);
                if (!string.Equals(prevHash, currHash, StringComparison.Ordinal))
                {
                    changed.Add(new DiffChange<T>(key, prevItem, currItem));
                }
            }
        }

        var removed = new List<T>();
        if (emitRemoved)
        {
            foreach (var (key, prevItem) in prevByKey)
            {
                if (!currByKey.ContainsKey(key))
                {
                    removed.Add(prevItem);
                }
            }
        }

        return new Diff<T>(added, removed, changed);
    }
}
