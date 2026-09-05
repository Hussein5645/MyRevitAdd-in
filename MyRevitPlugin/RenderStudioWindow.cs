using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ComboBox = System.Windows.Controls.ComboBox;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using Path = System.IO.Path;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using TextBox = System.Windows.Controls.TextBox;
using WpfVisibility = System.Windows.Visibility;

namespace MyRevitPlugin;

internal interface IRenderRequest
{
    string Prompt { get; }
    string RenderStyle { get; }
    string RenderArea { get; }
    string Fidelity { get; }
    int PixelSize { get; }
    IReadOnlyList<string> ReferenceImagePaths { get; }
}

internal interface IMaskedRenderRequest
{
    string? MaskPath { get; }
    string EditGuidance { get; }
}

internal sealed class RevitStudioBridge
{
    private readonly StudioRevitEventHandler _handler = new();
    private readonly ExternalEvent _externalEvent;
    public RevitStudioBridge() => _externalEvent = ExternalEvent.Create(_handler);
    public void Attach(RenderStudioWindow window) => _handler.Attach(window);
    public void Capture() { _handler.Action = StudioRevitAction.Capture; Raise(); }
    private void Raise()
    {
        ExternalEventRequest result = _externalEvent.Raise();
        if (result is ExternalEventRequest.Denied or ExternalEventRequest.TimedOut)
            _handler.NotifyError(new InvalidOperationException("Revit is busy. Finish the current command and try again."));
    }
}

internal enum StudioRevitAction { None, Capture }

internal sealed class StudioRevitEventHandler : IExternalEventHandler
{
    private WeakReference<RenderStudioWindow>? _window;
    public StudioRevitAction Action { get; set; }
    public void Attach(RenderStudioWindow window) => _window = new WeakReference<RenderStudioWindow>(window);
    public string GetName() => "AI Render Studio Revit bridge";
    public void Execute(UIApplication application)
    {
        if (_window is null || !_window.TryGetTarget(out RenderStudioWindow? window)) return;
        StudioRevitAction action = Action;
        Action = StudioRevitAction.None;
        try
        {
            UIDocument uiDocument = application.ActiveUIDocument ?? throw new InvalidOperationException("Open a Revit project first.");
            if (action == StudioRevitAction.Capture)
            {
                View view = uiDocument.ActiveView;
                if (!view.CanBePrinted) throw new InvalidOperationException("The active Revit view cannot be captured.");
                string path = AiRenderActiveViewCommand.ExportActiveView(uiDocument, view, window);
                window.Dispatcher.BeginInvoke(() => window.OnCaptureCompleted(path, view.Name));
            }
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { window.Dispatcher.BeginInvoke(window.OnRevitActionCancelled); }
        catch (Exception exception) { NotifyError(exception); }
    }
    public void NotifyError(Exception exception)
    {
        if (_window is not null && _window.TryGetTarget(out RenderStudioWindow? window))
            window.Dispatcher.BeginInvoke(() => window.OnRevitActionFailed(exception));
    }
}

internal sealed record StudioComment(int Number, string Text, double ImageX, double ImageY);
internal sealed record StudioAreaEdit(int Number, Rect Selection, CanvasTool Kind, string Instruction);
internal enum CanvasTool { None, Comment, Replace, Remove }

internal sealed class RenderStudioWindow : Window, IRenderRequest, IMaskedRenderRequest
{
    private readonly RevitStudioBridge _bridge;
    private readonly ComboBox _style;
    private readonly ComboBox _area;
    private readonly ComboBox _resolution;
    private readonly ComboBox _fidelity;
    private readonly TextBox _prompt;
    private readonly List<string> _references = new();
    private readonly List<StudioComment> _comments = new();
    private readonly List<StudioAreaEdit> _areaEdits = new();
    private readonly WrapPanel _referenceGrid = new();
    private readonly TextBlock _referenceStatus = new();
    private readonly StackPanel _commentList = new();
    private readonly Image _viewport = new();
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent };
    private readonly TextBlock _viewportLabel = new();
    private readonly TextBlock _status = new();
    private readonly ProgressBar _progress = new();
    private readonly StackPanel _resultActions = new();
    private readonly Button _renderButton;
    private readonly Button _galleryButton;
    private readonly ProjectGalleryInfo _galleryProject;
    private CancellationTokenSource? _cancellation;
    private string? _sourcePath;
    private string? _resultPath;
    private string? _maskPath;
    private string _currentViewName;
    private bool _resultSaved;
    private CanvasTool _tool;
    private Point _dragStart;
    private Rect? _dragPreview;

