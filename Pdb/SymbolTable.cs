using System.Runtime.InteropServices;
using System.Text;

namespace PdbExplorer.Pdb;

/// <summary>
/// A searchable list of names. All names are stored back to back in one native buffer, which keeps millions
/// of them compact and lets a substring search scan plain memory instead of chasing strings.
/// Each item also carries a reference (a type index, a record offset...) and a small kind used for filtering.
/// </summary>
internal sealed unsafe class SymbolTable
{
    /// <summary>Kind mask that selects every item.</summary>
    public const int AllKinds = 0xFF;

    const int MaxKinds = 8;

    // 16384 names of at most 64 KB each always fit in one Span.
    const int SearchChunk = 1 << 14;

    readonly int[] _kindCounts = new int[MaxKinds];
    uint[] _nameOffsets = new uint[1024];   // Count + 1 entries: name i spans [_nameOffsets[i], _nameOffsets[i + 1])
    uint[] _references = new uint[1024];
    byte[] _kinds = new byte[1024];
    byte* _names;
    long _namesLength;
    long _namesCapacity;
    int[] _rank = [];

    ~SymbolTable() => NativeMemory.Free(_names);

    public int Count { get; private set; }

    /// <summary>Item indices in alphabetical order (ordinal, on the UTF-8 bytes). Available after <see cref="Sort"/>.</summary>
    public int[] SortedItems { get; private set; } = [];

    public uint Reference(int item) => _references[item];

    public void SetReference(int item, uint reference) => _references[item] = reference;

    public ReadOnlySpan<byte> NameBytes(int item) =>
        new(_names + _nameOffsets[item], (int)(_nameOffsets[item + 1] - _nameOffsets[item]));

    public string Name(int item) => Encoding.UTF8.GetString(NameBytes(item));

    /// <summary>Number of items whose kind is selected by <paramref name="kindMask"/> (bit n selects kind n).</summary>
    public int CountMatching(int kindMask)
    {
        int total = 0;
        for (int kind = 0; kind < MaxKinds; kind++)
        {
            if (Matches(kindMask, kind))
            {
                total += _kindCounts[kind];
            }
        }
        return total;
    }

    public void Add(ReadOnlySpan<byte> name, uint reference, byte kind = 0)
    {
        if (Count + 1 >= _nameOffsets.Length)
        {
            int capacity = _nameOffsets.Length * 2;
            Array.Resize(ref _nameOffsets, capacity);
            Array.Resize(ref _references, capacity);
            Array.Resize(ref _kinds, capacity);
        }
        if (_namesLength + name.Length > _namesCapacity)
        {
            _namesCapacity = Math.Max(_namesCapacity * 2, _namesLength + name.Length + (1 << 20));
            if (_namesCapacity > uint.MaxValue)
            {
                throw new InvalidDataException("Name table too large (> 4 GB).");
            }
            _names = (byte*)NativeMemory.Realloc(_names, (nuint)_namesCapacity);
        }

        name.CopyTo(new Span<byte>(_names + _namesLength, name.Length));
        _nameOffsets[Count] = (uint)_namesLength;
        _references[Count] = reference;
        _kinds[Count] = kind;
        _kindCounts[kind & (MaxKinds - 1)]++;
        _namesLength += name.Length;
        Count++;
        _nameOffsets[Count] = (uint)_namesLength;
    }

    /// <summary>Call once, after the last <see cref="Add"/>: trims the buffers and builds the alphabetical order.</summary>
    public void Sort()
    {
        Array.Resize(ref _nameOffsets, Count + 1);
        Array.Resize(ref _references, Count);
        Array.Resize(ref _kinds, Count);
        _namesCapacity = Math.Max(_namesLength, 1);
        _names = (byte*)NativeMemory.Realloc(_names, (nuint)_namesCapacity);

        var byName = Comparer<int>.Create((a, b) =>
        {
            int order = NameBytes(a).SequenceCompareTo(NameBytes(b));
            return order != 0 ? order : a.CompareTo(b);
        });
        SortedItems = ParallelEnumerable.Range(0, Count).OrderBy(item => item, byName).ToArray();

        _rank = new int[Count];
        for (int position = 0; position < Count; position++)
        {
            _rank[SortedItems[position]] = position;
        }
    }

    /// <summary>
    /// Range of positions in <see cref="SortedItems"/> whose name is exactly <paramref name="name"/>
    /// (empty when Start == End).
    /// </summary>
    public (int Start, int End) EqualRange(ReadOnlySpan<byte> name)
    {
        int start = LowerBound(name, orEqual: false);
        int end = LowerBound(name, orEqual: true);
        return (start, end);
    }

