namespace Fetchline.Core.Asm;

/// <summary>Finds what a misspelled name was probably meant to be.</summary>
public static class Suggest
{
    /// <summary>
    /// The candidate closest to <paramref name="name"/>, if one is close enough to be a likely
    /// typo: one edit, or two for a name of six letters or more. Among equally close candidates
    /// the one of the same length wins, since a swapped or mistyped letter is likelier than a
    /// missing one: <c>adid</c> suggests <c>addi</c>, not <c>add</c>.
    /// </summary>
    public static string? Closest(string name, IEnumerable<string> candidates)
    {
        var limit = name.Length >= 6 ? 2 : 1;
        string? best = null;
        var bestDistance = limit + 1;

        foreach (var candidate in candidates)
        {
            if (Math.Abs(candidate.Length - name.Length) > limit)
            {
                continue;
            }

            var distance = Distance(name, candidate);
            if (distance < bestDistance || (distance == bestDistance && best is not null && Better(candidate, best)))
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return bestDistance <= limit ? best : null;

        bool Better(string candidate, string current)
        {
            var gap = Math.Abs(candidate.Length - name.Length) - Math.Abs(current.Length - name.Length);
            return gap < 0 || (gap == 0 && string.CompareOrdinal(candidate, current) < 0);
        }
    }

    /// <summary>The hint line for a diagnostic, or null when nothing is close.</summary>
    public static string? Hint(string name, IEnumerable<string> candidates) =>
        Closest(name, candidates) is { } closest ? $"did you mean '{closest}'?" : null;

    /// <summary>
    /// Edit distance where a swap of two neighbouring letters counts as one edit, since that is
    /// the commonest typo (<c>adid</c> for <c>addi</c>). Case is ignored.
    /// </summary>
    public static int Distance(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        var previous2 = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var same = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]);
                var cost = same ? 0 : 1;
                var best = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);

                if (i > 1 && j > 1
                    && char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 2])
                    && char.ToLowerInvariant(a[i - 2]) == char.ToLowerInvariant(b[j - 1]))
                {
                    best = Math.Min(best, previous2[j - 2] + 1);
                }

                current[j] = best;
            }

            (previous2, previous, current) = (previous, current, previous2);
        }

        return previous[b.Length];
    }
}
