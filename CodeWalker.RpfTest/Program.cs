using CodeWalker.GameFiles;

if (args.Length == 3 && args[0] == "--rebuild-cache")
{
    var game = Path.GetFullPath(args[1]);
    var mods = Path.GetFullPath(args[2]);
    GTA5Keys.LoadFromPath(game, false, null);
    GTA5Keys.EnsureNgEncryptionTables();
    return RpfCacheBuilder.Rebuild(game, mods, Console.WriteLine) ? 0 : 1;
}

if (args.Length == 3 && args[0] == "--list-rpfs")
{
    var archive = Path.GetFullPath(args[1]);
    GTA5Keys.LoadFromPath(Path.GetFullPath(args[2]), false, null);
    GTA5Keys.EnsureNgEncryptionTables();
    var root = new RpfFile(archive, Path.GetFileName(archive));
    root.ScanStructure(null, Console.Error.WriteLine);
    var pending = new Stack<RpfFile>();
    pending.Push(root);
    while (pending.Count > 0)
    {
        var current = pending.Pop();
        Console.WriteLine($"{current.StartPos:X12} {JenkHash.GenHash(current.Path.ToLowerInvariant()):X8} {current.Path}");
        for (var i = (current.Children?.Count ?? 0) - 1; i >= 0; i--) pending.Push(current.Children[i]);
    }
    return 0;
}

if (args.Length is < 1 or > 3)
{
    Console.Error.WriteLine("Usage: CodeWalker.RpfTest <archive.rpf> [gta-folder] [ng-key-length]\n       CodeWalker.RpfTest --rebuild-cache <gta-folder> <mods-folder>");
    return 2;
}

var archivePath = Path.GetFullPath(args[0]);
var gameFolder = args.Length == 2 ? Path.GetFullPath(args[1]) : FindGameFolder(archivePath);
if (!File.Exists(archivePath))
{
    Console.Error.WriteLine($"RPF not found: {archivePath}");
    return 2;
}
if (gameFolder == null)
{
    Console.Error.WriteLine("Could not find GTA5.exe; pass the GTA folder as the second argument.");
    return 2;
}

try
{
    Console.WriteLine($"Loading keys from: {gameFolder}");
    GTA5Keys.LoadFromPath(gameFolder, false, null);
    GTA5Keys.EnsureNgEncryptionTables();
    var cipherTest = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
    var cipherRoundTrip = GTACrypto.DecryptNG(GTACrypto.EncryptNG(cipherTest, "dlc.rpf", 123456), "dlc.rpf", 123456);
    if (!cipherTest.SequenceEqual(cipherRoundTrip))
        throw new InvalidOperationException("CodeWalker's NG encryption tables fail an encrypt/decrypt round trip.");

    var errors = new List<string>();
    var rpf = new RpfFile(archivePath, Path.GetFileName(archivePath));
    if (args.Length == 3) rpf.FileSize = long.Parse(args[2]);
    rpf.ScanStructure(null, errors.Add);
    if (rpf.Root == null)
    {
        var error = rpf.LastError ?? "The root archive could not be parsed.";
        if (!errors.Any(e => e.Contains(error))) errors.Add(error);
        var candidates = FindNgKeyCandidates(archivePath);
        Console.Error.WriteLine(candidates.Count == 0
            ? "NG diagnostic: no GTA V NG key can decrypt this table of contents."
            : "NG diagnostic: plausible key-length residues: " + string.Join(", ", candidates));
    }
    else
    {
        Validate(rpf, new FileInfo(archivePath).Length, errors, out var archives, out var files);
        Console.WriteLine($"Parsed {archives:N0} archive(s) and tested {files:N0} file(s).");
    }

    foreach (var error in errors) Console.Error.WriteLine("ERROR: " + error);
    Console.WriteLine(errors.Count == 0 ? "VALID: no structural or extraction errors found." : $"INVALID: {errors.Count:N0} error(s) found.");
    return errors.Count == 0 ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine("FATAL: " + ex);
    return 1;
}

static string FindGameFolder(string path)
{
    for (var dir = Directory.GetParent(path); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "GTA5.exe"))) return dir.FullName;
    }
    return null;
}

static List<int> FindNgKeyCandidates(string path)
{
    using var reader = new BinaryReader(File.OpenRead(path));
    if (reader.ReadUInt32() != 0x52504637) return new();
    var count = reader.ReadUInt32();
    reader.ReadUInt32();
    if (reader.ReadUInt32() != (uint)RpfEncryption.NG || count == 0 || count > 1_000_000) return new();
    var encrypted = reader.ReadBytes(checked((int)count * 16));
    var result = new List<int>();
    for (var residue = 0; residue < 101; residue++)
    {
        var entries = GTACrypto.DecryptNG(encrypted, Path.GetFileName(path), (uint)residue);
        if (BitConverter.ToUInt32(entries, 4) != 0x7FFFFF00) continue;
        var valid = true;
        for (var offset = 0; offset < entries.Length; offset += 16)
        {
            var type = BitConverter.ToUInt32(entries, offset + 4);
            if (type != 0x7FFFFF00 && (type & 0x80000000) == 0 && BitConverter.ToUInt32(entries, offset + 12) > 1)
            {
                valid = false;
                break;
            }
        }
        if (valid) result.Add(residue);
    }
    return result;
}

static void Validate(RpfFile rpf, long physicalLength, List<string> errors, out int archiveCount, out int fileCount)
{
    archiveCount = 1;
    fileCount = 0;
    if (rpf.AllEntries == null) return;

    foreach (var dir in rpf.AllEntries.OfType<RpfDirectoryEntry>())
    {
        if ((ulong)dir.EntriesIndex + dir.EntriesCount > (ulong)rpf.AllEntries.Count)
            errors.Add($"{dir.Path}: directory entry range is outside the table of contents.");
    }

    foreach (var entry in rpf.AllEntries.OfType<RpfFileEntry>())
    {
        fileCount++;
        var size = entry.GetFileSize();
        var start = rpf.StartPos + (long)entry.FileOffset * 512;
        if (start < 0 || size < 0 || start > physicalLength || size > physicalLength - start)
        {
            errors.Add($"{entry.Path}: data range {start:N0}..{start + size:N0} is outside the {physicalLength:N0}-byte archive.");
            continue;
        }

        if (size > 0)
        {
            var data = rpf.ExtractFile(entry);
            if (data == null) errors.Add($"{entry.Path}: extraction failed. {rpf.LastError}");
            else if (entry is RpfBinaryFileEntry binary && binary.FileSize > 0 && data.LongLength != binary.FileUncompressedSize)
                errors.Add($"{entry.Path}: extracted {data.LongLength:N0} bytes, expected {binary.FileUncompressedSize:N0}.");
        }
    }

    foreach (var child in rpf.Children ?? Enumerable.Empty<RpfFile>())
    {
        Validate(child, physicalLength, errors, out var childArchives, out var childFiles);
        archiveCount += childArchives;
        fileCount += childFiles;
    }
}