    public RenderStudioWindow(UIDocument uiDocument, View activeView, string? initialImagePath = null, ProjectGalleryInfo? galleryProject = null, RevitStudioBridge? bridge = null)
    {
        _bridge = bridge ?? new RevitStudioBridge();
        _bridge.Attach(this);
        _sourcePath = initialImagePath;
        _currentViewName = activeView.Name;
        _galleryProject = galleryProject ?? RenderGalleryStorage.ForDocument(uiDocument.Document);
        Title = "AI Render Studio";
        Width = 1320;
        Height = 860;
        MinWidth = 1080;
        MinHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = RenderUi.WindowBackground;
        Foreground = RenderUi.PrimaryText;

        var root = new DockPanel();
        var header = new Grid { Background = Brushes.White, Height = 74 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerText = new StackPanel { Margin = new Thickness(26, 14, 0, 0) };
        headerText.Children.Add(new TextBlock { Text = "AI Render Studio", FontSize = 23, FontWeight = FontWeights.SemiBold });
        headerText.Children.Add(new TextBlock { Text = "A live rendering workspace connected to your Revit view", Foreground = RenderUi.MutedText, Margin = new Thickness(0, 3, 0, 0) });
        header.Children.Add(headerText);
        var close = RenderUi.Button("Close", false, 86, new Thickness(0, 19, 24, 0));
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var content = new Grid { Margin = new Thickness(20) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        var controls = new StackPanel();
        controls.Children.Add(RenderUi.SectionLabel("RENDER SETTINGS"));
        controls.Children.Add(RenderUi.FieldLabel("Visual style"));
        _style = RenderUi.ComboBox(0);
        foreach (string item in new[] { "Photorealistic architectural", "Warm golden-hour exterior", "Soft editorial interior", "Competition visualization" }) _style.Items.Add(item);
        controls.Children.Add(_style);
        controls.Children.Add(RenderUi.FieldLabel("Capture area"));
        _area = RenderUi.ComboBox(0);
        foreach (string item in new[] { "Visible viewport (current zoom)", "Current Revit crop region", "Full printable view" }) _area.Items.Add(item);
        controls.Children.Add(_area);
        var settingRow = new Grid();
        settingRow.ColumnDefinitions.Add(new ColumnDefinition());
        settingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        settingRow.ColumnDefinitions.Add(new ColumnDefinition());
        var quality = new StackPanel();
        quality.Children.Add(RenderUi.FieldLabel("Quality"));
        _resolution = RenderUi.ComboBox(1);
        foreach (string item in new[] { "Draft · Low", "Standard · Medium", "Final · High" }) _resolution.Items.Add(item);
        quality.Children.Add(_resolution);
        settingRow.Children.Add(quality);
        var geometry = new StackPanel();
        geometry.Children.Add(RenderUi.FieldLabel("Geometry"));
        _fidelity = RenderUi.ComboBox(0);
        foreach (string item in new[] { "Strict — preserve model geometry", "Balanced — improve setting", "Creative — reinterpret freely" }) _fidelity.Items.Add(item);
        geometry.Children.Add(_fidelity);
        Grid.SetColumn(geometry, 2);
        settingRow.Children.Add(geometry);
        controls.Children.Add(settingRow);
        controls.Children.Add(RenderUi.FieldLabel("Render brief"));
        _prompt = new TextBox { Height = 88, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10), Margin = new Thickness(0, 5, 0, 14), Background = Brushes.White, Foreground = RenderUi.PrimaryText, BorderBrush = RenderUi.Border, Text = "Natural materials, realistic daylight, refined landscaping, professional architectural visualization." };
        controls.Children.Add(_prompt);
        controls.Children.Add(HeaderRow("REFERENCE IMAGES", "+ Add", AddReferences));
        controls.Children.Add(new Border { Height = 78, Background = Brushes.White, BorderBrush = RenderUi.Border, BorderThickness = new Thickness(1), Padding = new Thickness(5), Child = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _referenceGrid } });
        _referenceStatus.Text = "Optional material, lighting, landscape, or mood references.";
        _referenceStatus.Foreground = RenderUi.MutedText;
        _referenceStatus.FontSize = 10;
        _referenceStatus.Margin = new Thickness(0, 5, 0, 12);
        controls.Children.Add(_referenceStatus);
        controls.Children.Add(RenderUi.SectionLabel("EDIT NOTES & AREAS"));
        _commentList.Children.Add(new TextBlock { Text = "Add notes on the image and create multiple replace or remove areas.", Foreground = RenderUi.MutedText, TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        controls.Children.Add(new Border { MinHeight = 48, MaxHeight = 104, Background = RenderUi.CanvasBackground, CornerRadius = new CornerRadius(6), Padding = new Thickness(9), Margin = new Thickness(0, 0, 0, 14), Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _commentList } });
        _renderButton = RenderUi.Button("Generate render", true, 156, new Thickness(0), true);
        _renderButton.HorizontalAlignment = HorizontalAlignment.Left;
        _renderButton.Click += async (_, _) => await RenderAsync();
        controls.Children.Add(_renderButton);
        content.Children.Add(new Border { Background = Brushes.White, BorderBrush = RenderUi.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(20), Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = controls } });

        var viewer = new DockPanel();
        var viewerHeader = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        viewerHeader.ColumnDefinitions.Add(new ColumnDefinition());
        viewerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _viewportLabel.Text = "Viewport source";
        _viewportLabel.FontSize = 16;
        _viewportLabel.FontWeight = FontWeights.SemiBold;
        viewerHeader.Children.Add(_viewportLabel);
        var sync = RenderUi.Button("Sync Revit viewport", false, 148, new Thickness(0));
        sync.ToolTip = "Move, orbit, or zoom in Revit, then capture the active view again.";
        sync.Click += (_, _) => RequestCapture();
        Grid.SetColumn(sync, 1);
        viewerHeader.Children.Add(sync);
        DockPanel.SetDock(viewerHeader, Dock.Top);
        viewer.Children.Add(viewerHeader);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        toolbar.Children.Add(ToolButton("Comment image", CanvasTool.Comment));
        toolbar.Children.Add(ToolButton("Replace area", CanvasTool.Replace));
        toolbar.Children.Add(ToolButton("Remove area", CanvasTool.Remove));
        toolbar.Children.Add(ActionButton("Clear edits", () => ClearEdits(true)));
        DockPanel.SetDock(toolbar, Dock.Top);
        viewer.Children.Add(toolbar);
        _status.Text = "Preparing viewport…";
        _status.Foreground = RenderUi.MutedText;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        _progress.Height = 5;
        _progress.Visibility = WpfVisibility.Collapsed;
        _progress.IsIndeterminate = true;
        _progress.Foreground = RenderUi.AccentBrush;
        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(_progress);
        footer.Children.Add(_status);
        _resultActions.Orientation = Orientation.Horizontal;
        _resultActions.HorizontalAlignment = HorizontalAlignment.Right;
        _resultActions.Visibility = WpfVisibility.Collapsed;
        footer.Children.Add(_resultActions);
        DockPanel.SetDock(footer, Dock.Bottom);
        viewer.Children.Add(footer);
        var imageSurface = new Grid { Background = RenderUi.CanvasBackground, ClipToBounds = true };
        _viewport.Stretch = Stretch.Uniform;
        imageSurface.Children.Add(_viewport);
        imageSurface.Children.Add(_overlay);
        _overlay.MouseLeftButtonDown += OverlayMouseDown;
        _overlay.MouseMove += OverlayMouseMove;
        _overlay.MouseLeftButtonUp += OverlayMouseUp;
        _overlay.SizeChanged += (_, _) => DrawOverlay();
        viewer.Children.Add(new Border { Background = RenderUi.CanvasBackground, BorderBrush = RenderUi.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(8), Child = imageSurface });
        Grid.SetColumn(viewer, 2);
        content.Children.Add(viewer);
        root.Children.Add(content);
        Content = root;
        SourceInitialized += (_, _) => new WindowInteropHelper(this).Owner = Process.GetCurrentProcess().MainWindowHandle;
        _galleryButton = BuildResultActions();
        Loaded += (_, _) =>
        {
            _bridge.Attach(this);
            if (_sourcePath is not null && File.Exists(_sourcePath)) { ShowImage(_sourcePath, "Gallery image · ready to edit"); SetReady("Adjust the brief, add edit notes, or select an area to change."); }
            else RequestCapture();
        };
        Activated += (_, _) => _bridge.Attach(this);
        Closed += (_, _) => _cancellation?.Cancel();
    }

