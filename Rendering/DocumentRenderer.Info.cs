using PdbExplorer.Pdb;

namespace PdbExplorer.Rendering;

// The summary page shown when a PDB is opened.
internal sealed partial class DocumentRenderer
{
    void RenderInfo()
    {
        _document.Title = Path.GetFileName(_pdb.FilePath);
        Property("File", _pdb.FilePath);
        Property("Size", Lang.Format("{0:N1} MB ({1:N0} bytes)", _pdb.FileSize / 1048576.0, _pdb.FileSize));
        Property("Container", Lang.Format("MSF 7.00 - {0:N0} pages of {1} bytes, {2:N0} streams", _pdb.PageCount, _pdb.PageSize, _pdb.StreamCount));
        Property("GUID", _pdb.Guid.ToString("B").ToUpperInvariant());
        Property("Age", _pdb.Age.ToString());

        // GUID + age is the folder name symbol servers store the PDB under.
        Property("Symbol server key", $"{_pdb.Guid:N}{_pdb.Age:X}".ToUpperInvariant());
        Property("Timestamp", $"{DateTimeOffset.FromUnixTimeSeconds(_pdb.Timestamp):yyyy-MM-dd HH:mm:ss} UTC (0x{_pdb.Timestamp:X8})");
        Property("Version", _pdb.Version + (_pdb.Features.Count > 0 ? $" - {string.Join(", ", _pdb.Features)}" : ""));
        Property("Machine", _pdb.Machine switch
        {
            0x8664 => "x64 (AMD64)",
            0x014C => "x86",
            0xAA64 => "ARM64",
            0x01C4 => "ARM",
            var machine => $"0x{machine:X4}",
        });
        Property("Types (TPI)", Lang.Format("{0:N0} records, including {1:N0} distinct class/struct/union/enum", _pdb.Tpi.Count, _pdb.Types.Count));
        Property("Ids (IPI)", Count(_pdb.Ipi.Count));
        Property("Functions", Count(_pdb.Functions.Count));
        Property("Global variables", Count(_pdb.GlobalVariables.Count));
        Property("Constants", Count(_pdb.Constants.Count));
        Property("Typedefs", Count(_pdb.Typedefs.Count));
        Property("Public symbols", Count(_pdb.Publics.Count));
        Property("Modules", Count(_pdb.Modules.Count));
        Property("Source files", Count(_pdb.SourceFiles.Count));
        Property("Sections", _pdb.Sections.Length.ToString());
        Property("Loaded in", Lang.Format("{0:F1} s", _pdb.LoadTime.TotalSeconds));
        if (_propertiesOnly)
        {
            return;
        }

        WriteHeader();
        _out.Comment("// " + Lang.Tr("Double-click (or Enter) in the explorer: open an item.")).Line();
        _out.Comment("// " + Lang.Tr("Ctrl+Click or F12 on a type / a method: go to its definition.  Ctrl+F: find in the document.")).Line();
        _out.Line();

        WriteSectionTitle("Sections");
        foreach (var section in _pdb.Sections)
        {
            _out.Write(Indent).Write($"{section.Name,-10}", TokenKind.Member)
                .Number($" RVA 0x{section.VirtualAddress:X8}  {Lang.Tr("size")} 0x{section.VirtualSize:X8}").Line();
        }
        _out.Line();

        WriteRecordCounts("Type records (TPI)", _pdb.TypeRecordCounts, kind => ((LeafKind)kind).DisplayName());
        WriteRecordCounts("Id records (IPI)", _pdb.IdRecordCounts, kind => ((LeafKind)kind).DisplayName());
        WriteRecordCounts("Global symbol records", _pdb.SymbolRecordCounts, kind => ((SymbolKind)kind).DisplayName());
    }

    static string Count(int value) => Lang.Format("{0:N0}", value);

    /// <summary>How many records of each kind a stream contains, most frequent first.</summary>
    void WriteRecordCounts(string title, int[] counts, Func<ushort, string> nameOf)
    {
        WriteSectionTitle(Lang.Tr(title));
        var kinds = Enumerable.Range(0, counts.Length).Where(kind => counts[kind] > 0).OrderByDescending(kind => counts[kind]);
        foreach (int kind in kinds)
        {
            _out.Write(Indent).Keyword($"{nameOf((ushort)kind),-40}").Number(Lang.Format("{0,14:N0}", counts[kind])).Line();
        }
        _out.Line();
    }
}
