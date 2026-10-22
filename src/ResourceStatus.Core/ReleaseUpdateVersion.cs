using System.Globalization;

namespace ResourceStatus.Core;

/// <summary>SemVer precedence, with build metadata retained for exact asset names but ignored when comparing versions.</summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private ReleaseVersion(Version numericVersion, string? prerelease, string? buildMetadata)
    {
        NumericVersion = numericVersion;
        Prerelease = prerelease;
        BuildMetadata = buildMetadata;
    }

    public Version NumericVersion { get; }
    public string? Prerelease { get; }
    public string? BuildMetadata { get; }
    public bool IsPrerelease => Prerelease is not null;
    public string PrecedenceText => NumericVersion.ToString(3) + (Prerelease is null ? "" : "-" + Prerelease);
    public string Text => PrecedenceText + (BuildMetadata is null ? "" : "+" + BuildMetadata);
    public override string ToString() => Text;
    public static ReleaseVersion FromVersion(Version version) => new(ReleaseUpdateMetadata.NormalizeVersion(version), null, null);

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = FromVersion(new Version(0, 0, 0));
        if (string.IsNullOrEmpty(text) || text.Length > 128) return false;
        string? build = null, prerelease = null;
        int plus = text.IndexOf('+');
        if (plus >= 0)
        {
            build = text[(plus + 1)..];
            text = text[..plus];
            if (!ValidIdentifiers(build, numericLeadingZerosAllowed: true)) return false;
        }
        int dash = text.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];
            if (!ValidIdentifiers(prerelease, numericLeadingZerosAllowed: false)) return false;
        }
        var parts = text.Split('.');
        if (parts.Length != 3) return false;
        var values = new int[3];
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            if (part.Length == 0 || (part.Length > 1 && part[0] == '0') || !part.All(char.IsAsciiDigit) ||
                !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out values[index])) return false;
        }
        version = new ReleaseVersion(new Version(values[0], values[1], values[2]), prerelease, build);
        return true;
    }

    private static bool ValidIdentifiers(string value, bool numericLeadingZerosAllowed)
    {
        foreach (string identifier in value.Split('.'))
        {
            if (identifier.Length == 0 || identifier.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return false;
            if (!numericLeadingZerosAllowed && identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsAsciiDigit)) return false;
        }
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int numeric = NumericVersion.CompareTo(other.NumericVersion);
        if (numeric != 0) return numeric;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        string[] left = Prerelease.Split('.'), right = other.Prerelease.Split('.');
        for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            bool leftNumeric = left[index].All(char.IsAsciiDigit), rightNumeric = right[index].All(char.IsAsciiDigit);
            int compared;
            if (leftNumeric != rightNumeric) compared = leftNumeric ? -1 : 1;
            else if (leftNumeric && left[index].Length != right[index].Length) compared = left[index].Length.CompareTo(right[index].Length);
            else compared = string.CompareOrdinal(left[index], right[index]);
            if (compared != 0) return compared;
        }
        return left.Length.CompareTo(right.Length);
    }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? other) => other is ReleaseVersion version && Equals(version);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(PrecedenceText);
}