    public string Prompt => _prompt.Text.Trim();
    public string RenderStyle => _style.SelectedItem?.ToString() ?? "Photorealistic architectural";
    public string RenderArea => _area.SelectedItem?.ToString() ?? "Visible viewport (current zoom)";
    public string Fidelity => _fidelity.SelectedItem?.ToString() ?? "Strict — preserve model geometry";
    public int PixelSize => _resolution.SelectedIndex switch { 0 => 1024, 2 => 4096, _ => 2048 };
    public IReadOnlyList<string> ReferenceImagePaths => _references;
    public string? MaskPath => _maskPath;
    public string EditGuidance
    {
        get
        {
            var lines = _comments.Select(comment => $"Note {comment.Number} at image position {PositionName(comment.ImageX, comment.ImageY)} ({comment.ImageX:P0} from left, {comment.ImageY:P0} from top): {comment.Text}").ToList();
            lines.AddRange(_areaEdits.Select(area => area.Kind == CanvasTool.Replace
                ? $"Inside selected area {area.Number} only, replace the current content with: {area.Instruction}. Blend lighting, perspective, materials, and edges naturally with the surrounding image."
                : $"Inside selected area {area.Number} only: {area.Instruction}"));
            return string.Join(" ", lines);
        }
    }

    private Grid HeaderRow(string title, string actionText, Action action)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(RenderUi.SectionLabel(title));
        var button = RenderUi.Button(actionText, false, 70, new Thickness(0, 0, 0, 6)); button.Click += (_, _) => action(); Grid.SetColumn(button, 1); row.Children.Add(button); return row;
    }
    private Button ToolButton(string text, CanvasTool tool) { var button = RenderUi.Button(text, false, text.Length > 12 ? 116 : 100, new Thickness(0, 0, 8, 0)); button.Click += (_, _) => ActivateTool(tool); return button; }
    private Button ActionButton(string text, Action action) { var button = RenderUi.Button(text, false, text.Length > 12 ? 126 : 96, new Thickness(0, 0, 8, 0)); button.Click += (_, _) => action(); return button; }
    private void ActivateTool(CanvasTool tool)
    {
        if (_viewport.Source is null) { SetWarning("Capture or open an image before using edit tools."); return; }
        _tool = tool; _overlay.Cursor = tool == CanvasTool.Comment ? Cursors.Cross : Cursors.Pen;
        SetReady(tool == CanvasTool.Comment ? "Click the image where you want to add an edit note." : "Drag a rectangle over the area you want to change.");
    }
    private void RequestCapture() { SetBusy("Waiting for Revit to capture the active viewport…"); _bridge.Attach(this); _bridge.Capture(); }
    internal void OnCaptureCompleted(string path, string viewName)
    {
        _sourcePath = path; _currentViewName = viewName; _resultPath = null; _resultSaved = false;
        _galleryButton.Content = "Save to Gallery"; _galleryButton.IsEnabled = true; _resultActions.Visibility = WpfVisibility.Collapsed;
        ClearEdits(false); ShowImage(path, $"Synced Revit viewport · {viewName}"); SetReady("Viewport synced. Continue working in Revit and sync again whenever the view changes.");
    }
    internal void OnRevitActionCancelled() => SetReady("Revit selection cancelled.");
    internal void OnRevitActionFailed(Exception exception) => ShowInlineError("Revit action failed", exception);

    private async Task RenderAsync()
    {
        string jobId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..22];
        try
        {
            if (_sourcePath is null || !File.Exists(_sourcePath)) throw new FileNotFoundException("Capture a Revit viewport or choose a gallery image first.");
            _cancellation = new CancellationTokenSource();
            _maskPath = null;
            SetBusy(_areaEdits.Count == 0 ? "Creating your render…" : $"Applying {_areaEdits.Count} selected-area edit{(_areaEdits.Count == 1 ? string.Empty : "s")}…");
            AiRenderActiveViewCommand.WriteRenderRequest(_sourcePath, this);
            RenderLog.Info(jobId, $"Render started | View={_currentViewName} | Quality={PixelSize} | Area={RenderArea} | References={_references.Count} | Mask areas={_areaEdits.Count} | Comments={_comments.Count}");
            string inputPath = _sourcePath;
            if (_areaEdits.Count == 0)
            {
                _resultPath = await OpenAiRenderService.RenderAsync(inputPath, this, _cancellation.Token);
            }
            else
            {
                for (int index = 0; index < _areaEdits.Count; index++)
                {
                    StudioAreaEdit area = _areaEdits[index];
                    _maskPath = CreateMask(inputPath, new[] { area });
                    _status.Text = $"Applying {area.Kind.ToString().ToLowerInvariant()} area {index + 1} of {_areaEdits.Count}…";
                    string candidatePath = await OpenAiRenderService.RenderAsync(inputPath, new AreaEditRequest(this, _maskPath, area), _cancellation.Token);
                    if (area.Kind == CanvasTool.Remove && ImageEditSafety.IsMostlyBlack(candidatePath, area.Selection) && !ImageEditSafety.IsMostlyBlack(inputPath, area.Selection))
                    {
                        RenderLog.Info(jobId, $"Removal area {index + 1} returned a black fill; restored its source pixels instead.");
                        candidatePath = ImageEditSafety.RestoreSourceArea(inputPath, candidatePath, area.Selection);
                        _status.Text = $"Removal area {index + 1} could not be reconstructed, so its source pixels were kept instead of a black fill.";
                    }
                    inputPath = candidatePath;
                }
                _resultPath = inputPath;
            }
            _resultSaved = false; _galleryButton.Content = "Save to Gallery"; _galleryButton.IsEnabled = true;
            RenderLogLog(jobId);
            _sourcePath = _resultPath;
            ClearEdits(false);
            ShowImage(_resultPath, "Rendered result · ready to edit");
            SetWarning("Render complete and ready for another edit. This version is not kept in Gallery until you save it.");
            _resultActions.Visibility = WpfVisibility.Visible;
        }
        catch (OperationCanceledException) { RenderLog.Info(jobId, "Render cancelled by user"); SetReady("Render cancelled. Your source image is unchanged."); }
        catch (Exception exception) { RenderLog.Error(jobId, exception); ShowInlineError("Render failed", exception); }
        finally { _cancellation?.Dispose(); _cancellation = null; }
    }
    private void RenderLogLog(string jobId) => RenderLog.Info(jobId, $"Render completed | Output={_resultPath}");

    private sealed class AreaEditRequest : IRenderRequest, IMaskedRenderRequest
    {
        private readonly RenderStudioWindow _studio;
        private readonly StudioAreaEdit _area;
        public AreaEditRequest(RenderStudioWindow studio, string maskPath, StudioAreaEdit area) { _studio = studio; MaskPath = maskPath; _area = area; }
        public string Prompt => _studio.Prompt;
        public string RenderStyle => _studio.RenderStyle;
        public string RenderArea => _studio.RenderArea;
        public string Fidelity => _studio.Fidelity;
        public int PixelSize => _studio.PixelSize;
        public IReadOnlyList<string> ReferenceImagePaths => _studio.ReferenceImagePaths;
        public string? MaskPath { get; }
        public string EditGuidance => string.Join(" ", _studio._comments.Select(comment => $"Note {comment.Number} at image position {PositionName(comment.ImageX, comment.ImageY)}: {comment.Text}")) + " " +
            (_area.Kind == CanvasTool.Replace
                ? $"Inside the selected mask only, replace the current content with: {_area.Instruction}. Blend lighting, perspective, materials, and edges naturally with the surrounding image."
                : $"Inside the selected mask only: {_area.Instruction}");
    }

    private void OverlayMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (_tool == CanvasTool.None || _viewport.Source is not BitmapSource) return;
        Point point = args.GetPosition(_overlay); if (!ImageBounds().Contains(point)) return;
        if (_tool == CanvasTool.Comment)
        {
            string? text = StudioTextPrompt.Ask(this, "Add edit note", "What should change at this point?", "Example: replace this material with warm limestone");
            if (!string.IsNullOrWhiteSpace(text)) { Rect bounds = ImageBounds(); _comments.Add(new StudioComment(_comments.Count + 1, text, (point.X - bounds.Left) / bounds.Width, (point.Y - bounds.Top) / bounds.Height)); RefreshComments(); DrawOverlay(); }
            _tool = CanvasTool.None; _overlay.Cursor = Cursors.Arrow; return;
        }
        _dragStart = point; _dragPreview = new Rect(point, point); _overlay.CaptureMouse();
    }
    private void OverlayMouseMove(object sender, MouseEventArgs args)
    {
        if (!_overlay.IsMouseCaptured || _tool is not (CanvasTool.Replace or CanvasTool.Remove)) return;
        Rect bounds = ImageBounds(); Point end = args.GetPosition(_overlay);
        end = new Point(Math.Clamp(end.X, bounds.Left, bounds.Right), Math.Clamp(end.Y, bounds.Top, bounds.Bottom));
        _dragPreview = new Rect(_dragStart, end); DrawOverlay();
    }
    private void OverlayMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (_tool is not (CanvasTool.Replace or CanvasTool.Remove) || !_overlay.IsMouseCaptured) return;
        _overlay.ReleaseMouseCapture(); Rect bounds = ImageBounds(); Point end = args.GetPosition(_overlay);
        end = new Point(Math.Clamp(end.X, bounds.Left, bounds.Right), Math.Clamp(end.Y, bounds.Top, bounds.Bottom));
        var displayRect = new Rect(_dragStart, end); _dragPreview = null; if (displayRect.Width < 8 || displayRect.Height < 8) { _tool = CanvasTool.None; DrawOverlay(); return; }
        Rect selection = new((displayRect.Left - bounds.Left) / bounds.Width, (displayRect.Top - bounds.Top) / bounds.Height, displayRect.Width / bounds.Width, displayRect.Height / bounds.Height);
        if (_tool == CanvasTool.Replace)
        {
            string? replacement = StudioTextPrompt.Ask(this, "Replace selected area", "What should appear inside this area?", "Example: replace with a mature olive tree");
            if (!string.IsNullOrWhiteSpace(replacement)) _areaEdits.Add(new StudioAreaEdit(_areaEdits.Count + 1, selection, CanvasTool.Replace, replacement));
        }
        else _areaEdits.Add(new StudioAreaEdit(_areaEdits.Count + 1, selection, CanvasTool.Remove, "Remove the visible objects and reconstruct the natural background, surfaces, lighting, and architectural context behind them."));
        _tool = CanvasTool.None; _overlay.Cursor = Cursors.Arrow; DrawOverlay();
        RefreshComments();
        if (_areaEdits.Count > 0) SetReady($"{_areaEdits.Count} selected-area edit{(_areaEdits.Count == 1 ? string.Empty : "s")} ready. Add more areas or generate the render.");
    }
    private Rect ImageBounds()
    {
        if (_viewport.Source is not BitmapSource bitmap || _overlay.ActualWidth <= 0 || _overlay.ActualHeight <= 0) return Rect.Empty;
        double scale = Math.Min(_overlay.ActualWidth / bitmap.PixelWidth, _overlay.ActualHeight / bitmap.PixelHeight);
        double width = bitmap.PixelWidth * scale, height = bitmap.PixelHeight * scale;
        return new Rect((_overlay.ActualWidth - width) / 2, (_overlay.ActualHeight - height) / 2, width, height);
    }
    private void DrawOverlay()
    {
        _overlay.Children.Clear(); Rect bounds = ImageBounds(); if (bounds.IsEmpty) return;
        if (_dragPreview is Rect preview)
        {
            var draft = new Rectangle { Width = preview.Width, Height = preview.Height, Fill = new SolidColorBrush(Color.FromArgb(35, 37, 99, 235)), Stroke = RenderUi.AccentBrush, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 } };
            Canvas.SetLeft(draft, preview.Left); Canvas.SetTop(draft, preview.Top); _overlay.Children.Add(draft);
        }
        foreach (StudioAreaEdit area in _areaEdits)
        {
            Brush stroke = area.Kind == CanvasTool.Remove ? RenderUi.ErrorBrush : RenderUi.AccentBrush;
            var rectangle = new Rectangle { Width = area.Selection.Width * bounds.Width, Height = area.Selection.Height * bounds.Height, Fill = new SolidColorBrush(area.Kind == CanvasTool.Remove ? Color.FromArgb(45, 220, 38, 38) : Color.FromArgb(55, 37, 99, 235)), Stroke = stroke, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 5, 3 } };
            double left = bounds.Left + area.Selection.Left * bounds.Width, top = bounds.Top + area.Selection.Top * bounds.Height;
            Canvas.SetLeft(rectangle, left); Canvas.SetTop(rectangle, top); _overlay.Children.Add(rectangle);
            var label = new Border { Background = stroke, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 2, 5, 2), Child = new TextBlock { Text = area.Kind == CanvasTool.Remove ? $"Remove {area.Number}" : $"Replace {area.Number}", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.SemiBold } };
            Canvas.SetLeft(label, left + 3); Canvas.SetTop(label, top + 3); _overlay.Children.Add(label);
        }
        foreach (StudioComment comment in _comments)
        {
            var marker = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = RenderUi.AccentBrush, BorderBrush = Brushes.White, BorderThickness = new Thickness(2), Child = new TextBlock { Text = comment.Number.ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, ToolTip = comment.Text };
            Canvas.SetLeft(marker, bounds.Left + comment.ImageX * bounds.Width - 14); Canvas.SetTop(marker, bounds.Top + comment.ImageY * bounds.Height - 14); _overlay.Children.Add(marker);
        }
    }
    private string CreateMask(string sourcePath, IReadOnlyList<StudioAreaEdit> areas)
    {
        BitmapSource source = LoadBitmap(sourcePath); int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4; byte[] pixels = new byte[stride * height];
        for (int index = 0; index < pixels.Length; index += 4) { pixels[index] = 255; pixels[index + 1] = 255; pixels[index + 2] = 255; pixels[index + 3] = 255; }
        foreach (StudioAreaEdit area in areas)
        {
            Rect selection = area.Selection;
            int left = Math.Clamp((int)Math.Floor(selection.Left * width), 0, width - 1), top = Math.Clamp((int)Math.Floor(selection.Top * height), 0, height - 1);
            int right = Math.Clamp((int)Math.Ceiling(selection.Right * width), left + 1, width), bottom = Math.Clamp((int)Math.Ceiling(selection.Bottom * height), top + 1, height);
            for (int y = top; y < bottom; y++) for (int x = left; x < right; x++) pixels[y * stride + x * 4 + 3] = 0;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        string path = Path.Combine(Path.GetDirectoryName(sourcePath)!, $"edit-mask-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using FileStream stream = File.Create(path); encoder.Save(stream); return path;
    }

    private void RefreshComments()
    {
        _commentList.Children.Clear();
        if (_comments.Count == 0 && _areaEdits.Count == 0) { _commentList.Children.Add(new TextBlock { Text = "Add notes on the image and create multiple replace or remove areas.", Foreground = RenderUi.MutedText, TextWrapping = TextWrapping.Wrap, FontSize = 11 }); return; }
        foreach (StudioComment comment in _comments.ToArray())
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 4) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = comment.Number.ToString(), Foreground = RenderUi.AccentBrush, FontWeight = FontWeights.Bold });
            var text = new TextBlock { Text = comment.Text, TextWrapping = TextWrapping.Wrap, FontSize = 11 }; Grid.SetColumn(text, 1); row.Children.Add(text);
            var edit = new Button { Content = "Edit", Height = 22, Padding = new Thickness(5, 0, 5, 0), Background = Brushes.Transparent, BorderBrush = RenderUi.Border, Foreground = RenderUi.PrimaryText, Margin = new Thickness(4, 0, 2, 0) };
            edit.Click += (_, _) => EditComment(comment); Grid.SetColumn(edit, 2); row.Children.Add(edit);
            var remove = new Button { Content = "×", Width = 22, Height = 22, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = RenderUi.MutedText };
            remove.Click += (_, _) => { _comments.Remove(comment); RenumberComments(); RefreshComments(); DrawOverlay(); }; Grid.SetColumn(remove, 3); row.Children.Add(remove); _commentList.Children.Add(row);
        }
        foreach (StudioAreaEdit area in _areaEdits.ToArray())
        {
            var row = new Grid { Margin = new Thickness(3, 4, 0, 2) }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = area.Kind == CanvasTool.Remove ? $"Remove {area.Number}" : $"Replace {area.Number}", Foreground = area.Kind == CanvasTool.Remove ? RenderUi.ErrorBrush : RenderUi.AccentBrush, FontWeight = FontWeights.Bold, FontSize = 10, VerticalAlignment = VerticalAlignment.Top });
            var text = new TextBlock { Text = area.Instruction, TextWrapping = TextWrapping.Wrap, FontSize = 11 }; Grid.SetColumn(text, 1); row.Children.Add(text);
            var edit = new Button { Content = "Edit", Height = 22, Padding = new Thickness(5, 0, 5, 0), Background = Brushes.Transparent, BorderBrush = RenderUi.Border, Foreground = RenderUi.PrimaryText, Margin = new Thickness(4, 0, 2, 0) };
            edit.Click += (_, _) => EditArea(area); Grid.SetColumn(edit, 2); row.Children.Add(edit);
            var remove = new Button { Content = "×", Width = 22, Height = 22, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = RenderUi.MutedText };
            remove.Click += (_, _) => { _areaEdits.Remove(area); RenumberAreas(); RefreshComments(); DrawOverlay(); }; Grid.SetColumn(remove, 3); row.Children.Add(remove); _commentList.Children.Add(row);
        }
    }
    private void EditComment(StudioComment comment)
    {
        string? text = StudioTextPrompt.Ask(this, "Edit image note", "Update this note for the render.", "", comment.Text);
        if (string.IsNullOrWhiteSpace(text)) return;
        int index = _comments.IndexOf(comment); if (index >= 0) _comments[index] = comment with { Text = text };
        RefreshComments(); DrawOverlay();
    }
    private void EditArea(StudioAreaEdit area)
    {
        string title = area.Kind == CanvasTool.Replace ? "Edit replacement" : "Edit removal instruction";
        string prompt = area.Kind == CanvasTool.Replace ? "What should appear in this selected area?" : "Describe how this selected area should be removed or rebuilt.";
        string? text = StudioTextPrompt.Ask(this, title, prompt, "", area.Instruction);
        if (string.IsNullOrWhiteSpace(text)) return;
        int index = _areaEdits.IndexOf(area); if (index >= 0) _areaEdits[index] = area with { Instruction = text };
        RefreshComments(); DrawOverlay();
    }
    private void RenumberComments() { for (int index = 0; index < _comments.Count; index++) _comments[index] = _comments[index] with { Number = index + 1 }; }
    private void RenumberAreas() { for (int index = 0; index < _areaEdits.Count; index++) _areaEdits[index] = _areaEdits[index] with { Number = index + 1 }; }
    private void ClearEdits(bool notify) { _comments.Clear(); _areaEdits.Clear(); _dragPreview = null; _maskPath = null; RefreshComments(); DrawOverlay(); if (notify) SetReady("Edit notes and area selections cleared."); }
    private void AddReferences()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose visual references", Filter = "Supported images|*.png;*.jpg;*.jpeg;*.webp", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        foreach (string path in dialog.FileNames) { if (_references.Count >= 8) break; if (!_references.Contains(path, StringComparer.OrdinalIgnoreCase) && new FileInfo(path).Length <= 50L * 1024 * 1024) _references.Add(path); }
        RefreshReferences();
    }
    private void RefreshReferences()
    {
        _referenceGrid.Children.Clear();
        foreach (string path in _references.ToArray())
        {
            var card = new Grid { Width = 82, Height = 60, Margin = new Thickness(0, 0, 6, 0), ToolTip = Path.GetFileName(path) }; card.Children.Add(new Image { Source = LoadThumbnail(path), Stretch = Stretch.UniformToFill });
            var remove = new Button { Content = "×", Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(0), Background = Brushes.White, BorderBrush = RenderUi.Border };
            remove.Click += (_, _) => { _references.Remove(path); RefreshReferences(); }; card.Children.Add(remove); _referenceGrid.Children.Add(card);
        }
        _referenceStatus.Text = _references.Count == 0 ? "Optional material, lighting, landscape, or mood references." : $"{_references.Count} reference image{(_references.Count == 1 ? string.Empty : "s")} ready.";
    }
    private Button BuildResultActions()
    {
        var edit = RenderUi.Button("Continue editing", false, 126, new Thickness(0, 10, 0, 0)); edit.Click += (_, _) => EditCurrentResult();
        var gallery = RenderUi.Button("Save to Gallery", true, 128, new Thickness(8, 10, 0, 0)); gallery.Click += (_, _) => SaveToGallery(gallery);
        var copy = RenderUi.Button("Copy", false, 70, new Thickness(8, 10, 0, 0)); copy.Click += (_, _) => { if (_viewport.Source is BitmapSource bitmap) Clipboard.SetImage(bitmap); };
        var saveAs = RenderUi.Button("Save as…", false, 88, new Thickness(8, 10, 0, 0)); saveAs.Click += (_, _) => SaveAs();
        _resultActions.Children.Add(edit); _resultActions.Children.Add(gallery); _resultActions.Children.Add(copy); _resultActions.Children.Add(saveAs); return gallery;
    }
    private void EditCurrentResult()
    {
        if (_resultPath is null || !File.Exists(_resultPath)) return; _sourcePath = _resultPath; ClearEdits(false); _renderButton.Content = "Generate edit"; ShowImage(_sourcePath, "Current result · editing source");
        SetWarning(_resultSaved ? "This result is saved. Add notes or select an area for the next edit." : "Not saved to Gallery: you can edit this result now, but this version will not be kept in Gallery unless you save it first.");
    }
    private void SaveToGallery(Button button)
    {
        if (_resultPath is null) return; RenderGalleryStorage.SaveRender(_resultPath, _galleryProject); _resultSaved = true; button.Content = "Saved"; button.IsEnabled = false; _status.Foreground = RenderUi.SuccessBrush; _status.Text = $"Saved to the {_galleryProject.DisplayName} gallery.";
    }
    private void SaveAs()
    {
        if (_resultPath is null) return; var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save rendered image", FileName = Path.GetFileName(_resultPath), DefaultExt = ".png", Filter = "PNG image|*.png" };
        if (dialog.ShowDialog() == true && !string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(_resultPath), StringComparison.OrdinalIgnoreCase)) File.Copy(_resultPath, dialog.FileName, true);
    }
    private void SetBusy(string message) { _renderButton.IsEnabled = false; _progress.Visibility = WpfVisibility.Visible; _status.Foreground = RenderUi.MutedText; _status.Text = message; }
    private void SetReady(string message) { _renderButton.IsEnabled = true; _progress.Visibility = WpfVisibility.Collapsed; _status.Foreground = RenderUi.MutedText; _status.Text = message; }
    private void SetWarning(string message) { _renderButton.IsEnabled = true; _progress.Visibility = WpfVisibility.Collapsed; _status.Foreground = RenderUi.WarningBrush; _status.Text = message; }
    private void ShowInlineError(string heading, Exception exception) { _renderButton.IsEnabled = true; _progress.Visibility = WpfVisibility.Collapsed; _status.Foreground = RenderUi.ErrorBrush; _status.Text = $"{heading}: {exception.Message}"; }
    private void ShowImage(string path, string label) { _viewport.Source = LoadBitmap(path); _viewportLabel.Text = label; DrawOverlay(); }
    private static BitmapImage LoadBitmap(string path) { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze(); return bitmap; }
    private static BitmapImage LoadThumbnail(string path) { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.UriSource = new Uri(path); bitmap.DecodePixelWidth = 164; bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze(); return bitmap; }
    private static string PositionName(double x, double y) { string vertical = y < .34 ? "top" : y > .66 ? "bottom" : "middle"; string horizontal = x < .34 ? "left" : x > .66 ? "right" : "center"; return $"{vertical}-{horizontal}"; }
}

