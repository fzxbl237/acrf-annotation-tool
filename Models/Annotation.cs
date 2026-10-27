using System.Collections;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PatChes.Controls.DataGrid;

namespace AcrfAnnotationTool.Models;

public sealed partial class Annotation : ObservableObject, ICloneable, INotifyDataErrorInfo
{
    private const string NotSubmittedAnnotationText = "[NOT SUBMITTED]";

    [ObservableProperty]
    private string? _formName;

    [ObservableProperty]
    private int _pageNum;

    [ObservableProperty]
    private string _varName = string.Empty;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string _type = string.Empty;

    [ObservableProperty]
    private string _annotationText = string.Empty;

    [ObservableProperty]
    private string _annotationTextDomain = string.Empty;

    [ObservableProperty]
    private bool _annotationDotted;

    [ObservableProperty]
    private int _x;

    [ObservableProperty]
    private int _y;

    [ObservableProperty]
    private int _width;

    [ObservableProperty]
    private int _line = 1;

    [ObservableProperty]
    private string _formOutline = string.Empty;

    [ObservableProperty]
    private string _annotationUniqueId = Guid.NewGuid().ToString();

    [ObservableProperty]
    private bool _isSelected;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public bool HasErrors => NeedsAnnotationTextDomain();

    public IEnumerable GetErrors(string? propertyName)
    {
        if (!string.IsNullOrEmpty(propertyName) &&
            !string.Equals(propertyName, nameof(AnnotationTextDomain), StringComparison.Ordinal))
            return Array.Empty<DataGridValidationResult>();

        return NeedsAnnotationTextDomain()
            ? [new DataGridValidationResult("注释文本不为空时，所属域不能为空", DataGridValidationSeverity.Error)]
            : Array.Empty<DataGridValidationResult>();
    }

    // "[NOT SUBMITTED]" is the only annotation text allowed without a domain.
    private bool NeedsAnnotationTextDomain() =>
        !string.IsNullOrWhiteSpace(AnnotationText) &&
        !string.Equals(AnnotationText.Trim(), NotSubmittedAnnotationText, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(AnnotationTextDomain);

    partial void OnAnnotationTextChanged(string value) => RaiseDomainErrorsChanged();

    partial void OnAnnotationTextDomainChanged(string value) => RaiseDomainErrorsChanged();

    private void RaiseDomainErrorsChanged() =>
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(nameof(AnnotationTextDomain)));

    public object Clone()
    {
        var clone = (Annotation)MemberwiseClone();
        clone.ErrorsChanged = null;
        return clone;
    }
}
