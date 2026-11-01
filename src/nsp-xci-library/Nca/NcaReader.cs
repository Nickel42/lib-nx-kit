using NspXciLibrary.Crypto;
using NspXciLibrary.IO;
using NspXciLibrary.Keys;

namespace NspXciLibrary.Nca;

/// <summary>Opens the decrypted sections of an NCA.</summary>
public sealed class NcaReader
{
    private readonly Stream _nca;
    private readonly KeySet _keys;
    private readonly Func<string, Ticket?> _ticketLookup;
    private byte[]? _contentKey;

    /// <param name="nca">The raw (encrypted) NCA, positioned anywhere. Not disposed by this reader.</param>
    /// <param name="ticketLookup">Resolves a rights ID (hex) to a ticket shipped in the same package.</param>
    public NcaReader(Stream nca, KeySet keys, Func<string, Ticket?>? ticketLookup = null)
    {
        _nca = nca;
        _keys = keys;
        _ticketLookup = ticketLookup ?? (_ => null);
        Header = NcaHeader.Decrypt(StreamUtil.ReadAt(nca, 0, NcaHeader.Size), keys);
    }

    public NcaHeader Header { get; }

    /// <summary>
    /// The AES-CTR key for the NCA body: the decrypted title key for rights-ID content,
    /// otherwise key-area slot 2.
    /// </summary>
    public byte[] GetContentKey()
    {
        if (_contentKey is not null)
            return _contentKey;

        var rev = Header.MasterKeyRevision;
        if (Header.HasRightsId)
        {
            var rightsHex = Convert.ToHexString(Header.RightsId);
            byte[] encryptedTitleKey;
            var ticket = _ticketLookup(rightsHex);
            if (ticket is not null)
            {
                if (ticket.IsPersonalized)
                    throw new NotSupportedException($"Ticket {rightsHex} is personalized to one console and cannot be used.");
                encryptedTitleKey = ticket.EncryptedTitleKey;
            }
            else if (!_keys.TryGetEncryptedTitleKey(Header.RightsId, out encryptedTitleKey))
            {
                throw new MissingKeyException(rightsHex,
                    $"No title key for rights ID {rightsHex}. The package has no ticket for it and it isn't in title.keys.");
            }

            _contentKey = AesEcb.Decrypt(_keys.GetTitleKek(rev), encryptedTitleKey);
        }
        else
        {
            var kak = _keys.GetKeyAreaKey(Header.KeyAreaKeyKind, rev);
            var keyArea = AesEcb.Decrypt(kak, Header.EncryptedKeyArea);
            _contentKey = keyArea.AsSpan(0x20, 0x10).ToArray();
        }
        return _contentKey;
    }

    /// <summary>Opens a section as a decrypted, seekable stream. Dispose the result when done.</summary>
    public Stream OpenSection(NcaSection section)
    {
        var raw = new SubStream(_nca, section.Offset, section.Size);
        return section.EncryptionType switch
        {
            NcaEncryptionType.None => raw,
            NcaEncryptionType.AesCtr or NcaEncryptionType.AesCtrEx
                or NcaEncryptionType.AesCtrSkipLayerHash or NcaEncryptionType.AesCtrExSkipLayerHash
                => new CtrTransformStream(raw, new AesCtrCipher(GetContentKey(), section.Counter), section.Offset),
            _ => throw new NotSupportedException($"Section encryption {section.EncryptionType} is not supported."),
        };
    }

    /// <summary>
    /// For a PartitionFs section (e.g. the one inside a Meta NCA), returns the decrypted section
    /// stream and the offset of the PFS0 within it.
    /// </summary>
    public (Stream Section, long Pfs0Offset) OpenPartitionFs(NcaSection section)
    {
        if (section.FsType != NcaFsType.PartitionFs)
            throw new InvalidOperationException("Section is not a partition filesystem.");

        var stream = OpenSection(section);
        var offset = section.HashType == NcaHashType.HierarchicalSha256 ? section.Sha256DataLayerOffset : 0;
        return (stream, offset);
    }
}
