// ============================================================================
// FILE: src/2_BreadboardAOI.Core/KiCad/KicadSymbolLibrary.cs
// PURPOSE: Read-only access to the bundled KiCad symbol archive. Symbols are
//          loaded straight from the zip on demand, so the 223 MB extracted tree
//          never touches the operator's disk.
//
// THREADING: safe for concurrent readers. Zip entry streams are opened per load
//            and the parse cache is guarded by a single lock.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BreadboardAOI.Core.Contracts;

namespace BreadboardAOI.Core.KiCad;

public sealed class KicadSymbolLibrary : IDisposable
{
    public const string DefaultArchive = "assets/kicad/kicad-symbols-master.zip";

    private const int CacheCapacity = 96;

    private static readonly string[] PreferredLibraries =
    [
        "Device", "Timer", "74xx", "4xxx", "Switch", "Amplifier_Operational",
        "Regulator_Linear", "Transistor_BJT", "Diode", "Connector"
    ];

    /// <summary>Symbols offered first in the picker, in a technician's order.</summary>
    private static readonly KicadSymbolRef[] Curated =
    [
        new("Device", "R"),
        new("Device", "C"),
        new("Device", "C_Polarized"),
        new("Device", "LED"),
        new("Device", "D"),
        new("Device", "L"),
        new("Timer", "NE555P"),
        new("74xx", "74LS00"),
        new("74xx", "74HC595"),
        new("Switch", "SW_Push"),
        new("Switch", "SW_SPST"),
        new("Amplifier_Operational", "LM358"),
        new("Regulator_Linear", "L7805"),
        new("Transistor_BJT", "2N3904"),
        new("MCU_Microchip_ATmega", "ATmega328P-P")
    ];

    private readonly object _gate = new();
    private readonly ZipArchive? _zip;
    private readonly string? _rootDirectory;
    private readonly Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, KicadSymbol?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();
    private readonly List<KicadSymbolRef> _index = new(24000);

    private KicadSymbolLibrary(ZipArchive? zip, string? rootDirectory)
    {
        _zip = zip;
        _rootDirectory = rootDirectory;

        if (zip is not null)
            IndexZip(zip);
        else if (rootDirectory is not null)
            IndexDirectory(rootDirectory);
    }

    public int SymbolCount => _index.Count;

    /// <summary>Libraries present in the archive, e.g. "Device", "Timer", "74xx".</summary>
    public IReadOnlyList<string> Libraries =>
        _index.Select(entry => entry.Library)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Opens a zip archive or an extracted library folder.</summary>
    public static KicadSymbolLibrary Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            if (Directory.Exists(path))
                return new KicadSymbolLibrary(null, path);

