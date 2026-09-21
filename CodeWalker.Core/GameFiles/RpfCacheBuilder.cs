using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodeWalker.GameFiles
{
    public static class RpfCacheBuilder
    {
        private const uint CacheMagic = 0x52485348; // HSHR
        private const uint TreeMagic = 0x43535454;  // TTSC
        private const uint IndexStart = 0x49445354; // TSDI
        private const uint IndexEnd = 0x4944454E;   // NEDI
        private static readonly object SyncRoot = new object();

        public static bool Rebuild(string gameFolder, string modsFolder, Action<string> log = null)
        {
            lock (SyncRoot) return RebuildCore(gameFolder, modsFolder, log);
        }

        private static bool RebuildCore(string gameFolder, string modsFolder, Action<string> log)
        {
            string templatePath = Path.Combine(gameFolder, "rpf.cache");
            if (!File.Exists(templatePath) || string.IsNullOrWhiteSpace(modsFolder)) return false;

            Directory.CreateDirectory(modsFolder);

            byte[] template = File.ReadAllBytes(templatePath);
            var records = ReadRecords(template);
            var recordsByHash = records.ToDictionary(record => record.Hash);
            int scanned = 0;
            int matched = 0;
            int unchanged = 0;

            foreach (string modPath in Directory.EnumerateFiles(modsFolder, "*.rpf", SearchOption.AllDirectories))
            {
                string relativePath = GetRelativePath(modsFolder, modPath).Replace('\\', '/');
                if (new DirectoryInfo(modsFolder).Name.Equals("mods", StringComparison.OrdinalIgnoreCase) &&
                    relativePath.StartsWith("versions/", StringComparison.OrdinalIgnoreCase)) continue;
                uint hash = JenkHash.GenHash(relativePath.ToLowerInvariant());
                if (!recordsByHash.TryGetValue(hash, out CacheRecord record))
                {
                    log?.Invoke($"RPF cache: no retail root for {relativePath}; skipped.");
                    continue;
                }
                matched++;
                RpfFile modRoot = Scan(modPath, relativePath);
                string basePath = GetBasePath(gameFolder, modsFolder, relativePath);
                RpfFile baseRoot = File.Exists(basePath) ? Scan(basePath, relativePath) : null;
                if (baseRoot != null && MetadataEquals(baseRoot, modRoot))
                {
                    unchanged++;
                    log?.Invoke($"RPF cache: {relativePath} matches the base archive; retaining Rockstar's cached metadata.");
                    continue;
                }
                record.Root = modRoot;
                record.BaseRoot = baseRoot;
                scanned += Flatten(record.Root).Count();
            }

            if (matched == 0)
            {
                log?.Invoke("RPF cache: no matching modified archives were found; retaining retail metadata.");
            }

            byte[] rebuilt;
            using (var output = new MemoryStream(template.Length))
            using (var writer = new BinaryWriter(output))
            {
                writer.Write(template, 0, records[0].TtscOffset);
                for (int i = 0; i < records.Count; i++) WriteRoot(writer, records[i]);
                rebuilt = output.ToArray();
            }

            ulong checksum = RpfCacheChecksum.UpdateInPlace(rebuilt);
            if (!RpfCacheChecksum.Verify(rebuilt))
                throw new InvalidDataException("Generated rpf.cache checksum verification failed.");

            string outputPath = Path.Combine(modsFolder, "rpf.cache");
            string temporaryPath = outputPath + ".tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, rebuilt);
                File.Copy(temporaryPath, outputPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            log?.Invoke($"RPF cache: reconstructed {outputPath} from {scanned} modified header(s) and {unchanged} retail-identical root(s); checksum=0x{checksum:X16}.");
            return true;
        }

        private static List<CacheRecord> ReadRecords(byte[] cache)
        {
            if (cache.Length < 20 || BitConverter.ToUInt32(cache, 0) != CacheMagic)
                throw new InvalidDataException("The retail rpf.cache has an unknown header.");
            uint count = BitConverter.ToUInt32(cache, 16);
            if (count == 0 || 20L + count * 8L > cache.Length)
                throw new InvalidDataException("The retail rpf.cache root table is truncated.");

            var records = new List<CacheRecord>();
            for (int i = 0; i < count; i++)
            {
                int tableOffset = 20 + i * 8;
                uint hash = BitConverter.ToUInt32(cache, tableOffset);
                int rpfOffset = checked((int)BitConverter.ToUInt32(cache, tableOffset + 4));
                int ttscOffset = FindTtsc(cache, rpfOffset);
                records.Add(new CacheRecord(hash, tableOffset, ttscOffset, rpfOffset));
            }

            for (int i = 0; i < records.Count; i++)
            {
                CacheRecord record = records[i];
                int end = i + 1 < records.Count ? records[i + 1].TtscOffset : cache.Length;
                if (end < record.RpfOffset) throw new InvalidDataException("The retail rpf.cache root order is invalid.");
                record.TreeData = new byte[end - record.RpfOffset];
                Buffer.BlockCopy(cache, record.RpfOffset, record.TreeData, 0, record.TreeData.Length);
            }
            return records;
        }

        private static int FindTtsc(byte[] cache, int rpfOffset)
        {
            for (int position = rpfOffset - 12; position >= Math.Max(0, rpfOffset - 27); position--)
            {
                if (BitConverter.ToUInt32(cache, position) == TreeMagic && Align16(position + 12) == rpfOffset)
                    return position;
            }
            throw new InvalidDataException($"No TTSC record precedes the retail RPF at 0x{rpfOffset:X}.");
        }

        private static void WriteRoot(BinaryWriter writer, CacheRecord record)
        {
            long ttscOffset = writer.BaseStream.Position;
            writer.Write(TreeMagic);
            writer.Write(0u);
            writer.Write(0u);
            Align16(writer);
            long rpfOffset = writer.BaseStream.Position;

            uint selfSpan;
            uint treeSpan;
            if (record.Root == null)
            {
                WriteRetailTree(writer, record, out long selfEnd, out long treeEnd);
                selfSpan = checked((uint)(selfEnd - (ttscOffset + 12)));
                treeSpan = checked((uint)(treeEnd - (ttscOffset + 12)));
            }
            else
            {
                WriteModifiedTree(writer, record, out long selfEnd, out long treeEnd);
                selfSpan = checked((uint)(selfEnd - (ttscOffset + 12)));
                treeSpan = checked((uint)(treeEnd - (ttscOffset + 12)));
            }

            long end = writer.BaseStream.Position;
            writer.BaseStream.Position = ttscOffset + 4;
            writer.Write(selfSpan);
            writer.Write(treeSpan);
            writer.BaseStream.Position = record.TableOffset + 4;
            writer.Write(checked((uint)rpfOffset));
            writer.BaseStream.Position = end;
        }

        private static void WriteModifiedTree(BinaryWriter writer, CacheRecord record, out long selfEnd, out long treeEnd)
        {
            RetailTree retail = ReadRetailTree(record);
            byte[] rootHeader = GetPreservedHeader(record.Root, record.BaseRoot, retail.RootHeader);
            WriteCachedHeader(writer, rootHeader);
            selfEnd = writer.BaseStream.Position;

            var modNested = Flatten(record.Root).Skip(1).ToList();
            var modByHash = modNested.ToDictionary(rpf => GetNestedPathHash(record.Root, rpf));
            var baseByHash = record.BaseRoot == null
                ? new Dictionary<uint, RpfFile>()
                : Flatten(record.BaseRoot).Skip(1).ToDictionary(rpf => GetNestedPathHash(record.BaseRoot, rpf));
            var retailByHash = retail.Nested.ToDictionary(archive => archive.Hash);
            var ordered = new List<KeyValuePair<uint, RpfFile>>();
            foreach (RetailArchive archive in retail.Nested)
            {
                if (modByHash.TryGetValue(archive.Hash, out RpfFile rpf))
                {
                    ordered.Add(new KeyValuePair<uint, RpfFile>(archive.Hash, rpf));
                    modByHash.Remove(archive.Hash);
                }
            }
            foreach (RpfFile rpf in modNested)
            {
                uint hash = GetNestedPathHash(record.Root, rpf);
                if (modByHash.ContainsKey(hash)) ordered.Add(new KeyValuePair<uint, RpfFile>(hash, rpf));
            }

            writer.Write(checked((uint)ordered.Count));
            writer.Write(IndexStart);
            long indexOffset = writer.BaseStream.Position;
            WriteZeros(writer, checked(ordered.Count * 8));
            writer.Write(IndexEnd);

            var offsets = new long[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
            {
                uint hash = ordered[i].Key;
                RpfFile mod = ordered[i].Value;
                retailByHash.TryGetValue(hash, out RetailArchive cached);
                baseByHash.TryGetValue(hash, out RpfFile original);
                byte[] header = GetPreservedHeader(mod, original, cached?.Header);
                Align16(writer);
                offsets[i] = writer.BaseStream.Position;
                WriteCachedHeader(writer, header);
            }
            treeEnd = writer.BaseStream.Position;

            writer.BaseStream.Position = indexOffset;
            for (int i = 0; i < ordered.Count; i++)
            {
                writer.Write(ordered[i].Key);
                writer.Write(checked((uint)offsets[i]));
            }
            writer.BaseStream.Position = treeEnd;
        }

        private static byte[] GetPreservedHeader(RpfFile modified, RpfFile original, byte[] retailHeader)
        {
            byte[] modifiedHeader = ReadHeader(modified);
            if (original != null && retailHeader != null && modifiedHeader.SequenceEqual(ReadHeader(original)))
                return retailHeader;
            return modifiedHeader;
        }

        private static void WriteRetailTree(BinaryWriter writer, CacheRecord record, out long selfEnd, out long treeEnd)
        {
            RetailTree retail = ReadRetailTree(record);
            WriteCachedHeader(writer, retail.RootHeader);
            selfEnd = writer.BaseStream.Position;
            writer.Write(checked((uint)retail.Nested.Count));
            writer.Write(IndexStart);
            long newEntriesOffset = writer.BaseStream.Position;
            WriteZeros(writer, checked(retail.Nested.Count * 8));
            writer.Write(IndexEnd);

            var offsets = new long[retail.Nested.Count];
            for (int i = 0; i < retail.Nested.Count; i++)
            {
                Align16(writer);
                offsets[i] = writer.BaseStream.Position;
                WriteCachedHeader(writer, retail.Nested[i].Header);
            }
            treeEnd = writer.BaseStream.Position;

            writer.BaseStream.Position = newEntriesOffset;
            for (int i = 0; i < retail.Nested.Count; i++)
            {
                writer.Write(retail.Nested[i].Hash);
                writer.Write(checked((uint)offsets[i]));
            }
            writer.BaseStream.Position = treeEnd;
        }

        private static RetailTree ReadRetailTree(CacheRecord record)
        {
            byte[] tree = record.TreeData;
            byte[] rootHeader = ReadCachedHeader(tree, 0, out uint entryCount);
            int indexOffset = checked(rootHeader.Length + (int)entryCount * 2);
            if (indexOffset + 12 > tree.Length) throw new InvalidDataException("A retail rpf.cache root has a truncated nested index.");
            uint nestedCount = BitConverter.ToUInt32(tree, indexOffset);
            if (BitConverter.ToUInt32(tree, indexOffset + 4) != IndexStart)
                throw new InvalidDataException("A retail rpf.cache root is missing its TSDI marker.");
            int entriesOffset = indexOffset + 8;
            int indexEnd = checked(entriesOffset + (int)nestedCount * 8);
            if (indexEnd + 4 > tree.Length || BitConverter.ToUInt32(tree, indexEnd) != IndexEnd)
                throw new InvalidDataException("A retail rpf.cache root is missing its NEDI marker.");

            var nested = new List<RetailArchive>(checked((int)nestedCount));
            for (int i = 0; i < nestedCount; i++)
            {
                uint hash = BitConverter.ToUInt32(tree, entriesOffset + i * 8);
                uint absoluteOffset = BitConverter.ToUInt32(tree, entriesOffset + i * 8 + 4);
                int relativeOffset = checked((int)(absoluteOffset - record.RpfOffset));
                nested.Add(new RetailArchive(hash, ReadCachedHeader(tree, relativeOffset, out _)));
            }
            return new RetailTree(rootHeader, nested);
        }

        private static byte[] ReadCachedHeader(byte[] tree, int offset, out uint entryCount)
        {
            if (offset < 0 || offset + 16 > tree.Length || BitConverter.ToUInt32(tree, offset) != 0x52504637)
                throw new InvalidDataException("A retail nested-cache offset does not point to an RPF7 header.");
            entryCount = BitConverter.ToUInt32(tree, offset + 4);
            uint nameLength = BitConverter.ToUInt32(tree, offset + 8) & 0x0FFFFFFF;
            int length = checked(16 + (int)entryCount * 16 + (int)nameLength);
            if (offset + length > tree.Length) throw new InvalidDataException("A retail cached RPF7 header is truncated.");
            var header = new byte[length];
            Buffer.BlockCopy(tree, offset, header, 0, length);
            return header;
        }

        private static void WriteCachedHeader(BinaryWriter writer, byte[] header)
        {
            writer.Write(header);
            WriteZeros(writer, checked((int)BitConverter.ToUInt32(header, 4) * 2));
        }

        private static uint GetNestedPathHash(RpfFile root, RpfFile nested)
        {
            if (!nested.Path.StartsWith(root.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Nested RPF path {nested.Path} is outside {root.Path}.");
            string path = nested.Path.Substring(root.Path.Length).TrimStart('\\', '/').Replace('\\', '/');
            if (path.Length == 0) throw new InvalidDataException($"Invalid nested RPF path: {nested.Path}");
            return JenkHash.GenHash(path.ToLowerInvariant());
        }

        private static void Align16(BinaryWriter writer)
        {
            int padding = checked((int)(Align16(writer.BaseStream.Position) - writer.BaseStream.Position));
            WriteZeros(writer, padding);
        }

        private static long Align16(long value) => checked((value + 15) & ~15L);

        private static void WriteZeros(BinaryWriter writer, int count)
        {
            if (count > 0) writer.Write(new byte[count]);
        }

        private static RpfFile Scan(string filePath, string relativePath)
        {
            var errors = new List<string>();
            var rpf = new RpfFile(filePath, relativePath);
            rpf.ScanStructure(null, errors.Add);
            if (rpf.Root == null || errors.Count > 0)
                throw new InvalidDataException($"Could not scan {filePath}: {string.Join(Environment.NewLine, errors)}");
            return rpf;
        }

        private static bool MetadataEquals(RpfFile left, RpfFile right)
        {
            var leftArchives = Flatten(left).ToDictionary(rpf => rpf.Path, StringComparer.OrdinalIgnoreCase);
            var rightArchives = Flatten(right).ToDictionary(rpf => rpf.Path, StringComparer.OrdinalIgnoreCase);
            if (leftArchives.Count != rightArchives.Count) return false;
            foreach (var item in leftArchives)
            {
                if (!rightArchives.TryGetValue(item.Key, out RpfFile other) ||
                    !ReadHeader(item.Value).SequenceEqual(ReadHeader(other))) return false;
            }
            return true;
        }

        private static string GetBasePath(string gameFolder, string modsFolder, string relativePath)
        {
            string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var root = new DirectoryInfo(modsFolder);
            string fileName = Path.GetFileName(normalized);
            if (root.Parent?.Name.Equals("versions", StringComparison.OrdinalIgnoreCase) == true &&
                (normalized.Equals(Path.Combine("update", "update.rpf"), StringComparison.OrdinalIgnoreCase) ||
                 normalized.Equals(Path.Combine("update", "update2.rpf"), StringComparison.OrdinalIgnoreCase)))
            {
                return Path.Combine(gameFolder, "update", "versions", root.Name, fileName);
            }
            return Path.Combine(gameFolder, normalized);
        }

        private static IEnumerable<RpfFile> Flatten(RpfFile root)
        {
            var pending = new Stack<RpfFile>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                RpfFile current = pending.Pop();
                yield return current;
                if (current.Children == null) continue;
                for (int i = current.Children.Count - 1; i >= 0; i--) pending.Push(current.Children[i]);
            }
        }

        private static byte[] ReadHeader(RpfFile rpf)
        {
            using (var stream = File.OpenRead(rpf.GetPhysicalFilePath()))
            using (var reader = new BinaryReader(stream))
            {
                stream.Position = rpf.StartPos;
                byte[] fixedHeader = reader.ReadBytes(16);
                if (fixedHeader.Length != 16 || BitConverter.ToUInt32(fixedHeader, 0) != 0x52504637)
                    throw new InvalidDataException($"Invalid RPF header at {rpf.Path}.");
                uint entryCount = BitConverter.ToUInt32(fixedHeader, 4);
                uint nameLength = BitConverter.ToUInt32(fixedHeader, 8) & 0x0FFFFFFF;
                int length = checked(16 + (int)entryCount * 16 + (int)nameLength);
                stream.Position = rpf.StartPos;
                byte[] header = reader.ReadBytes(length);
                if (header.Length != length) throw new EndOfStreamException($"Truncated RPF header at {rpf.Path}.");
                return header;
            }
        }

        private static string GetRelativePath(string root, string path)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{path} is outside {root}.");
            return fullPath.Substring(prefix.Length);
        }

        private sealed class CacheRecord
        {
            public uint Hash { get; }
            public int TableOffset { get; }
            public int TtscOffset { get; }
            public int RpfOffset { get; }
            public byte[] TreeData { get; set; }
            public RpfFile Root { get; set; }
            public RpfFile BaseRoot { get; set; }

            public CacheRecord(uint hash, int tableOffset, int ttscOffset, int rpfOffset)
            {
                Hash = hash;
                TableOffset = tableOffset;
                TtscOffset = ttscOffset;
                RpfOffset = rpfOffset;
            }
        }

        private sealed class RetailTree
        {
            public byte[] RootHeader { get; }
            public List<RetailArchive> Nested { get; }
            public RetailTree(byte[] rootHeader, List<RetailArchive> nested) { RootHeader = rootHeader; Nested = nested; }
        }

        private sealed class RetailArchive
        {
            public uint Hash { get; }
            public byte[] Header { get; }
            public RetailArchive(uint hash, byte[] header) { Hash = hash; Header = header; }
        }
    }
}
