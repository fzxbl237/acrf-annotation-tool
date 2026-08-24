using System;
using Avalonia;

namespace PatChes.Controls.DataGrid.Searching;

public static class DataGridColumnSearch
{
    public static readonly AttachedProperty<bool> IsSearchableProperty =
        AvaloniaProperty.RegisterAttached<DataGridColumn, bool>("IsSearchable", typeof(DataGridColumnSearch), defaultValue: true);
    public static readonly AttachedProperty<string?> SearchMemberPathProperty =
        AvaloniaProperty.RegisterAttached<DataGridColumn, string?>("SearchMemberPath", typeof(DataGridColumnSearch));
    public static readonly AttachedProperty<Func<object, string?>?> TextProviderProperty =
        AvaloniaProperty.RegisterAttached<DataGridColumn, Func<object, string?>?>("TextProvider", typeof(DataGridColumnSearch));

    public static void SetIsSearchable(AvaloniaObject target, bool value) => target.SetValue(IsSearchableProperty, value);
    public static bool GetIsSearchable(AvaloniaObject target) => target.GetValue(IsSearchableProperty);
    public static void SetSearchMemberPath(AvaloniaObject target, string? value) => target.SetValue(SearchMemberPathProperty, value);
    public static string? GetSearchMemberPath(AvaloniaObject target) => target.GetValue(SearchMemberPathProperty);
    public static void SetTextProvider(AvaloniaObject target, Func<object, string?>? value) => target.SetValue(TextProviderProperty, value);
    public static Func<object, string?>? GetTextProvider(AvaloniaObject target) => target.GetValue(TextProviderProperty);
}
