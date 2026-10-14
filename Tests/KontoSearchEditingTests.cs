using System.Windows;
using System.Windows.Controls;
using PpEvidencija.Models;
using PpEvidencija.Views;

namespace PpEvidencija.Tests;

[Collection("WPF")]
public sealed class KontoSearchEditingTests
{
    [Theory]
    [InlineData("h", 1, 0, "i", "hi")]
    [InlineData("hlo", 1, 0, "i", "hilo")]
    [InlineData("halo", 1, 1, "i", "hilo")]
    public Task OpeningSearchResultsKeepsTheInsertionPointAndSelectedRange(
        string text, int start, int length, string input, string expected) =>
        WpfTestHost.Run(() => WithEditor((comboBox, editor) =>
        {
            editor.Text = text;
            editor.Select(start, length);

            MainWindow.PrikaziRezultateKonta(comboBox, [new("21101", "Hilton")], true);

            Assert.True(comboBox.IsDropDownOpen);
            Assert.Equal(start, editor.SelectionStart);
            Assert.Equal(length, editor.SelectionLength);
            editor.SelectedText = input;
            Assert.Equal(expected, comboBox.Text);
        }));

    [Fact]
    public Task EmptyResultsDoNotSelectOrReplaceTheCurrentSearchText() =>
        WpfTestHost.Run(() => WithEditor((comboBox, editor) =>
        {
            editor.Text = "nepoznat";
            editor.Select(editor.Text.Length, 0);

            MainWindow.PrikaziRezultateKonta(comboBox, [], true);

            Assert.Empty(comboBox.Items);
            Assert.Null(comboBox.SelectedItem);
            editor.SelectedText = "i";
            Assert.Equal("nepoznati", comboBox.Text);
        }));

    [Fact]
    public Task RefreshingResultsPreservesTypingAfterEditingAnExistingSelection() =>
        WpfTestHost.Run(() => WithEditor((comboBox, editor) =>
        {
            var previous = new Konto("21101", "Stari kupac");
            var result = new Konto("21102", "Hilton");
            comboBox.ItemsSource = new[] { previous };
            comboBox.SelectedItem = previous;
            comboBox.IsDropDownOpen = true;
            editor.Text = "hi";
            editor.Select(2, 0);

            MainWindow.PrikaziRezultateKonta(comboBox, [result], true);

            Assert.Null(comboBox.SelectedItem);
            editor.SelectedText = "l";
            Assert.Equal("hil", comboBox.Text);
            comboBox.SelectedItem = result;
            Assert.Equal(result.Prikaz, comboBox.Text);
        }));

    private static void WithEditor(Action<ComboBox, TextBox> action)
    {
        var comboBox = new ComboBox
        {
            IsEditable = true,
            IsTextSearchEnabled = false,
            StaysOpenOnEdit = true,
            DisplayMemberPath = nameof(Konto.Prikaz)
        };
        var window = new Window
        {
            Content = comboBox,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            Width = 400,
            Height = 100
        };

        try
        {
            window.Show();
            window.UpdateLayout();
            var editor = Assert.IsType<TextBox>(comboBox.Template.FindName("PART_EditableTextBox", comboBox));
            action(comboBox, editor);
        }
        finally
        {
            comboBox.IsDropDownOpen = false;
            window.Close();
        }
    }
}
