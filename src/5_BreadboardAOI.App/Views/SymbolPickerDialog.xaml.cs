// ============================================================================
// FILE: src/5_BreadboardAOI.App/Views/SymbolPickerDialog.xaml.cs
// PURPOSE: Code-behind for the KiCad symbol browser. The dialog owns a short
//          lived library handle; it is closed when the window closes so the zip
//          is never held open by a dismissed picker.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using BreadboardAOI.Core.KiCad;

namespace BreadboardAOI.App.Views;

public partial class SymbolPickerDialog : Window
{
    private const int MaxResults = 400;

    private KicadSymbolLibrary? _library;
    private bool _initialised;

    public SymbolPickerDialog()
        : this(null)
    {
    }

    public SymbolPickerDialog(string? initialSymbolId)
    {
        InitializeComponent();

        _initialised = true;

        if (!KicadSymbolLibrary.TryOpenDefault(out KicadSymbolLibrary? library, out string? error))
        {
            HintText.Text = error ?? "The KiCad library could not be opened.";
            UseButton.IsEnabled = false;
            return;
        }

        _library = library!;
        HintText.Text = $"{_library.SymbolCount:N0} symbols in {_library.Libraries.Count} libraries.";

        if (!string.IsNullOrWhiteSpace(initialSymbolId))
        {
            SearchBox.Text = initialSymbolId.Trim();
            SearchBox.SelectAll();
        }
        else
        {
            Populate(string.Empty);
        }
    }

    /// <summary>"Library/Symbol" the operator picked, or null to clear the symbol.</summary>
    public string? SelectedSymbolId { get; private set; }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialised)
            return;

        Populate(SearchBox.Text);
    }

    private void Populate(string query)
    {
        if (_library is null)
            return;

        IReadOnlyList<KicadSymbolRef> results = _library.Search(query, MaxResults);

        var ids = new List<string>(results.Count);
        foreach (KicadSymbolRef entry in results)
            ids.Add(entry.FullId);

        SymbolList.ItemsSource = ids;

        if (ids.Count == 0)
        {
            HintText.Text = string.IsNullOrWhiteSpace(query)
                ? "No starter symbols found; type a name."
                : $"No symbol matches '{query.Trim()}'.";
            return;
        }

        SymbolList.SelectedIndex = 0;
        HintText.Text = string.IsNullOrWhiteSpace(query)
            ? $"Starter symbols. {_library.SymbolCount:N0} available; type to search."
            : $"{ids.Count} match(es), best first.";
    }

    private void OnUse(object sender, RoutedEventArgs e) => Accept();

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (SymbolList.SelectedItem is not string id)
        {
            HintText.Text = "Pick a symbol from the list first.";
            return;
        }

        SelectedSymbolId = id;
        DialogResult = true;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        SelectedSymbolId = null;
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        _library?.Dispose();
        _library = null;
    }
}
