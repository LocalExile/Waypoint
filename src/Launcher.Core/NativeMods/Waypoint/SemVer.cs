using System.Text.RegularExpressions;

namespace LocalExile.Client.Waypoint;

/// <summary>
/// Semantic version (major.minor.patch[-prerelease]) with SemVer 2.0 comparison semantics.
/// Compatible with both LocalExile release versions (e.g. <c>0.5.2-beta</c>) and mod/SDK versions (<c>1.0.0</c>).
/// </summary>
public readonly partial record struct SemVer(int Major, int Minor, int Patch, string? Prerelease = null) : IComparable<SemVer>
{
    [GeneratedRegex(@"\A(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SemVerRegex();

    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        var match = SemVerRegex().Match(trimmed);
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups["major"].Value, out int major) ||
            !int.TryParse(match.Groups["minor"].Value, out int minor) ||
            !int.TryParse(match.Groups["patch"].Value, out int patch))
            return false;

        string? pre = match.Groups["pre"].Success ? match.Groups["pre"].Value : null;
        if (pre is not null && pre.Split('.').Any(seg => seg.Length == 0))
            return false;

        version = new SemVer(major, minor, patch, pre);
        return true;
    }

    public static SemVer Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Invalid semantic version '{text}'.");

    public int CompareTo(SemVer other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        if (Prerelease is null && other.Prerelease is null) return 0;
        if (Prerelease is null) return 1;       // release > prerelease
        if (other.Prerelease is null) return -1;

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        int count = Math.Min(left.Length, right.Length);
        for (int i = 0; i < count; i++)
        {
            bool leftNum = int.TryParse(left[i], out int ln);
            bool rightNum = int.TryParse(right[i], out int rn);
            if (leftNum && rightNum)
            {
                c = ln.CompareTo(rn);
                if (c != 0) return c;
            }
            else if (leftNum != rightNum)
            {
                return leftNum ? -1 : 1; // numeric < non-numeric per SemVer 2.0
            }
            else
            {
                c = StringComparer.Ordinal.Compare(left[i], right[i]);
                if (c != 0) return c;
            }
        }
        return left.Length.CompareTo(right.Length);
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public override string ToString() =>
        Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}

/// <summary>
/// Evaluates SemVer range expressions such as <c>>=1.0.0 &lt;2.0.0</c>, <c>^1.2.0</c>, <c>~1.2.0</c>,
/// <c>1.0.0</c>, or <c>||</c>-separated alternatives.
/// </summary>
public sealed class SemVerRange
{
    private readonly List<List<Func<SemVer, bool>>> _orClauses;
    public string Expression { get; }

    private SemVerRange(string expression, List<List<Func<SemVer, bool>>> orClauses)
    {
        Expression = expression;
        _orClauses = orClauses;
    }

    public bool IsSatisfiedBy(SemVer version) =>
        _orClauses.Any(andClause => andClause.All(pred => pred(version)));

    public static bool TryParse(string? expression, out SemVerRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var orParts = expression.Split("||", StringSplitOptions.TrimEntries);
        if (orParts.Length == 0 || orParts.Any(string.IsNullOrWhiteSpace))
            return false;

        var clauses = new List<List<Func<SemVer, bool>>>();
        foreach (string orPart in orParts)
        {
            var tokens = orPart.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return false;

            var predicates = new List<Func<SemVer, bool>>();
            foreach (string token in tokens)
            {
                if (!TryParseComparator(token, predicates))
                    return false;
            }
            clauses.Add(predicates);
        }

        range = new SemVerRange(expression.Trim(), clauses);
        return true;
    }

    public static SemVerRange Parse(string expression) =>
        TryParse(expression, out var r) && r is not null
            ? r
            : throw new FormatException($"Invalid SemVer range '{expression}'.");

    private static bool TryParseComparator(string token, List<Func<SemVer, bool>> predicates)
    {
        if (token is "*" or "x" or "X")
        {
            predicates.Add(_ => true);
            return true;
        }

        if (token.StartsWith(">=", StringComparison.Ordinal))
        {
            if (!SemVer.TryParse(token[2..], out var v)) return false;
            predicates.Add(candidate => candidate >= v);
            return true;
        }
        if (token.StartsWith("<=", StringComparison.Ordinal))
        {
            if (!SemVer.TryParse(token[2..], out var v)) return false;
            predicates.Add(candidate => candidate <= v);
            return true;
        }
        if (token.StartsWith('>'))
        {
            if (!SemVer.TryParse(token[1..], out var v)) return false;
            predicates.Add(candidate => candidate > v);
            return true;
        }
        if (token.StartsWith('<'))
        {
            if (!SemVer.TryParse(token[1..], out var v)) return false;
            predicates.Add(candidate => candidate < v);
            return true;
        }
        if (token.StartsWith('='))
        {
            if (!SemVer.TryParse(token[1..], out var v)) return false;
            predicates.Add(candidate => candidate.CompareTo(v) == 0);
            return true;
        }
        if (token.StartsWith('^'))
        {
            if (!SemVer.TryParse(token[1..], out var v)) return false;
            SemVer upper = v.Major > 0
                ? new SemVer(v.Major + 1, 0, 0)
                : v.Minor > 0
                    ? new SemVer(0, v.Minor + 1, 0)
                    : new SemVer(0, 0, v.Patch + 1);
            predicates.Add(candidate => candidate >= v && candidate < upper);
            return true;
        }
        if (token.StartsWith('~'))
        {
            if (!SemVer.TryParse(token[1..], out var v)) return false;
            var upper = new SemVer(v.Major, v.Minor + 1, 0);
            predicates.Add(candidate => candidate >= v && candidate < upper);
            return true;
        }

        if (SemVer.TryParse(token, out var exact))
        {
            predicates.Add(candidate => candidate.CompareTo(exact) == 0);
            return true;
        }

        return false;
    }
}
