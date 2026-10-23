using System.Data;
using System.Windows;
using System.Windows.Controls;

namespace RDS_Results;

public partial class GridStyles : ResourceDictionary
{
    public GridStyles()
    {
        InitializeComponent();
    }

    public void ResultsGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (sender is DataGrid grid && grid.ItemsSource is DataView view && view.Table!.Columns.Contains(e.PropertyName))
            e.Column.Header = view.Table.Columns[e.PropertyName]!.Caption;
    }

    void ScrollFieldsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ReportSection section)
            return;
        if (Window.GetWindow(button) is MainWindow window)
            window.ChooseScrollFields(section, button);
    }
}