            throw new FileNotFoundException(
                $"KiCad symbol library not found: '{path}'. Expected the bundled zip at {DefaultArchive}.",
                path);
        }

        ZipArchive zip = ZipFile.OpenRead(path);
        return new KicadSymbolLibrary(zip, null);
    }

    /// <summary>
    /// Finds and opens the bundled archive. Search order: the BBAOI_KICAD_ZIP
    /// environment variable, then <c>assets/kicad/…</c> under the working folder
    /// and the application folder (walking up a few parents for a repo layout).
    /// </summary>
    public static bool TryOpenDefault(out KicadSymbolLibrary? library, out string? error)
    {
        library = null;
        error = null;

        string? archive = Environment.GetEnvironmentVariable("BBAOI_KICAD_ZIP");
        if (!string.IsNullOrWhiteSpace(archive) && File.Exists(archive))
        {
            library = Open(archive);
            return true;
        }

        foreach (string candidate in CandidatePaths())
        {
            if (!File.Exists(candidate))
                continue;

            try
            {
                library = Open(candidate);
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                error = $"KiCad library at '{candidate}' could not be opened: {ex.Message}";
            }
        }

        error ??= $"KiCad library not found. Expected {DefaultArchive} next to the application.";
        return false;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string relative = DefaultArchive.Replace('/', Path.DirectorySeparatorChar);

        yield return Path.Combine(Environment.CurrentDirectory, relative);

        string baseDirectory = AppContext.BaseDirectory;
        for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(baseDirectory); depth++)
        {
            yield return Path.Combine(baseDirectory, relative);

            DirectoryInfo? parent = Directory.GetParent(baseDirectory);
            if (parent is null)
                yield break;

            baseDirectory = parent.FullName;
        }
    }

    /// <summary>Default symbol for a part kind, used when the operator has not picked one.</summary>
    public static string DefaultSymbolId(ComponentKind kind) => kind switch
    {
        ComponentKind.Resistor => "Device/R",
        ComponentKind.Capacitor => "Device/C_Polarized",
        ComponentKind.Led => "Device/LED",
        ComponentKind.IntegratedCircuit => "Timer/NE555P",
        ComponentKind.PushButton => "Switch/SW_Push",
        _ => "Device/R"
    };

    /// <summary>True when the id names a symbol this archive actually contains.</summary>
    public bool Contains(string? symbolId)
    {
        if (string.IsNullOrWhiteSpace(symbolId))
            return false;

        lock (_gate)
            return _entries.ContainsKey(Normalise(symbolId));
    }

    /// <summary>Search by symbol name. Empty query returns the curated starter list.</summary>
    public IReadOnlyList<KicadSymbolRef> Search(string? query, int limit = 80)
    {
        if (limit <= 0)
            return Array.Empty<KicadSymbolRef>();

        string text = (query ?? string.Empty).Trim();

        lock (_gate)
        {
            if (text.Length == 0)
            {
                var starters = new List<KicadSymbolRef>(Curated.Length);
                foreach (KicadSymbolRef curated in Curated)
                {
                    if (_entries.ContainsKey(curated.FullId))
                        starters.Add(curated);

                    if (starters.Count >= limit)
                        break;
                }

                return starters;
            }

            var matches = new List<(KicadSymbolRef Symbol, int Rank, int LibraryRank, int Length)>();
            foreach (KicadSymbolRef entry in _index)
            {
                int rank = entry.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)
                    ? (entry.Name.Equals(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    : entry.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                        ? 2
                        : -1;

                if (rank < 0)
                    continue;

                matches.Add((entry, rank, LibraryRank(entry.Library), entry.Name.Length));
            }

            matches.Sort(static (a, b) =>
            {
                int order = a.Rank.CompareTo(b.Rank);
                if (order != 0)
                    return order;

                order = a.LibraryRank.CompareTo(b.LibraryRank);
                if (order != 0)
                    return order;

                order = a.Length.CompareTo(b.Length);
                return order != 0
                    ? order
                    : string.Compare(a.Symbol.Name, b.Symbol.Name, StringComparison.OrdinalIgnoreCase);
            });

            int count = Math.Min(limit, matches.Count);
            var result = new KicadSymbolRef[count];
            for (int i = 0; i < count; i++)
                result[i] = matches[i].Symbol;

            return result;
        }
    }

    /// <summary>Loads "Library/Symbol" (or a bare symbol name from any library).</summary>
    public KicadSymbol? LoadById(string? symbolId)
    {
        if (string.IsNullOrWhiteSpace(symbolId))
            return null;

        string id = Normalise(symbolId);

        int slash = id.IndexOf('/');
        if (slash > 0 && slash < id.Length - 1)
            return Load(id[..slash], id[(slash + 1)..]);

        lock (_gate)
        {
            foreach (KicadSymbolRef entry in _index)
            {
                if (string.Equals(entry.Name, id, StringComparison.OrdinalIgnoreCase))
                    return LoadCore(entry.Library, entry.Name, 0);
            }
        }

        return null;
    }

    public KicadSymbol? Load(string library, string name)
    {
        if (string.IsNullOrWhiteSpace(library) || string.IsNullOrWhiteSpace(name))
            return null;

        lock (_gate)
            return LoadCore(library.Trim(), name.Trim(), 0);
    }

    private KicadSymbol? LoadCore(string library, string name, int depth)
    {
        if (depth > 8)
            return null;

        string key = library + "/" + name;
        if (_cache.TryGetValue(key, out KicadSymbol? cached))
            return cached;

        KicadSymbol? parsed = ParseEntry(library, name);
        if (parsed is null)
        {
            Remember(key, null);
            return null;
        }

        if (parsed.BaseName is { Length: > 0 } baseName
            && !string.Equals(baseName, name, StringComparison.OrdinalIgnoreCase))
        {
            KicadSymbol? baseSymbol = LoadCore(library, baseName, depth + 1);
            if (baseSymbol is not null)
                parsed = Merge(parsed, baseSymbol);
        }

        Remember(key, parsed);
        return parsed;
    }

    /// <summary>
    /// A derived symbol ("NE555P extends NE555D") only carries properties; the
    /// body and pins come from the base. Fill every gap from the base.
    /// </summary>
    private static KicadSymbol Merge(KicadSymbol derived, KicadSymbol baseSymbol) =>
        new(
            derived.Library,
            derived.Name,
            derived.BaseName,
            string.IsNullOrWhiteSpace(derived.Reference) ? baseSymbol.Reference : derived.Reference,
            string.IsNullOrWhiteSpace(derived.Value) || string.Equals(derived.Value, derived.Name, StringComparison.Ordinal)
                ? derived.Name
                : derived.Value,
            string.IsNullOrWhiteSpace(derived.Description) ? baseSymbol.Description : derived.Description,
            string.IsNullOrWhiteSpace(derived.Footprint) ? baseSymbol.Footprint : derived.Footprint,
            derived.Pins.Count > 0 ? derived.Pins : baseSymbol.Pins,
            derived.Graphics.Count > 0 ? derived.Graphics : baseSymbol.Graphics);

    private KicadSymbol? ParseEntry(string library, string name)
    {
        string key = library + "/" + name;
        if (!_entries.TryGetValue(key, out string? entry))
            return null;

        string text = _zip is not null
            ? ReadZipText(entry)
            : File.ReadAllText(entry, Encoding.UTF8);

        return KicadSymbolParser.Parse(library, text);
    }

    private string ReadZipText(string entryName)
    {
        ZipArchiveEntry entry = _zip!.GetEntry(entryName)
            ?? throw new FileNotFoundException($"Zip entry '{entryName}' disappeared.");

        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private void Remember(string key, KicadSymbol? symbol)
    {
        if (_cache.ContainsKey(key))
            return;

        _cache[key] = symbol;
        _cacheOrder.Enqueue(key);

        while (_cacheOrder.Count > CacheCapacity)
            _cache.Remove(_cacheOrder.Dequeue());
    }

    private void IndexZip(ZipArchive zip)
    {
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (!entry.FullName.EndsWith(".kicad_sym", StringComparison.OrdinalIgnoreCase))
                continue;

            int nameStart = entry.FullName.LastIndexOf('/');
            if (nameStart <= 0)
                continue;

            string fileName = entry.FullName[(nameStart + 1)..];
            string directory = entry.FullName[..nameStart];
            int directoryStart = directory.LastIndexOf('/');
            string folder = directoryStart >= 0 ? directory[(directoryStart + 1)..] : directory;

            const string suffix = ".kicad_symdir";
            if (!folder.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            string library = folder[..^suffix.Length];
            string name = fileName[..^".kicad_sym".Length];

            if (library.Length == 0 || name.Length == 0)
                continue;

            AddEntry(library, name, entry.FullName);
        }
    }

    private void IndexDirectory(string root)
    {
        foreach (string file in Directory.EnumerateFiles(root, "*.kicad_sym", SearchOption.AllDirectories))
        {
            string directory = Path.GetDirectoryName(file) ?? root;
            string folder = Path.GetFileName(directory);

            const string suffix = ".kicad_symdir";
            string library = folder.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? folder[..^suffix.Length]
                : folder;

            string name = Path.GetFileNameWithoutExtension(file);
            AddEntry(library, name, file);
        }
    }

    private void AddEntry(string library, string name, string location)
    {
        string key = library + "/" + name;
        if (!_entries.TryAdd(key, location))
            return;

        _index.Add(new KicadSymbolRef(library, name));
    }

    private static int LibraryRank(string library)
    {
        for (int i = 0; i < PreferredLibraries.Length; i++)
        {
            if (string.Equals(PreferredLibraries[i], library, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return PreferredLibraries.Length;
    }

    private static string Normalise(string symbolId)
    {
        string trimmed = symbolId.Trim().Replace('\\', '/');
        return trimmed.StartsWith('/') ? trimmed[1..] : trimmed;
    }

    public void Dispose() => _zip?.Dispose();
}
