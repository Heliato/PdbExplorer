using System.Buffers.Binary;
using System.Collections.Concurrent;
using static PdbExplorer.Pdb.ByteReading;
using static PdbExplorer.Pdb.SymbolKind;

namespace PdbExplorer.Pdb;

// A function that is always inlined has no symbol of its own, so its parameter names are nowhere in the
// global symbols. Its inline sites, however, can still carry the parameters as S_LOCAL records. This index
// remembers, for each member function, one inline site whose parameters line up with the signature.
internal sealed unsafe partial class PdbFile
{
    /// <summary>A scope being walked. <see cref="Inlinee"/> is 0 unless it is an inline site still worth examining.</summary>
    record struct OpenScope(uint Inlinee, uint Offset, int ParameterCount);

    // Sorted hashes of (member function type, method name) and, for each, the module and offset of the inline site.
    ulong[] _inlineKeys = [];
    ulong[] _inlineLocations = [];

    public bool HasInlinedParameterIndex { get; private set; }

    /// <summary>Number of always-inlined functions whose parameter names were found.</summary>
    public int InlinedFunctionCount => _inlineKeys.Length;

    /// <summary>Walks the symbols of every module once. Takes a moment on a large PDB: meant to run in the background.</summary>
    public void BuildInlinedParameterIndex(CancellationToken cancellation)
    {
        // Set when a function has been recorded. Updated by several threads without locking:
        // the worst outcome of a race is the same function being recorded twice, which is harmless.
        var recorded = new byte[Ipi.Count];
        var perThread = new ConcurrentBag<List<(ulong Key, ulong Location)>>();

        Parallel.ForEach(
            Modules,
            new ParallelOptions { CancellationToken = cancellation },
            () => new List<(ulong Key, ulong Location)>(),
            (module, _, found) =>
            {
                if (FindStream(module.StreamIndex) is { } stream)
                {
                    CollectInlineSites(module, stream, recorded, found);
                }
                return found;
            },
            perThread.Add);

        var entries = perThread.SelectMany(found => found).ToArray();
        ulong[] keys = Array.ConvertAll(entries, entry => entry.Key);
        ulong[] locations = Array.ConvertAll(entries, entry => entry.Location);
        Array.Sort(keys, locations);

        _inlineLocations = locations;
        _inlineKeys = keys;
        HasInlinedParameterIndex = true;
    }

    /// <summary>Module and symbol offset of an inline site that carries the parameter names of this member function.</summary>
    public (int Module, uint Offset)? FindInlineSite(uint methodType, ReadOnlySpan<byte> methodName)
    {
        int position = Array.BinarySearch(_inlineKeys, InlineKey(methodType, methodName));
        if (position < 0)
        {
            return null;
        }
        ulong location = _inlineLocations[position];
        return ((int)(location >> 32), (uint)location);
    }

    static ulong InlineKey(uint functionType, ReadOnlySpan<byte> name)
    {
        Span<byte> typeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(typeBytes, functionType);
        return Fnv1a.Hash(name, Fnv1a.Hash(typeBytes));
    }

    void CollectInlineSites(Module module, MsfStream stream, byte[] recorded, List<(ulong Key, ulong Location)> found)
    {
        uint end = Math.Min(module.SymbolsSize, stream.Size);
        var scopes = new List<OpenScope>();
        for (uint offset = 4; (ulong)offset + 4 <= end;)
        {
            var header = stream.Read(offset, 4);
            int length = U16(header, 0);
            var kind = (SymbolKind)U16(header, 2);
            if (length < 2 || (ulong)offset + 2 + (uint)length > end)
            {
                break;
            }

            if (kind.IsInlineSite() && length >= 14)
            {
                uint inlinee = U32(stream.Read(offset + 12, 4), 0);
                bool worthExamining = Ipi.Contains(inlinee) && recorded[inlinee - Ipi.FirstIndex] == 0;
                scopes.Add(new OpenScope(worthExamining ? inlinee : 0, offset, 0));
            }
            else if (kind.OpensScope())
            {
                scopes.Add(new OpenScope(0, offset, 0));
            }
            else if (kind.ClosesScope() && scopes.Count > 0)
            {
                var closed = scopes[^1];
                scopes.RemoveAt(scopes.Count - 1);
                if (closed.Inlinee != 0 && closed.ParameterCount > 0 && recorded[closed.Inlinee - Ipi.FirstIndex] == 0)
                {
                    RecordIfComplete(module, closed, recorded, found);
                }
            }
            else if (kind == S_LOCAL && scopes.Count > 0 && scopes[^1].Inlinee != 0)
            {
                var local = new LocalSymbol(stream.Read(offset + 2, length));
                if (local.IsParameter && !local.Name.SequenceEqual("this"u8))
                {
                    scopes[^1] = scopes[^1] with { ParameterCount = scopes[^1].ParameterCount + 1 };
                }
            }
            offset += 2 + (uint)length;
        }
    }

    /// <summary>
    /// Keeps the site only if it has exactly as many parameters as the signature: the compiler drops the ones
    /// it optimized away, and a partial list could not be matched to the right parameters.
    /// </summary>
    void RecordIfComplete(Module module, OpenScope site, byte[] recorded, List<(ulong Key, ulong Location)> found)
    {
        var record = Ipi.Record(site.Inlinee);
        if ((LeafKind)U16(record, 0) != LeafKind.LF_MFUNC_ID)
        {
            return;
        }

        var method = new FunctionIdRecord(record);
        if (GetSignature(method.FunctionType) is not { } signature || signature.NamedParameterCount != site.ParameterCount)
        {
            return;
        }

        recorded[site.Inlinee - Ipi.FirstIndex] = 1;
        found.Add((InlineKey(method.FunctionType, method.Name), (ulong)module.Index << 32 | site.Offset));
    }
}
