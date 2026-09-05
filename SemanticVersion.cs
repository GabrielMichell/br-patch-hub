namespace BrPatchHub;

public static class SemanticVersion
{
    public static bool TryCompare(string? left, string? right, out int result)
    {
        result = 0;
        if (!TryParse(left, out var a) || !TryParse(right, out var b)) return false;
        for (var i = 0; i < Math.Max(a.Numbers.Length, b.Numbers.Length); i++)
        {
            var av = i < a.Numbers.Length ? a.Numbers[i] : 0;
            var bv = i < b.Numbers.Length ? b.Numbers[i] : 0;
            if (av == bv) continue;
            result = av.CompareTo(bv);
            return true;
        }
        if (a.PreRelease is null && b.PreRelease is not null) { result = 1; return true; }
        if (a.PreRelease is not null && b.PreRelease is null) { result = -1; return true; }
        result = ComparePreRelease(a.PreRelease, b.PreRelease);
        return true;
    }

    private static int ComparePreRelease(string? left, string? right)
    {
        if (left is null || right is null) return 0;
        var a = left.Split('.'); var b = right.Split('.');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i >= a.Length) return -1;
            if (i >= b.Length) return 1;
            var an = int.TryParse(a[i], out var av); var bn = int.TryParse(b[i], out var bv);
            var comparison = an && bn ? av.CompareTo(bv) : an ? -1 : bn ? 1 : string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static bool TryParse(string? value, out ParsedVersion parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().TrimStart('v', 'V');
        var build = normalized.IndexOf('+');
        if (build >= 0) normalized = normalized[..build];
        string? preRelease = null;
        var dash = normalized.IndexOf('-');
        if (dash >= 0) { preRelease = normalized[(dash + 1)..]; normalized = normalized[..dash]; }
        var parts = normalized.Split('.');
        if (parts.Length == 0 || parts.Any(x => !int.TryParse(x, out _))) return false;
        parsed = new ParsedVersion(parts.Select(int.Parse).ToArray(), string.IsNullOrWhiteSpace(preRelease) ? null : preRelease);
        return true;
    }

    private readonly record struct ParsedVersion(int[] Numbers, string? PreRelease);
}
