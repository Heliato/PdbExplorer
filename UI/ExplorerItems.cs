using System.Collections;
using System.Windows.Media;
using PdbExplorer.Pdb;
using PdbExplorer.Rendering;

namespace PdbExplorer.UI;

/// <summary>
/// One line of the category list: a symbol table, optionally restricted to some kinds of items
/// (bit n of <see cref="KindMask"/> selects kind n).
/// </summary>
internal sealed class Category(string title, string glyph, TokenKind color, SymbolTable table, int kindMask, ItemKind itemKind)
{
    public string Title => Lang.Tr(title);
    public string Glyph => glyph;
    public Brush Brush => AppTheme.For(color);
    public SymbolTable Table => table;
    public int KindMask => kindMask;
    public ItemKind ItemKind => itemKind;
    public int Count { get; } = table.CountMatching(kindMask);
    public string CountText => Count.ToString("N0", Lang.Culture);

    public static int Kinds(params byte[] kinds) => kinds.Aggregate(0, (mask, kind) => mask | 1 << kind);

    public Target TargetOf(PdbFile pdb, int item)
    {
        uint reference = table.Reference(item);
        return itemKind == ItemKind.Function ? Target.Function(pdb, reference) : new Target(itemKind, reference);
    }

    /// <summary>Public symbols are stored decorated; the list shows them undecorated.</summary>
    public string DisplayName(int item)
    {
        string name = table.Name(item);
        return itemKind == ItemKind.PublicSymbol ? Undecorator.Undecorate(name, nameOnly: true) : name;
    }
}

/// <summary>A row of the result list. Rows are created on demand and compared by value.</summary>
internal sealed record ExplorerRow(Category Category, int Item, int Position)
{
    public string Name => Category.DisplayName(Item);
    public string Glyph => Category.Glyph;
    public Brush Brush => Category.Brush;
}

/// <summary>
/// Read-only list handed to the ListBox. WPF only asks for the rows it displays, so millions of items
/// cost nothing until they are scrolled into view.
/// </summary>
internal sealed class VirtualList(Category category, int[] items) : IList
{
    public int Count => items.Length;
    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public object? this[int index]
    {
        get => new ExplorerRow(category, items[index], index);
        set => throw new NotSupportedException();
    }

    /// <summary>WPF calls this to locate the selected row: answer from the row itself instead of scanning.</summary>
    public int IndexOf(object? value)
    {
        bool isRowOfThisList = value is ExplorerRow row
            && ReferenceEquals(row.Category, category)
            && (uint)row.Position < (uint)items.Length
            && items[row.Position] == row.Item;
        return isRowOfThisList ? ((ExplorerRow)value!).Position : -1;
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator GetEnumerator()
    {
        for (int i = 0; i < items.Length; i++)
        {
            yield return this[i];
        }
    }

    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < items.Length; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }

    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
