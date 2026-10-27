using System.Diagnostics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace AcrfAnnotationTool.Controls;

public class ExpandingButtonGroupItem : Button
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<ExpandingButtonGroupItem, string>(nameof(Label), string.Empty);
    public static readonly StyledProperty<Control?> IconProperty =
        AvaloniaProperty.Register<ExpandingButtonGroupItem, Control?>(nameof(Icon));
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public Control? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public static readonly StyledProperty<string?> DoneLabelProperty =
        AvaloniaProperty.Register<ExpandingButtonGroupItem, string?>(nameof(DoneLabel));
    public string? DoneLabel { get => GetValue(DoneLabelProperty); set => SetValue(DoneLabelProperty, value); }
    public Func<Task<bool>>? AsyncAction { get; set; }
    public bool IsDone { get; private set; }
    public bool IsBusy { get; private set; }
    private ContentPresenter? _iconPresenter;
    private readonly Control _doneIcon = new AtomUI.Icons.AntDesign.CheckOutlined
    {
        Width = 16, Height = 16, Foreground = Brush.Parse("#2E7D32")
    };
    public bool IsExpanded { get; private set; }
    internal ExpandingButtonGroup? Group { get; set; }
    private Control? _label;
    private readonly TranslateTransform _shift = new();
    private readonly BlurEffect _blur = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private double _width = 36, _velocity, _target = 36, _lastTime;
    private double _reveal, _revealFrom, _revealStart;
    private bool _measured;
    private static readonly SplineEasing Enter = new(0.16, 1, 0.3, 1);
    private static readonly SplineEasing Exit = new(0.22, 1, 0.36, 1);

    public ExpandingButtonGroupItem()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
        _timer.Stop();
    }
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _label = e.NameScope.Find<Control>("PART_Label");
        _iconPresenter = e.NameScope.Find<ContentPresenter>("PART_Icon");
        if (_label != null) _label.RenderTransform = _shift;
        UpdateFeedback();
        ApplyReveal();
    }
    internal void SetExpanded(bool expanded)
    {
        if (IsExpanded == expanded) return;
        AdvanceWidth();
        IsExpanded = expanded;
        PseudoClasses.Set(":expanded", expanded);
        _revealFrom = _reveal;
        _revealStart = _clock.Elapsed.TotalSeconds;
        if (IsLoaded) _timer.Start();
        else { _reveal = expanded ? 1 : 0; ApplyReveal(); }
        InvalidateMeasure();
    }
    private void ApplyReveal()
    {
        if (_label == null) return;
        _label.Opacity = Math.Clamp(_reveal, 0, 1);
        _shift.X = -6 * (1 - _reveal);
        _blur.Radius = 2 * (1 - _reveal);
        _label.Effect = _blur.Radius < 0.05 ? null : _blur;
    }
    private void AdvanceWidth()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Max(0, now - _lastTime);
        _lastTime = now;
        // Arc Morph: visualDuration=.42, bounce=.16. Preserve velocity on retarget.
        var omega = 2 * Math.PI / (1.2 * 0.42);
        const double zeta = 0.84;
        var wd = omega * Math.Sqrt(1 - zeta * zeta);
        var x = _width - _target;
        var b = (_velocity + zeta * omega * x) / wd;
        var decay = Math.Exp(-zeta * omega * dt);
        var cos = Math.Cos(wd * dt);
        var sin = Math.Sin(wd * dt);
        _width = _target + decay * (x * cos + b * sin);
        _velocity = decay * ((b * wd - zeta * omega * x) * cos - (x * wd + zeta * omega * b) * sin);
    }
    private void Tick()
    {
        AdvanceWidth();
        var elapsed = _clock.Elapsed.TotalSeconds - _revealStart;
        var progress = Math.Clamp((elapsed - (IsExpanded ? 0.04 : 0)) / (IsExpanded ? 0.24 : 0.12), 0, 1);
        _reveal = _revealFrom + ((IsExpanded ? 1 : 0) - _revealFrom) * (IsExpanded ? Enter : Exit).Ease(progress);
        ApplyReveal();
        if (Math.Abs(_width - _target) < 0.005 && Math.Abs(_velocity) < 0.01 && progress >= 1)
        {
            _width = _target;
            _velocity = 0;
            _timer.Stop();
        }
        InvalidateMeasure();
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(new Size(double.PositiveInfinity, availableSize.Height));
        var text = _label?.DesiredSize.Width ?? 0;
        var target = IsExpanded && text > 0 ? 10 + 16 + 7 + text + 14 : 36;
        if (!_measured || !IsLoaded)
        {
            _measured = true;
            _width = _target = target;
            _velocity = 0;
            _lastTime = _clock.Elapsed.TotalSeconds;
        }
        else if (Math.Abs(target - _target) > 0.5)
        {
            AdvanceWidth();
            _target = target;
            _timer.Start();
        }
        return new Size(Math.Max(36, _width), 36);
    }
    internal void SetDone(bool done)
    {
        IsDone = done;
        PseudoClasses.Set(":done", done);
        UpdateFeedback();
    }
    private void UpdateFeedback()
    {
        if (_label is TextBlock label) label.Text = IsDone ? DoneLabel ?? Label : Label;
        if (_iconPresenter != null) _iconPresenter.Content = IsDone ? _doneIcon : Icon;
        InvalidateMeasure();
    }
    protected override async void OnClick()
    {
        if (IsBusy || IsDone || Group?.ShouldInvoke(this) == false) return;
        var group = Group;
        if (group != null && !group.BeginInvoke(this)) return;
        IsBusy = true;
        PseudoClasses.Set(":busy", true);
        try
        {
            base.OnClick();
            var succeeded = AsyncAction is not { } action || await action();
            if (succeeded && IsLoaded && Group == group && !string.IsNullOrEmpty(DoneLabel))
                group?.Confirm(this);
        }
        catch (Exception exception)
        {
            Trace.TraceError("Expanding button action failed: {0}", exception);
        }
        finally
        {
            IsBusy = false;
            PseudoClasses.Set(":busy", false);
            group?.EndInvoke(this);
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Group?.PressItem(this, e.Pointer.Type);
        base.OnPointerPressed(e);
    }
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        Group?.FocusItem(this, e.NavigationMethod);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty || change.Property == DoneLabelProperty || change.Property == IconProperty)
        {
            AutomationProperties.SetName(this, Label);
            UpdateFeedback();
        }
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        _measured = false;
        _reveal = IsExpanded ? 1 : 0;
        ApplyReveal();
        base.OnDetachedFromVisualTree(e);
    }
}
