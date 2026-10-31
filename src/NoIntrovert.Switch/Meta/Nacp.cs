using System.Text;

namespace NoIntrovert.Switch.Meta;

/// <summary>The application control property (control.nacp): names, publisher and display version.</summary>
public sealed class Nacp
{
    public const int Size = 0x4000;

    /// <summary>Language slots in NACP order.</summary>
    public static readonly string[] Languages =
    [
        "AmericanEnglish", "BritishEnglish", "Japanese", "French", "German", "LatinAmericanSpanish", "Spanish",
        "Italian", "Dutch", "CanadianFrench", "Portuguese", "Russian", "Korean", "TraditionalChinese",
        "SimplifiedChinese", "BrazilianPortuguese",
    ];

    private Nacp(IReadOnlyList<(string Name, string Publisher)> titles, string displayVersion)
    {
        Titles = titles;
        DisplayVersion = displayVersion;
    }

    /// <summary>Name and publisher per language slot (empty strings where the game has no entry).</summary>
    public IReadOnlyList<(string Name, string Publisher)> Titles { get; }

    /// <summary>The version string shown on the console, e.g. "1.2.0".</summary>
    public string DisplayVersion { get; }

    /// <summary>The first non-empty name, trying the preferred language slots first (default: English).</summary>
    public string? GetName(params int[] preferredLanguages)
        => Pick(preferredLanguages, t => t.Name);

    public string? GetPublisher(params int[] preferredLanguages)
        => Pick(preferredLanguages, t => t.Publisher);

    private string? Pick(int[] preferred, Func<(string Name, string Publisher), string> select)
    {
        var order = (preferred.Length > 0 ? preferred : new[] { 0, 1 }).Concat(Enumerable.Range(0, Titles.Count));
        foreach (var i in order)
        {
            if (i < 0 || i >= Titles.Count)
                continue;
            var value = select(Titles[i]);
            if (value.Length > 0)
                return value;
        }
        return null;
    }

    public static Nacp Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x3070)
            throw new InvalidDataException("control.nacp is truncated.");

        var titles = new List<(string, string)>(Languages.Length);
        for (var i = 0; i < Languages.Length; i++)
        {
            var entry = data.Slice(i * 0x300, 0x300);
            titles.Add((ReadString(entry[..0x200]), ReadString(entry.Slice(0x200, 0x100))));
        }
        return new Nacp(titles, ReadString(data.Slice(0x3060, 0x10)));
    }

    private static string ReadString(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]).Trim();
    }
}