    /// <summary>
    /// Case-insensitive (ASCII) substring search among the items selected by <paramref name="kindMask"/>.
    /// Shortest names come first, then alphabetical order, so an exact match is always at the top.
    /// </summary>
    public int[] Search(string query, int kindMask, CancellationToken cancellation)
    {
        if (query.Length == 0)
        {
            return AllMatching(kindMask);
        }

        byte[] needle = Encoding.UTF8.GetBytes(query.ToLowerInvariant());
        var chunks = new List<int>?[(Count + SearchChunk - 1) / SearchChunk];
        Parallel.For(0, chunks.Length, new ParallelOptions { CancellationToken = cancellation }, chunk =>
        {
            chunks[chunk] = SearchChunkOfNames(chunk, needle, kindMask);
        });

        int[] items = chunks.Where(found => found != null).SelectMany(found => found!).ToArray();
        var keys = new long[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            long nameLength = _nameOffsets[items[i] + 1] - _nameOffsets[items[i]];
            keys[i] = (nameLength << 32) | (uint)_rank[items[i]];
        }
        Array.Sort(keys, items);
        return items;
    }

    static bool Matches(int kindMask, int kind) => (kindMask >> kind & 1) != 0;

    int LowerBound(ReadOnlySpan<byte> name, bool orEqual)
    {
        int low = 0;
        int high = SortedItems.Length;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            int order = NameBytes(SortedItems[middle]).SequenceCompareTo(name);
            if (order < 0 || (orEqual && order == 0))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    /// <summary>
    /// Every matching item in alphabetical order, with compiler-generated names
    /// (&lt;lambda_...&gt;, `anonymous namespace'...) moved after the real identifiers.
    /// </summary>
    int[] AllMatching(int kindMask)
    {
        int[] all = Array.FindAll(SortedItems, item => Matches(kindMask, _kinds[item]));
        return [.. Array.FindAll(all, IsIdentifier), .. Array.FindAll(all, item => !IsIdentifier(item))];
    }

    bool IsIdentifier(int item)
    {
        if (_nameOffsets[item + 1] == _nameOffsets[item])
        {
            return false;
        }
        byte first = _names[_nameOffsets[item]];
        return char.IsAsciiLetter((char)first) || first == '_';
    }

    /// <summary>Searches the names of one chunk as a single block of memory, then maps each hit back to its item.</summary>
    List<int>? SearchChunkOfNames(int chunk, byte[] needle, int kindMask)
    {
        int firstItem = chunk * SearchChunk;
        int endItem = Math.Min(Count, firstItem + SearchChunk);
        long blockStart = _nameOffsets[firstItem];
        var block = new ReadOnlySpan<byte>(_names + blockStart, (int)(_nameOffsets[endItem] - blockStart));

        List<int>? found = null;
        int item = firstItem;
        int position = 0;
        while (position < block.Length)
        {
            int hit = IndexOfIgnoreCase(block[position..], needle);
            if (hit < 0)
            {
                break;
            }

            // Last item starting at or before the hit: items only move forward, so search from the previous one.
            uint hitOffset = (uint)(blockStart + position + hit);
            int low = item;
            int high = endItem - 1;
            while (low < high)
            {
                int middle = (low + high + 1) >>> 1;
                if (_nameOffsets[middle] <= hitOffset)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }
            item = low;

            // Names have no separator, so a hit can straddle two of them: that is not a match.
            if (hitOffset + needle.Length > _nameOffsets[item + 1])
            {
                position += hit + 1;
                continue;
            }

            if (Matches(kindMask, _kinds[item]))
            {
                (found ??= []).Add(item);
            }
            position = (int)(_nameOffsets[item + 1] - blockStart);
        }
        return found;
    }

    /// <summary>Index of <paramref name="needle"/> (already lower-case) in <paramref name="text"/>, ignoring ASCII case.</summary>
    static int IndexOfIgnoreCase(ReadOnlySpan<byte> text, ReadOnlySpan<byte> needle)
    {
        byte first = needle[0];
        bool firstIsLetter = IsLowerLetter(first);
        byte firstUpper = (byte)(first - 32);

        int position = 0;
        while (position + needle.Length <= text.Length)
        {
            var candidates = text.Slice(position, text.Length - position - needle.Length + 1);
            int next = firstIsLetter ? candidates.IndexOfAny(first, firstUpper) : candidates.IndexOf(first);
            if (next < 0)
            {
                return -1;
            }

            position += next;
            int matched = 1;
            while (matched < needle.Length && EqualsIgnoreCase(text[position + matched], needle[matched]))
            {
                matched++;
            }
            if (matched == needle.Length)
            {
                return position;
            }
            position++;
        }
        return -1;
    }

    static bool IsLowerLetter(byte value) => value is >= (byte)'a' and <= (byte)'z';

    static bool EqualsIgnoreCase(byte value, byte lower) => value == lower || (IsLowerLetter(lower) && value == lower - 32);
}
