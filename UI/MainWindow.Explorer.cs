using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PdbExplorer.Pdb;
using PdbExplorer.Rendering;

namespace PdbExplorer.UI;

// The symbol explorer: categories, search box and result list.
public partial class MainWindow
{
    /// <summary>Typing restarts this timer; the search runs when it fires.</summary>
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };

    Category? _category;
    int[] _items = [];
    CancellationTokenSource? _searchCancellation;
    string _resultCount = "";
    object[] _resultCountArguments = [];

    static Category[] CreateCategories(PdbFile pdb) =>
    [
        new("Classes", "C", TokenKind.Type, pdb.Types, Category.Kinds(TypeKinds.Class, TypeKinds.Interface), ItemKind.Type),
        new("Structs", "S", TokenKind.Type, pdb.Types, Category.Kinds(TypeKinds.Struct), ItemKind.Type),
        new("Unions", "U", TokenKind.Type, pdb.Types, Category.Kinds(TypeKinds.Union), ItemKind.Type),
        new("Enums", "E", TokenKind.Enumerator, pdb.Types, Category.Kinds(TypeKinds.Enum), ItemKind.Type),
        new("Typedefs", "T", TokenKind.Member, pdb.Typedefs, SymbolTable.AllKinds, ItemKind.Typedef),
        new("Functions", "ƒ", TokenKind.Function, pdb.Functions, SymbolTable.AllKinds, ItemKind.Function),
        new("Global variables", "V", TokenKind.Member, pdb.GlobalVariables, SymbolTable.AllKinds, ItemKind.GlobalVariable),
        new("Constants", "K", TokenKind.Number, pdb.Constants, SymbolTable.AllKinds, ItemKind.Constant),
        new("Public symbols", "P", TokenKind.Keyword, pdb.Publics, SymbolTable.AllKinds, ItemKind.PublicSymbol),
        new("Modules (.obj)", "M", TokenKind.Parameter, pdb.ModuleNames, SymbolTable.AllKinds, ItemKind.Module),
        new("Source files", "F", TokenKind.Comment, pdb.SourceFiles, SymbolTable.AllKinds, ItemKind.SourceFile),
        new("Sections", "§", TokenKind.Text, pdb.SectionNames, SymbolTable.AllKinds, ItemKind.Section),
        new("MSF streams", "≡", TokenKind.Text, pdb.StreamList, SymbolTable.AllKinds, ItemKind.Stream),
    ];

    void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Shows the functions with this name: used by links whose exact target symbol is not known.</summary>
    void SearchFunctions(string name)
    {
        Categories.SelectedItem = Categories.Items.OfType<Category>().First(category => category.ItemKind == ItemKind.Function);
        SearchBox.Text = name;
    }

    void OnCategorySelected(object sender, SelectionChangedEventArgs e)
    {
        _category = Categories.SelectedItem as Category;
        RunSearch();
    }

    void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    async void RunSearch()
    {
        _searchCancellation?.Cancel();
        if (_category is not { } category)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        string query = SearchBox.Text.Trim();
        var timer = Stopwatch.StartNew();

        int[] items;
        try
        {
            items = await Task.Run(() => category.Table.Search(query, category.KindMask, cancellation.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        _items = items;
        Results.ItemsSource = new VirtualList(category, items);
        if (query.Length == 0)
        {
            _resultCount = "{0:N0} items";
            _resultCountArguments = [items.Length];
        }
        else
        {
            _resultCount = "{0:N0} result(s) out of {1:N0} - {2} ms";
            _resultCountArguments = [items.Length, category.Count, timer.ElapsedMilliseconds];
        }
        ShowResultCount();
    }

    void ShowResultCount() =>
        CountText.Text = _resultCount.Length == 0 ? "" : Lang.Format(_resultCount, _resultCountArguments);

    Target? SelectedTarget =>
        Results.SelectedItem is ExplorerRow row && _pdb != null ? row.Category.TargetOf(_pdb, row.Item) : null;

    void OnRowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedTarget is { } target)
        {
            PropertyList.ItemsSource = DocumentRenderer.Describe(_pdb!, target).Properties;
        }
    }

    void OnRowDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (SelectedTarget is { } target)
        {
            Open(target);
        }
    }

    void OnRowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SelectedTarget is { } target)
        {
            Open(target);
            e.Handled = true;
        }
    }
}
