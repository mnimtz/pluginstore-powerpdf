using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AddonStore.Web.Services;

/// <summary>
/// Passphrase encryption of a whole backup file (S0.18.0) for storage outside
/// the server. Format "PSBAK1": header (magic, salt, PBKDF2 iterations, chunk
/// size), then chunks of AES-256-GCM, each with its own random nonce. Every
/// chunk authenticates the header, its index and whether it is the last one,
/// so reordered, swapped or cut-off files are refused, not half restored.
/// </summary>
public static class BackupCrypto
{
    public const string Extension = ".psbak";
    private static readonly byte[] Magic = "PSBAK1"u8.ToArray();
    private const int Iterations = 600_000;
    private const int ChunkSize = 1024 * 1024;
    private const int HeaderLength = 6 + 16 + 4 + 4;

    /// <summary>True when the file starts like an encrypted backup.</summary>
    public static bool IsEncrypted(string path)
    {
        using var f = File.OpenRead(path);
        var head = new byte[Magic.Length];
        return f.Read(head, 0, head.Length) == head.Length && head.AsSpan().SequenceEqual(Magic);
    }

    public static async Task EncryptAsync(string source, string target, string passphrase, CancellationToken ct = default)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        salt.CopyTo(header, 6);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22), Iterations);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(26), ChunkSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);

        await using var input = File.OpenRead(source);
        await using var output = File.Create(target);
        await output.WriteAsync(header, ct);
        using var gcm = new AesGcm(key, 16);
        var current = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var currentLength = await ReadFullAsync(input, current, ct);
        long index = 0;
        while (true)
        {
            // Read ahead: a chunk is the last one when nothing follows it.
            var nextLength = currentLength == ChunkSize ? await ReadFullAsync(input, next, ct) : 0;
            var last = nextLength == 0;
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[currentLength];
            var tag = new byte[16];
            gcm.Encrypt(nonce, current.AsSpan(0, currentLength), cipher, tag, Aad(header, index, last));
            var len = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(len, currentLength);
            await output.WriteAsync(nonce, ct);
            await output.WriteAsync(len, ct);
            await output.WriteAsync(cipher, ct);
            await output.WriteAsync(tag, ct);
            if (last) break;
            (current, next) = (next, current);
            currentLength = nextLength;
            index++;
        }
    }

    /// <summary>Decrypts to <paramref name="target"/>; false (target deleted) when the passphrase is wrong or the file is damaged.</summary>
    public static async Task<bool> DecryptAsync(string source, string target, string passphrase, CancellationToken ct = default)
    {
        var ok = false;
        try
        {
            await using var input = File.OpenRead(source);
            var header = new byte[HeaderLength];
            if (await ReadFullAsync(input, header, ct) != HeaderLength || !header.AsSpan(0, 6).SequenceEqual(Magic)) return false;
            var salt = header.AsSpan(6, 16).ToArray();
            var iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(22));
            var chunk = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(26));
            if (iterations is < 100_000 or > 10_000_000 || chunk is < 4096 or > 64 * 1024 * 1024) return false;
            var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);

            await using var output = File.Create(target);
            using var gcm = new AesGcm(key, 16);
            var nonce = new byte[12];
            var len = new byte[4];
            var tag = new byte[16];
            for (long index = 0; ; index++)
            {
                if (await ReadFullAsync(input, nonce, ct) != 12 || await ReadFullAsync(input, len, ct) != 4) return false;
                var length = BinaryPrimitives.ReadInt32LittleEndian(len);
                if (length < 0 || length > chunk) return false;
                var cipher = new byte[length];
                if (await ReadFullAsync(input, cipher, ct) != length || await ReadFullAsync(input, tag, ct) != 16) return false;
                var plain = new byte[length];
                // The last chunk is marked in its authenticated data: try "not last" first,
                // then "last"; only the right one verifies.
                var last = false;
                try { gcm.Decrypt(nonce, cipher, tag, plain, Aad(header, index, false)); }
                catch (AuthenticationTagMismatchException)
                {
                    try { gcm.Decrypt(nonce, cipher, tag, plain, Aad(header, index, true)); last = true; }
                    catch (AuthenticationTagMismatchException) { return false; }
                }
                await output.WriteAsync(plain, ct);
                if (last)
                {
                    ok = input.Position == input.Length;   // nothing may follow the last chunk
                    return ok;
                }
            }
        }
        finally
        {
            if (!ok) try { File.Delete(target); } catch { }
        }
    }

    private static byte[] Aad(byte[] header, long index, bool last)
    {
        var aad = new byte[header.Length + 9];
        header.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(header.Length), index);
        aad[^1] = last ? (byte)1 : (byte)0;
        return aad;
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
