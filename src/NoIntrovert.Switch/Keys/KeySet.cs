using NoIntrovert.Switch.Crypto;

namespace NoIntrovert.Switch.Keys;

/// <summary>Which key-area key family an NCA's key area is encrypted with.</summary>
public enum KeyAreaKeyKind : byte
{
    Application = 0,
    Ocean = 1,
    System = 2,
}

/// <summary>
/// Console keys loaded from a user-supplied <c>prod.keys</c> (and optionally <c>title.keys</c>).
/// Keys are never bundled with the library; the user must dump them from their own console.
/// </summary>
public sealed class KeySet
{
    public const int MaxKeyRevision = 0x20;

    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _titleKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads keys from a prod.keys file and an optional title.keys file.</summary>
    public static KeySet Load(string prodKeysPath, string? titleKeysPath = null)
    {
        var set = new KeySet();
        using (var reader = File.OpenText(prodKeysPath))
            set.ParseInto(reader, set._keys);

        if (titleKeysPath is not null && File.Exists(titleKeysPath))
        {
            using var reader = File.OpenText(titleKeysPath);
            set.ParseInto(reader, set._titleKeys);
        }

        set.DeriveMissingKeys();
        return set;
    }

    /// <summary>Loads keys from text (useful for tests or keys stored elsewhere).</summary>
    public static KeySet FromText(string prodKeysText, string? titleKeysText = null)
    {
        var set = new KeySet();
        set.ParseInto(new StringReader(prodKeysText), set._keys);
        if (titleKeysText is not null)
            set.ParseInto(new StringReader(titleKeysText), set._titleKeys);
        set.DeriveMissingKeys();
        return set;
    }

    /// <summary>The 32-byte NCA header key (two 16-byte AES-XTS keys).</summary>
    public byte[] HeaderKey => Require("header_key", 0x20);

    public bool HasHeaderKey => TryGet("header_key", 0x20, out _);

    /// <summary>Gets a key-area key for the given master key revision.</summary>
    public byte[] GetKeyAreaKey(KeyAreaKeyKind kind, int revision)
    {
        var name = kind switch
        {
            KeyAreaKeyKind.Application => "key_area_key_application",
            KeyAreaKeyKind.Ocean => "key_area_key_ocean",
            KeyAreaKeyKind.System => "key_area_key_system",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return Require($"{name}_{revision:x2}", 0x10);
    }

    /// <summary>Gets the title key encryption key for the given master key revision.</summary>
    public byte[] GetTitleKek(int revision) => Require($"titlekek_{revision:x2}", 0x10);

    /// <summary>Looks up an encrypted title key from title.keys by rights ID.</summary>
    public bool TryGetEncryptedTitleKey(ReadOnlySpan<byte> rightsId, out byte[] encryptedTitleKey)
        => _titleKeys.TryGetValue(Convert.ToHexString(rightsId), out encryptedTitleKey!);

    /// <summary>Raw lookup for any named key.</summary>
    public bool TryGet(string name, int expectedLength, out byte[] key)
    {
        if (_keys.TryGetValue(name, out var value) && value.Length == expectedLength)
        {
            key = value;
            return true;
        }
        key = Array.Empty<byte>();
        return false;
    }

    private byte[] Require(string name, int length)
    {
        if (TryGet(name, length, out var key))
            return key;
        throw new MissingKeyException(name);
    }

    private void ParseInto(TextReader reader, Dictionary<string, byte[]> target)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is ';' or '#' or '[')
                continue;

            var sep = trimmed.IndexOfAny(['=', ',']);
            if (sep <= 0)
                continue;

            var name = trimmed[..sep].Trim();
            var hex = trimmed[(sep + 1)..].Trim();
            if (hex.Length == 0 || hex.Length % 2 != 0 || !IsHex(hex))
                continue;

            target[name] = Convert.FromHexString(hex);
        }
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!char.IsAsciiHexDigit(c))
                return false;
        return true;
    }

    /// <summary>
    /// Derives titlekek_XX and key_area_key_*_XX from master_key_XX and the key sources
    /// when the keys file contains the sources but not the derived keys.
    /// </summary>
    private void DeriveMissingKeys()
    {
        TryGet("titlekek_source", 0x10, out var titleKekSource);
        TryGet("aes_kek_generation_source", 0x10, out var kekGenSource);
        TryGet("aes_key_generation_source", 0x10, out var keyGenSource);

        var areaSources = new (string Name, string SourceName)[]
        {
            ("key_area_key_application", "key_area_key_application_source"),
            ("key_area_key_ocean", "key_area_key_ocean_source"),
            ("key_area_key_system", "key_area_key_system_source"),
        };

        for (var rev = 0; rev < MaxKeyRevision; rev++)
        {
            if (!TryGet($"master_key_{rev:x2}", 0x10, out var masterKey))
                continue;

            var kekName = $"titlekek_{rev:x2}";
            if (titleKekSource.Length == 0x10 && !_keys.ContainsKey(kekName))
                _keys[kekName] = AesEcb.Decrypt(masterKey, titleKekSource);

            if (kekGenSource.Length != 0x10 || keyGenSource.Length != 0x10)
                continue;

            foreach (var (name, sourceName) in areaSources)
            {
                var full = $"{name}_{rev:x2}";
                if (_keys.ContainsKey(full) || !TryGet(sourceName, 0x10, out var source))
                    continue;

                var kek = AesEcb.Decrypt(masterKey, kekGenSource);
                var srcKek = AesEcb.Decrypt(kek, source);
                _keys[full] = AesEcb.Decrypt(srcKek, keyGenSource);
            }
        }
    }
}

/// <summary>Thrown when an operation needs a key that the loaded key files do not contain.</summary>
public sealed class MissingKeyException : Exception
{
    public MissingKeyException(string keyName)
        : base($"Required key '{keyName}' was not found in the keys file. Dump an up-to-date prod.keys from your console.")
        => KeyName = keyName;

    public MissingKeyException(string keyName, string message)
        : base(message)
        => KeyName = keyName;

    public string KeyName { get; }
}
