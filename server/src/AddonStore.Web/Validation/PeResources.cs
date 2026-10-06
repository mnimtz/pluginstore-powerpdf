namespace AddonStore.Web.Validation;

/// <summary>
/// Reads what the store checks in a 64-bit PE file (.zxt): resource languages,
/// the version resource, exported names, a CLR header and the hardening flags.
/// Hardened against crafted files: every directory is visited once and the
/// number of entries is capped, so a malicious resource tree cannot loop.
/// </summary>
public sealed class PeResources
{
    public const int RtMenu = 4, RtDialog = 5, RtString = 6, RtVersion = 16;
    private const int MaxEntries = 65_536;

    private readonly byte[] _b;
    private readonly int _opt;
    private readonly List<(uint Va, uint VSize, uint Raw, uint RawSize)> _sections = new();

    public bool Valid { get; }
    public ushort DllCharacteristics { get; }

    private PeResources(byte[] b)
    {
        _b = b;
        if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') return;
        var pe = BitConverter.ToInt32(b, 0x3C);
        if (pe <= 0 || (long)pe + 24 > b.Length) return;
        _opt = pe + 24;
        if ((long)_opt + 240 > b.Length || BitConverter.ToUInt16(b, _opt) != 0x20B) return;   // PE32+
        var numSections = Math.Min(BitConverter.ToUInt16(b, pe + 6), (ushort)96);
        var optSize = BitConverter.ToUInt16(b, pe + 20);
        for (int i = 0; i < numSections; i++)
        {
            var s = _opt + optSize + i * 40;
            if ((long)s + 40 > b.Length) return;
            _sections.Add((BitConverter.ToUInt32(b, s + 12), BitConverter.ToUInt32(b, s + 8),
                           BitConverter.ToUInt32(b, s + 20), BitConverter.ToUInt32(b, s + 16)));
        }
        DllCharacteristics = BitConverter.ToUInt16(b, _opt + 70);
        Valid = true;
    }

    public static PeResources Open(byte[] bytes) => new(bytes);

    private long Offset(uint rva)
    {
        foreach (var s in _sections)
            if (rva >= s.Va && rva < s.Va + Math.Max(s.VSize, s.RawSize))
            {
                var o = (long)s.Raw + (rva - s.Va);
                return o < _b.Length ? o : -1;
            }
        return -1;
    }

    private (uint Rva, uint Size) Directory(int index)
    {
        if (!Valid || BitConverter.ToUInt32(_b, _opt + 108) <= index) return (0, 0);
        return (BitConverter.ToUInt32(_b, _opt + 112 + index * 8), BitConverter.ToUInt32(_b, _opt + 116 + index * 8));
    }

    /// <summary>True when the image carries a CLR (.NET) header.</summary>
    public bool IsManaged => Directory(14).Rva != 0;

    /// <summary>Names exported by the DLL (at most 4096).</summary>
    public List<string> Exports()
    {
        var names = new List<string>();
        var (rva, _) = Directory(0);
        var e = rva == 0 ? -1 : Offset(rva);
        if (e < 0 || e + 40 > _b.Length) return names;
        var n = Math.Min(BitConverter.ToUInt32(_b, (int)e + 24), 4096u);
        var table = Offset(BitConverter.ToUInt32(_b, (int)e + 32));
        if (table < 0) return names;
        for (int i = 0; i < n; i++)
        {
            if (table + 4 * i + 4 > _b.Length) break;
            var s = Offset(BitConverter.ToUInt32(_b, (int)table + 4 * i));
            if (s < 0) continue;
            var end = Array.IndexOf(_b, (byte)0, (int)s, (int)Math.Min(260, _b.Length - s));
            if (end > s) names.Add(System.Text.Encoding.ASCII.GetString(_b, (int)s, end - (int)s));
        }
        return names;
    }

    // --- resource tree --------------------------------------------------------------------

    private long _root = -1;
    private int _seen;
    private readonly HashSet<long> _visited = new();

    private long Root()
    {
        if (_root >= 0) return _root;
        var (rva, _) = Directory(2);
        _root = rva == 0 ? -1 : Offset(rva);
        return _root;
    }

    // (id, absolute offset of the sub-directory or -1, absolute offset of the data entry or -1); named entries get id -1.
    private IEnumerable<(int Id, long Dir, long Data)> Entries(long dir)
    {
        if (dir < 0 || dir + 16 > _b.Length || !_visited.Add(dir)) yield break;
        int named = BitConverter.ToUInt16(_b, (int)dir + 12), ids = BitConverter.ToUInt16(_b, (int)dir + 14);
        var root = Root();
        for (int i = 0; i < named + ids; i++)
        {
            if (++_seen > MaxEntries) yield break;
            var e = dir + 16 + i * 8;
            if (e + 8 > _b.Length) yield break;
            var name = BitConverter.ToUInt32(_b, (int)e);
            var off = BitConverter.ToUInt32(_b, (int)e + 4);
            var id = (name & 0x80000000) != 0 ? -1 : (int)(name & 0xFFFF);
            var target = root + (off & 0x7FFFFFFF);
            if (target >= _b.Length) target = -1;
            yield return (off & 0x80000000) != 0 ? (id, target, -1) : (id, -1, target);
        }
    }

