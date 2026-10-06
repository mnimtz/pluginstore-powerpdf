namespace AddonStore.Web.Validation;

/// <summary>
/// The imported DLLs and functions of a PE32+ binary (S1.0.11), from the import
/// directory and the delay-load import directory. Best effort: a malformed table
/// yields what could be read (the PE checks report broken files on their own).
/// </summary>
public static class PeImports
{
    public record Result(HashSet<string> Dlls, HashSet<string> Functions);

    public static Result Read(byte[] bytes)
    {
        var dlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var funcs = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (bytes.Length < 0x40 || bytes[0] != 'M' || bytes[1] != 'Z') return new(dlls, funcs);
            int pe = BitConverter.ToInt32(bytes, 0x3C);
            if (pe <= 0 || pe + 24 > bytes.Length) return new(dlls, funcs);
            int opt = pe + 24;
            if (BitConverter.ToUInt16(bytes, opt) != 0x20B) return new(dlls, funcs);   // PE32+ only
            // bounded work on crafted files (audit S1.3.1): at most 96 sections (as PeResources),
            // a global budget of thunk entries, and every thunk table only once
            int numSections = Math.Min((int)BitConverter.ToUInt16(bytes, pe + 6), 96);
            var budget = 200_000;
            var seen = new HashSet<uint>();
            int optSize = BitConverter.ToUInt16(bytes, pe + 20);
            var sections = new List<(uint va, uint size, uint raw)>();
            for (int i = 0; i < numSections; i++)
            {
                int s = opt + optSize + i * 40;
                if (s + 40 > bytes.Length) break;
                sections.Add((BitConverter.ToUInt32(bytes, s + 12),
                              Math.Max(BitConverter.ToUInt32(bytes, s + 8), BitConverter.ToUInt32(bytes, s + 16)),
                              BitConverter.ToUInt32(bytes, s + 20)));
            }
            long Off(uint rva)
            {
                foreach (var s in sections)
                    if (rva >= s.va && rva < s.va + s.size) return s.raw + (rva - s.va);
                return -1;
            }
            string? Str(uint rva, int max = 260)
            {
                long o = Off(rva);
                if (o < 0 || o >= bytes.Length) return null;
                int end = Array.IndexOf(bytes, (byte)0, (int)o, (int)Math.Min(max, bytes.Length - o));
                return end < 0 ? null : System.Text.Encoding.ASCII.GetString(bytes, (int)o, end - (int)o);
            }
            void Thunks(uint rva)
            {
                if (!seen.Add(rva)) return;
                long o = Off(rva);
                for (int k = 0; o >= 0 && o + 8 <= bytes.Length && k < 20000 && budget-- > 0; k++, o += 8)
                {
                    ulong t = BitConverter.ToUInt64(bytes, (int)o);
                    if (t == 0) break;
                    if ((t & 0x8000000000000000UL) != 0) continue;      // by ordinal
                    var name = Str((uint)(t & 0x7FFFFFFF) + 2, 512);     // skip the 2-byte hint
                    if (!string.IsNullOrEmpty(name)) funcs.Add(name);
                }
            }

            // import directory (data directory 1): 20-byte descriptors
            uint impRva = BitConverter.ToUInt32(bytes, opt + 120);
            if (impRva != 0)
                for (int i = 0; i < 1024; i++)
                {
                    long d = Off(impRva);
                    if (d < 0) break;
                    d += i * 20;
                    if (d + 20 > bytes.Length) break;
                    uint ilt = BitConverter.ToUInt32(bytes, (int)d), nameRva = BitConverter.ToUInt32(bytes, (int)d + 12),
                         iat = BitConverter.ToUInt32(bytes, (int)d + 16);
                    if (nameRva == 0) break;
                    if (Str(nameRva) is { } dll) dlls.Add(dll);
                    Thunks(ilt != 0 ? ilt : iat);
                }

            // delay-load import directory (data directory 13): 32-byte descriptors
            uint delayRva = BitConverter.ToUInt32(bytes, opt + 112 + 13 * 8);
            if (delayRva != 0)
                for (int i = 0; i < 1024; i++)
                {
                    long d = Off(delayRva);
                    if (d < 0) break;
                    d += i * 32;
                    if (d + 32 > bytes.Length) break;
                    uint nameRva = BitConverter.ToUInt32(bytes, (int)d + 4), intRva = BitConverter.ToUInt32(bytes, (int)d + 16);
                    if (nameRva == 0) break;
                    if (Str(nameRva) is { } dll) dlls.Add(dll);
                    if (intRva != 0) Thunks(intRva);
                }
        }
        catch
        {
            // best effort
        }
        return new(dlls, funcs);
    }
}