internal static class ImageEditSafety
{
    public static bool IsMostlyBlack(string imagePath, Rect normalizedArea)
    {
        BitmapSource bitmap = LoadBgra(imagePath);
        byte[] pixels = CopyPixels(bitmap, out int stride);
        GetBounds(bitmap, normalizedArea, out int left, out int top, out int right, out int bottom);
        int step = Math.Max(1, Math.Min(right - left, bottom - top) / 180);
        int total = 0, dark = 0;
        for (int y = top; y < bottom; y += step)
            for (int x = left; x < right; x += step)
            {
                int offset = y * stride + x * 4;
                if (pixels[offset] < 16 && pixels[offset + 1] < 16 && pixels[offset + 2] < 16) dark++;
                total++;
            }
        return total > 0 && dark / (double)total >= .88;
    }

    public static string RestoreSourceArea(string sourcePath, string resultPath, Rect normalizedArea)
    {
        BitmapSource result = LoadBgra(resultPath);
        BitmapSource source = LoadBgra(sourcePath);
        if (source.PixelWidth != result.PixelWidth || source.PixelHeight != result.PixelHeight)
            source = ScaleTo(source, result.PixelWidth, result.PixelHeight);
        byte[] resultPixels = CopyPixels(result, out int resultStride);
        byte[] sourcePixels = CopyPixels(source, out int sourceStride);
        GetBounds(result, normalizedArea, out int left, out int top, out int right, out int bottom);
        for (int y = top; y < bottom; y++)
            Buffer.BlockCopy(sourcePixels, y * sourceStride + left * 4, resultPixels, y * resultStride + left * 4, (right - left) * 4);
        var restored = BitmapSource.Create(result.PixelWidth, result.PixelHeight, 96, 96, PixelFormats.Bgra32, null, resultPixels, resultStride);
        string restoredPath = Path.Combine(Path.GetDirectoryName(resultPath)!, $"guarded-edit-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(restored));
        using FileStream stream = File.Create(restoredPath);
        encoder.Save(stream);
        return restoredPath;
    }

    private static BitmapSource LoadBgra(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static BitmapSource ScaleTo(BitmapSource source, int width, int height)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen()) context.DrawImage(source, new Rect(0, 0, width, height));
        var scaled = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        scaled.Render(visual);
        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static byte[] CopyPixels(BitmapSource bitmap, out int stride)
    {
        stride = bitmap.PixelWidth * 4;
        byte[] pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void GetBounds(BitmapSource bitmap, Rect area, out int left, out int top, out int right, out int bottom)
    {
        left = Math.Clamp((int)Math.Floor(area.Left * bitmap.PixelWidth), 0, bitmap.PixelWidth - 1);
        top = Math.Clamp((int)Math.Floor(area.Top * bitmap.PixelHeight), 0, bitmap.PixelHeight - 1);
        right = Math.Clamp((int)Math.Ceiling(area.Right * bitmap.PixelWidth), left + 1, bitmap.PixelWidth);
        bottom = Math.Clamp((int)Math.Ceiling(area.Bottom * bitmap.PixelHeight), top + 1, bitmap.PixelHeight);
    }
}

internal static class StudioTextPrompt
{
    public static string? Ask(Window owner, string title, string instruction, string hint, string? initialText = null)
    {
        var window = new Window { Owner = owner, Title = title, Width = 460, Height = 245, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = RenderUi.WindowBackground, Foreground = RenderUi.PrimaryText };
        var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(new TextBlock { Text = instruction, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Height = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(10), Margin = new Thickness(0, 14, 0, 14), Background = Brushes.White, BorderBrush = RenderUi.Border, ToolTip = hint, Text = initialText ?? string.Empty }; panel.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = RenderUi.Button("Cancel", false, 82, new Thickness(0, 0, 8, 0)); cancel.Click += (_, _) => window.DialogResult = false;
        var add = RenderUi.Button("Add", true, 82, new Thickness(0), true); add.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) window.DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(add); panel.Children.Add(actions); window.Content = panel; window.Loaded += (_, _) => input.Focus();
        return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
