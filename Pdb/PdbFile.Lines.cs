using System.Collections.Concurrent;
using static PdbExplorer.Pdb.ByteReading;

namespace PdbExplorer.Pdb;

// C13 line tables: which source file and line an address comes from.
internal sealed unsafe partial class PdbFile
{
    const uint LinesSubsection = 0xF2;          // DEBUG_S_LINES
    const uint FileChecksumsSubsection = 0xF4;  // DEBUG_S_FILECHKSMS

    /// <summary>One DEBUG_S_LINES subsection: the code range it covers and where it is in the module stream.</summary>
    readonly record struct LineRange(ushort Section, uint Offset, uint CodeSize, uint Position, uint Length);

    /// <summary>
    /// The line subsections of a module sorted by address. A module can hold thousands of them (one per
    /// function), so they are indexed once instead of being scanned for every lookup.
    /// </summary>
    sealed record LineIndex(uint ChecksumsPosition, uint ChecksumsLength, LineRange[] Ranges);

    readonly ConcurrentDictionary<int, LineIndex?> _lineIndexes = new();

    /// <summary>Frees the line index of a module (bulk export goes through every module once).</summary>
    public void ReleaseLineIndex(Module module) => _lineIndexes.TryRemove(module.Index, out _);

    /// <summary>Source file and line of the code at section:offset, or null when the module has no line for it.</summary>
    public (string File, uint Line)? FindSourceLine(Module module, ushort section, uint offset)
    {
        if (_lineIndexes.GetOrAdd(module.Index, _ => BuildLineIndex(module)) is not { } index)
        {
            return null;
        }

        // Last range starting at or before the address.
        var ranges = index.Ranges;
        int low = 0;
        int high = ranges.Length - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            var candidate = ranges[middle];
            if (candidate.Section < section || (candidate.Section == section && candidate.Offset <= offset))
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        if (found < 0 || ranges[found].Section != section || offset >= ranges[found].Offset + ranges[found].CodeSize)
        {
            return null;
        }

        var range = ranges[found];
        var stream = GetStream(module.StreamIndex);
        uint fileEntry = 0;
        uint line = 0;
        bool hasLine = false;

        // The subsection is a 12-byte header followed by blocks: file entry, line count, block size, then (offset, line) pairs.
        uint end = range.Position + range.Length;
        for (uint block = range.Position + 12; block + 12 <= end;)
        {
            var blockHeader = stream.Read(block, 12);
            uint lineCount = U32(blockHeader, 4);
            uint blockSize = U32(blockHeader, 8);
            if (blockSize < 12 || (ulong)block + blockSize > end || 12UL + 8UL * lineCount > blockSize)
            {
                break;
            }

            var lines = stream.Read(block + 12, (int)(8 * lineCount));
            for (int i = 0; i < lineCount && U32(lines, 8 * i) + range.Offset <= offset; i++)
            {
                fileEntry = U32(blockHeader, 0);
                line = U32(lines, 8 * i + 4) & 0xFFFFFF;
                hasLine = true;
            }
            block += blockSize;
        }

        // 0xFEEFEE and 0xF00F00 are markers ("do not step into"), not line numbers.
        const uint firstSpecialLine = 0xF00F00;
        if (!hasLine || index.ChecksumsPosition == 0 || fileEntry + 4 > index.ChecksumsLength || line >= firstSpecialLine)
        {
            return null;
        }

        // A file entry starts with the offset of the file name in the /names table.
        uint nameOffset = U32(stream.Read(index.ChecksumsPosition + fileEntry, 4), 0);
        return (GetName(nameOffset), line);
    }

    LineIndex? BuildLineIndex(Module module)
    {
        if (FindStream(module.StreamIndex) is not { } stream || module.C13LinesSize == 0)
        {
            return null;
        }

        uint start = module.SymbolsSize + module.C11LinesSize;
        uint end = start + module.C13LinesSize;
        if (end > stream.Size)
        {
            return null;
        }

        uint checksumsPosition = 0;
        uint checksumsLength = 0;
        var ranges = new List<LineRange>();
        for (uint offset = start; offset + 8 <= end;)
        {
            var header = stream.Read(offset, 8);
            uint kind = U32(header, 0);
            uint length = U32(header, 4);
            uint content = offset + 8;
            if ((ulong)content + length > end)
            {
                break;
            }

            if (kind == FileChecksumsSubsection)
            {
                checksumsPosition = content;
                checksumsLength = length;
            }
            else if (kind == LinesSubsection && length >= 24)
            {
                var linesHeader = stream.Read(content, 12);
                ranges.Add(new LineRange(
                    Section: U16(linesHeader, 4),
                    Offset: U32(linesHeader, 0),
                    CodeSize: U32(linesHeader, 8),
                    Position: content,
                    Length: length));
            }
            offset = content + ((length + 3) & ~3u);
        }

        ranges.Sort((a, b) =>
            (a.Section, a.Offset, a.CodeSize, a.Position, a.Length).CompareTo((b.Section, b.Offset, b.CodeSize, b.Position, b.Length)));
        return new LineIndex(checksumsPosition, checksumsLength, ranges.ToArray());
    }
}