    /// <summary>
    /// type -> primary language -> set of resource name ids in that language, for menus,
    /// dialogs and string tables. Null when the resource tree is unreadable.
    /// </summary>
    public Dictionary<int, Dictionary<int, HashSet<int>>>? Languages()
    {
        try
        {
            var root = Root();
            if (!Valid) return null;
            if (root < 0) return new();
            _visited.Clear(); _seen = 0;
            var result = new Dictionary<int, Dictionary<int, HashSet<int>>>();
            foreach (var (typeId, typeDir, _) in Entries(root).ToList())
            {
                if (typeId is not (RtMenu or RtDialog or RtString) || typeDir < 0 || result.ContainsKey(typeId)) continue;
                var langs = new Dictionary<int, HashSet<int>>();
                foreach (var (nameId, nameDir, _) in Entries(typeDir).ToList())
                {
                    if (nameDir < 0) continue;
                    foreach (var (langId, _, _) in Entries(nameDir).ToList())
                    {
                        if (langId < 0) continue;
                        var primary = Key(langId);
                        if (!langs.TryGetValue(primary, out var set)) langs[primary] = set = new HashSet<int>();
                        set.Add(nameId);
                    }
                }
                result[typeId] = langs;
            }
            return result;
        }
        catch (ArgumentException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
    }

    /// <summary>FileVersion of the version resource (major, minor, build, revision); null when there is none.</summary>
    public (int, int, int, int)? FileVersion()
    {
        try
        {
            var root = Root();
            if (!Valid || root < 0) return null;
            _visited.Clear(); _seen = 0;
            foreach (var (typeId, typeDir, _) in Entries(root).ToList())
            {
                if (typeId != RtVersion || typeDir < 0) continue;
                foreach (var (_, nameDir, _) in Entries(typeDir).ToList())
                    foreach (var (_, _, data) in Entries(nameDir).ToList())
                    {
                        if (data < 0 || data + 8 > _b.Length) continue;
                        var at = Offset(BitConverter.ToUInt32(_b, (int)data));
                        var size = BitConverter.ToUInt32(_b, (int)data + 4);
                        if (at < 0 || size < 92 || at + size > _b.Length) continue;
                        // VS_FIXEDFILEINFO starts with the signature 0xFEEF04BD inside VS_VERSIONINFO
                        for (long i = at; i + 52 <= at + Math.Min(size, 512u); i += 4)
                            if (BitConverter.ToUInt32(_b, (int)i) == 0xFEEF04BD)
                            {
                                uint ms = BitConverter.ToUInt32(_b, (int)i + 8), ls = BitConverter.ToUInt32(_b, (int)i + 12);
                                return ((int)(ms >> 16), (int)(ms & 0xFFFF), (int)(ls >> 16), (int)(ls & 0xFFFF));
                            }
                    }
            }
            return null;
        }
        catch (ArgumentException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
    }

    /// <summary>
    /// Key of a resource language: the primary language id, for Chinese (0x04) a key per script:
    /// Simplified (zh-CN, zh-SG) and Traditional (zh-TW, zh-HK, zh-MO), see <see cref="ExtendedLanguages"/>.
    /// </summary>
    public static int Key(int langId)
    {
        var primary = langId & 0x3FF;
        if (primary != 0x04) return primary;
        var sub = langId >> 10;
        return sub is 0x02 or 0x04 ? ChineseSimplified : ChineseTraditional;
    }
    public const int ChineseSimplified = 0x10002, ChineseTraditional = 0x10001;

    /// <summary>The five further Power PDF UI languages (S1.2.0), keys as in <see cref="Key"/>.</summary>
    public static readonly (string Code, int Primary)[] ExtendedLanguages =
    {
        ("zh-Hans", ChineseSimplified), ("zh-Hant", ChineseTraditional), ("ja", 0x11), ("ko", 0x12), ("ar", 0x01),
    };

    /// <summary>Primary language id of each of the 16 Power PDF UI languages (manifest codes).</summary>
    public static readonly (string Code, int Primary)[] PowerPdfLanguages =
    {
        ("en", 0x09), ("de", 0x07), ("fr", 0x0C), ("it", 0x10), ("es", 0x0A), ("nl", 0x13), ("pt", 0x16), ("da", 0x06),
        ("fi", 0x0B), ("nb", 0x14), ("sv", 0x1D), ("pl", 0x15), ("cs", 0x05), ("hu", 0x0E), ("ru", 0x19), ("tr", 0x1F),
    };
}
