namespace AddonStore.Web.Validation;

/// <summary>
/// Reads the resource directory of a 64-bit PE file (.zxt) and reports, per
/// resource type, which languages exist and how many named resources each
/// language has. Used for the rule that every add-on's own UI (string tables,
/// dialogs, menus) follows the Power PDF UI language.
/// </summary>
public static class PeResources
{
    public const int RtMenu = 4, RtDialog = 5, RtString = 6;

    /// <summary>type -> (LANGID -> number of resource names in that language). Null when unreadable.</summary>
    public static Dictionary<int, Dictionary<ushort, int>>? Languages(byte[] bytes)
    {
        try
        {
            if (bytes.Length < 0x40 || bytes[0] != 'M' || bytes[1] != 'Z') return null;
            var pe = BitConverter.ToInt32(bytes, 0x3C);
            if (pe <= 0 || (long)pe + 24 > bytes.Length) return null;
            var opt = pe + 24;
            if (BitConverter.ToUInt16(bytes, opt) != 0x20B) return null;   // PE32+
            var numSections = BitConverter.ToUInt16(bytes, pe + 6);
            var optSize = BitConverter.ToUInt16(bytes, pe + 20);
            if (BitConverter.ToUInt32(bytes, opt + 108) < 3) return new();  // no resource directory slot
            var resRva = BitConverter.ToUInt32(bytes, opt + 112 + 2 * 8);
            if (resRva == 0) return new();

            long rootOff = -1;
            var sec = opt + optSize;
            for (int i = 0; i < numSections; i++)
            {
                var s = sec + i * 40;
                if (s + 40 > bytes.Length) return null;
                uint va = BitConverter.ToUInt32(bytes, s + 12), vsize = BitConverter.ToUInt32(bytes, s + 8);
                uint raw = BitConverter.ToUInt32(bytes, s + 20), rawSize = BitConverter.ToUInt32(bytes, s + 16);
                if (resRva >= va && resRva < va + Math.Max(vsize, rawSize)) { rootOff = raw + (resRva - va); break; }
            }
            if (rootOff < 0 || rootOff + 16 > bytes.Length) return null;

            var result = new Dictionary<int, Dictionary<ushort, int>>();
            foreach (var (typeId, typeDir) in Entries(bytes, rootOff, rootOff))
            {
                if (typeId is not (RtMenu or RtDialog or RtString) || typeDir < 0) continue;
                var langs = new Dictionary<ushort, int>();
                foreach (var (_, nameDir) in Entries(bytes, rootOff, typeDir))
                {
                    if (nameDir < 0) continue;
                    foreach (var (langId, _) in Entries(bytes, rootOff, nameDir))
                        langs[(ushort)langId] = langs.GetValueOrDefault((ushort)langId) + 1;
                }
                result[typeId] = langs;
            }
            return result;
        }
        catch (ArgumentException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
    }

    // (id, absolute offset of the sub-directory or -1 for a data entry); named entries get id -1.
    private static IEnumerable<(int Id, long Dir)> Entries(byte[] b, long root, long dir)
    {
        if (dir + 16 > b.Length) yield break;
        int named = BitConverter.ToUInt16(b, (int)dir + 12), ids = BitConverter.ToUInt16(b, (int)dir + 14);
        var n = Math.Min(named + ids, 4096);
        for (int i = 0; i < n; i++)
        {
            var e = dir + 16 + i * 8;
            if (e + 8 > b.Length) yield break;
            var name = BitConverter.ToUInt32(b, (int)e);
            var off = BitConverter.ToUInt32(b, (int)e + 4);
            var id = (name & 0x80000000) != 0 ? -1 : (int)(name & 0xFFFF);
            var sub = (off & 0x80000000) != 0 ? root + (off & 0x7FFFFFFF) : -1;
            if (sub >= b.Length) sub = -1;
            yield return (id, sub);
        }
    }

    /// <summary>Primary language id of each of the 16 Power PDF UI languages (manifest codes).</summary>
    public static readonly (string Code, int Primary)[] PowerPdfLanguages =
    {
        ("en", 0x09), ("de", 0x07), ("fr", 0x0C), ("it", 0x10), ("es", 0x0A), ("nl", 0x13), ("pt", 0x16), ("da", 0x06),
        ("fi", 0x0B), ("nb", 0x14), ("sv", 0x1D), ("pl", 0x15), ("cs", 0x05), ("hu", 0x0E), ("ru", 0x19), ("tr", 0x1F),
    };
}
