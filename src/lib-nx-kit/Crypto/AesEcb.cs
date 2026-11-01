using System.Security.Cryptography;

namespace LibNxKit.Crypto;

internal static class AesEcb
{
    public static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes.DecryptEcb(data, PaddingMode.None);
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes.EncryptEcb(data, PaddingMode.None);
    }
}
