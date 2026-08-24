using CommunityToolkit.Mvvm.ComponentModel;
using MiniExcelLibs.Attributes;

namespace AcrfAnnotationTool.Models;

public sealed partial class Annotation : ObservableObject, ICloneable
{
    [ObservableProperty]
    [property: ExcelColumnName("Form名称")]
    private string? _formName;

    [ObservableProperty]
    [property: ExcelColumnName("页码")]
    private int _pageNum;

    [ObservableProperty]
    [property: ExcelColumnName("变量名称")]
    private string _varName = string.Empty;

    [ObservableProperty]
    [property: ExcelColumnName("文本")]
    private string _text = string.Empty;

    [ObservableProperty]
    [property: ExcelColumnName("类型")]
    private string _type = string.Empty;

    [ObservableProperty]
    [property: ExcelColumnName("注释文本")]
    private string _annotationText = string.Empty;

    [ObservableProperty]
    [property: ExcelColumnName("注释文本所属域")]
    private string _annotationTextDomain = string.Empty;

    [ObservableProperty]
    [property: ExcelColumnName("虚线框")]
    private bool _annotationDotted;

    [ObservableProperty]
    [property: ExcelColumnName("X轴")]
    private int _x;

    [ObservableProperty]
    [property: ExcelColumnName("Y轴")]
    private int _y;

    [ObservableProperty]
    [property: ExcelColumnName("宽度")]
    private int _width;

    [ObservableProperty]
    [property: ExcelColumnName("行数")]
    private int _line = 1;

    [ObservableProperty]
    [property: ExcelColumnName("Form书签名称")]
    private string _formOutline = string.Empty;

    [ObservableProperty]
    [property: ExcelColumnName("注释唯一标识")]
    private string _annotationUniqueId = Guid.NewGuid().ToString();

    public object Clone() => MemberwiseClone();
}
