using System.Data;
using System.Windows;
using System.Windows.Controls;

namespace RDS_Results;

static class BoardFit
{
    public static int Across(int count, double width)
    {
        if (count <= 1)
            return 1;
        var maxAcross = Math.Max(1, (int)Math.Floor(width / 220));
        return Math.Min(count, maxAcross);
    }

    public static double FontSize(double panelWidth) => panelWidth switch
    {
        >= 720 => 13,
        >= 480 => 12,
        >= 320 => 11,
        _ => 10
    };

    public static void Apply(DataGrid grid, double panelWidth)
    {
        if (grid.ItemsSource is not DataView view || view.Table == null || grid.Columns.Count == 0)
            return;

        var font = FontSize(panelWidth);
        var roles = new ColumnRole[grid.Columns.Count];
        var hasChipTime = false;
        for (var i = 0; i < grid.Columns.Count; i++)
        {
            roles[i] = RoleOf(view, grid.Columns[i]);
            if (roles[i] == ColumnRole.ChipTime)
                hasChipTime = true;
        }

        var show = Choose(roles, hasChipTime, panelWidth, font);
        grid.RowHeight = font + 9;
        grid.ColumnHeaderHeight = font + 12;
        for (var i = 0; i < grid.Columns.Count; i++)
        {
            var column = grid.Columns[i];
            if (!show[i])
            {
                column.Visibility = Visibility.Collapsed;
                continue;
            }

            column.Visibility = Visibility.Visible;
            if (roles[i] == ColumnRole.Name)
            {
                column.MinWidth = 56;
                column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            }
            else
            {
                column.MinWidth = 24;
                column.Width = new DataGridLength(ColumnWidth(roles[i], font));
            }
        }
    }

    static bool[] Choose(ColumnRole[] roles, bool hasChipTime, double panelWidth, double font)
    {
        var available = Math.Max(80, panelWidth - 28);
        var show = new bool[roles.Length];
        var order = new int[roles.Length];
        for (var i = 0; i < roles.Length; i++)
            order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            var group = Group(roles[a], hasChipTime).CompareTo(Group(roles[b], hasChipTime));
            return group != 0 ? group : a.CompareTo(b);
        });

        double used = 0;
        var stop = false;
        foreach (var index in order)
        {
            var width = ColumnWidth(roles[index], font);
            if (Group(roles[index], hasChipTime) == 0)
            {
                show[index] = true;
                used += width;
                continue;
            }

            if (stop || used + width > available)
            {
                stop = true;
                continue;
            }

            show[index] = true;
            used += width;
        }

        return show;
    }

    static ColumnRole RoleOf(DataView view, DataGridColumn column)
    {
        var name = column.SortMemberPath;
        if (string.IsNullOrEmpty(name) && column is DataGridBoundColumn bound && bound.Binding is System.Windows.Data.Binding binding)
            name = binding.Path?.Path ?? "";
        if (string.IsNullOrEmpty(name) || !view.Table!.Columns.Contains(name))
            return ColumnRole.Other;
        return view.Table.Columns[name]!.ExtendedProperties["role"] is ColumnRole role ? role : ColumnRole.Other;
    }

    static int Group(ColumnRole role, bool hasChipTime)
    {
        var finish = role == ColumnRole.ChipTime || (!hasChipTime && role == ColumnRole.Clock);
        if (role is ColumnRole.Bib or ColumnRole.Name || finish)
            return 0;
        return role switch
        {
            ColumnRole.Place => 1,
            ColumnRole.Gender or ColumnRole.Age => 2,
            ColumnRole.Pace => 3,
            ColumnRole.Division or ColumnRole.DivisionPlace => 4,
            ColumnRole.City or ColumnRole.State => 5,
            ColumnRole.Clock or ColumnRole.Chip or ColumnRole.AgeGroup or ColumnRole.Event => 6,
            _ => 7
        };
    }

    static double ColumnWidth(ColumnRole role, double font)
    {
        var scale = font / 13.0;
        var raw = role switch
        {
            ColumnRole.Bib => 64,
            ColumnRole.Name => 108,
            ColumnRole.ChipTime or ColumnRole.Clock => 86,
            ColumnRole.Place or ColumnRole.DivisionPlace => 58,
            ColumnRole.Gender => 74,
            ColumnRole.Age => 46,
            ColumnRole.Pace => 64,
            ColumnRole.State => 48,
            ColumnRole.Chip => 72,
            ColumnRole.City => 96,
            ColumnRole.Division or ColumnRole.AgeGroup or ColumnRole.Event => 104,
            _ => 96
        };
        return Math.Round(raw * scale);
    }
}
