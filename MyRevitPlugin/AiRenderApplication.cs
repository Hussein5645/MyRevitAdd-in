using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MyRevitPlugin;

public sealed class AiRenderApplication : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        const string tabName = "AI Render";
        try
        {
            application.CreateRibbonTab(tabName);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // Revit keeps custom tabs for the current session; reuse it when present.
        }

        RibbonPanel panel = application.CreateRibbonPanel(tabName, "Render");
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        var button = new PushButtonData(
            "AiRenderActiveView",
            "AI Render\nActive View",
            assemblyPath,
            typeof(AiRenderActiveViewCommand).FullName!);

        PushButton pushButton = (PushButton)panel.AddItem(button);
        pushButton.ToolTip = "Export the active Revit view and create an AI-render job.";
        pushButton.LongDescription = "Opens a live studio for structure-aware architectural rendering from the active Revit view.";

        var gallery = new PushButtonData(
            "OpenAiRenderGallery",
            "Saved Render\nGallery",
            assemblyPath,
            typeof(OpenRenderGalleryCommand).FullName!);
        ((PushButton)panel.AddItem(gallery)).ToolTip = "Browse images explicitly saved from AI Render previews.";

        var settings = new PushButtonData(
            "OpenAiRenderSettings",
            "AI Render\nSettings",
            assemblyPath,
            typeof(OpenRenderSettingsCommand).FullName!);
        ((PushButton)panel.AddItem(settings)).ToolTip = "Configure the rendering service connection.";
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
}

[Transaction(TransactionMode.Manual)]
public sealed class AiRenderActiveViewCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument? uiDocument = commandData.Application.ActiveUIDocument;
        if (uiDocument is null)
        {
            TaskDialog.Show("AI Render", "Open a Revit project and activate a printable view first.");
            return Result.Cancelled;
        }

        View activeView = uiDocument.ActiveView;
        if (!activeView.CanBePrinted)
        {
            TaskDialog.Show("AI Render", "The active view cannot be exported. Open a 3D, plan, section, elevation, or sheet view.");
            return Result.Cancelled;
        }

        try
        {
            new RenderStudioWindow(uiDocument, activeView).Show();
            return Result.Succeeded;
        }
        catch (Exception exception)
        {
            string jobId = $"launch-{DateTime.Now:yyyyMMdd-HHmmss}";
            RenderLog.Error(jobId, exception);
            message = exception.Message;
            new RenderErrorWindow("We couldn’t create this render", "The view or render service returned an error. Your Revit model was not changed.", exception, jobId).ShowDialog();
            return Result.Failed;
        }
    }

    internal static string ExportActiveView(UIDocument uiDocument, View activeView, IRenderRequest request)
    {
        string jobDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "MyRevitPlugin", "RenderJobs", $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}"[..28]);
        Directory.CreateDirectory(jobDirectory);

        var options = new ImageExportOptions
        {
            ExportRange = ExportRange.CurrentView,
            FilePath = Path.Combine(jobDirectory, "source"),
            FitDirection = FitDirectionType.Horizontal,
            HLRandWFViewsFileType = ImageFileType.PNG,
            ImageResolution = ImageResolution.DPI_300,
            PixelSize = request.PixelSize,
            ShadowViewsFileType = ImageFileType.PNG,
            ZoomType = ZoomFitType.FitToPage
        };

        TransactionGroup? cropOverride = null;
        try
        {
            if (request.RenderArea == "Full printable view" && activeView.CropBoxActive)
            {
                cropOverride = new TransactionGroup(uiDocument.Document, "AI Render temporary full-view export");
                cropOverride.Start();
                using var transaction = new Transaction(uiDocument.Document, "Temporarily disable crop region");
                transaction.Start();
                activeView.CropBoxActive = false;
                transaction.Commit();
            }
            else if (request.RenderArea == "Visible viewport (current zoom)")
            {
                UIView? uiView = uiDocument.GetOpenUIViews().FirstOrDefault(view => view.ViewId == activeView.Id);
                IList<XYZ>? corners = uiView?.GetZoomCorners();
                if (corners is { Count: 2 })
                {
                    BoundingBoxXYZ originalCrop = activeView.CropBox;
                    Autodesk.Revit.DB.Transform inverse = originalCrop.Transform.Inverse;
                    XYZ first = inverse.OfPoint(corners[0]);
                    XYZ second = inverse.OfPoint(corners[1]);
                    var visibleCrop = new BoundingBoxXYZ
                    {
                        Transform = originalCrop.Transform,
                        Min = new XYZ(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y), originalCrop.Min.Z),
                        Max = new XYZ(Math.Max(first.X, second.X), Math.Max(first.Y, second.Y), originalCrop.Max.Z)
                    };
                    cropOverride = new TransactionGroup(uiDocument.Document, "AI Render temporary visible viewport export");
                    cropOverride.Start();
                    using var transaction = new Transaction(uiDocument.Document, "Temporarily match active zoom");
                    transaction.Start();
                    activeView.CropBoxActive = true;
                    activeView.CropBox = visibleCrop;
                    transaction.Commit();
                }
            }
        }
        catch (Exception)
        {
            cropOverride?.RollBack();
            cropOverride = null;
        }

        try
        {
            uiDocument.Document.ExportImage(options);
        }
        finally
        {
            cropOverride?.RollBack();
        }
        string? export = Directory.EnumerateFiles(jobDirectory, "*.png")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        string sourcePath = export ?? throw new InvalidOperationException("Revit finished exporting but no PNG file was created.");
        WriteViewContext(jobDirectory, uiDocument, activeView, request);
        return sourcePath;
    }

    private static void WriteViewContext(string jobDirectory, UIDocument uiDocument, View activeView, IRenderRequest request)
    {
        UIView? uiView = uiDocument.GetOpenUIViews().FirstOrDefault(view => view.ViewId == activeView.Id);
        string zoomBounds = "Unavailable (view is not open in a Revit viewport).";
        if (uiView is not null)
        {
            IList<XYZ> corners = uiView.GetZoomCorners();
            if (corners.Count == 2)
            {
                zoomBounds = $"Lower-left: {corners[0]}{Environment.NewLine}Upper-right: {corners[1]}";
            }
        }

        File.WriteAllText(Path.Combine(jobDirectory, "view-context.txt"),
            $"View: {activeView.Name}{Environment.NewLine}" +
            $"View type: {activeView.ViewType}{Environment.NewLine}" +
            $"Render area: {request.RenderArea}{Environment.NewLine}" +
            $"Export resolution: {request.PixelSize}px{Environment.NewLine}" +
            $"Fidelity: {request.Fidelity}{Environment.NewLine}" +
            $"Active Revit zoom bounds:{Environment.NewLine}{zoomBounds}");
    }

    internal static void WriteRenderRequest(string sourcePath, IRenderRequest request)
    {
        string editDetails = request is IMaskedRenderRequest edits
            ? $"{Environment.NewLine}Selection mask: {edits.MaskPath ?? "None"}{Environment.NewLine}Edit notes: {edits.EditGuidance}"
            : string.Empty;
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(sourcePath)!, "render-request.txt"),
            $"Style: {request.RenderStyle}{Environment.NewLine}" +
            $"Fidelity: {request.Fidelity}{Environment.NewLine}" +
            $"Render area: {request.RenderArea}{Environment.NewLine}" +
            $"Reference images: {request.ReferenceImagePaths.Count}{Environment.NewLine}" +
            $"Prompt: {request.Prompt}{editDetails}");
    }
}

internal sealed class RenderProgressWindow : Window
{
    private readonly TextBlock _stage;
    private readonly TextBlock _detail;
    private readonly ProgressBar _progress;
    private readonly CancellationTokenSource _cancellation = new();

    public RenderProgressWindow(string jobId)
    {
        Title = "AI Render — Working";
        Width = 520;
        Height = 285;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = RenderUi.WindowBackground;
        Foreground = RenderUi.PrimaryText;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(30, 26, 30, 24) };
        panel.Children.Add(new TextBlock { Text = "AI RENDER", Foreground = RenderUi.AccentBrush, FontSize = 11, FontWeight = FontWeights.Bold });
        _stage = new TextBlock { Text = "Starting…", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) };
        _detail = new TextBlock { Foreground = RenderUi.MutedText, TextWrapping = TextWrapping.Wrap };
        _progress = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 24, 0, 12), Foreground = RenderUi.AccentBrush, Background = RenderUi.InputBackground, BorderThickness = new Thickness(0) };
        panel.Children.Add(_stage);
        panel.Children.Add(_detail);
        panel.Children.Add(_progress);
        panel.Children.Add(new TextBlock { Text = $"Job {jobId}", Foreground = RenderUi.MutedText, FontSize = 11 });
        var cancel = RenderUi.Button("Cancel", false, 92, new Thickness(0, 14, 0, 0));
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        cancel.Click += (_, _) =>
        {
            cancel.IsEnabled = false;
            cancel.Content = "Cancelling…";
            _cancellation.Cancel();
        };
        panel.Children.Add(cancel);
        Content = panel;
    }

    public CancellationToken CancellationToken => _cancellation.Token;

    public void SetStage(string stage, string detail, double progress)
    {
        _stage.Text = stage;
        _detail.Text = detail;
        _progress.Value = progress;
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    public T WaitFor<T>(Task<T> task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(25);
        }
        return task.GetAwaiter().GetResult();
    }
}

internal sealed class RenderErrorWindow : Window
{
    public RenderErrorWindow(string title, string summary, Exception exception, string jobId)
    {
        Title = "AI Render — Error";
        Width = 560;
        Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = RenderUi.WindowBackground;
        Foreground = RenderUi.PrimaryText;

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        panel.Children.Add(new TextBlock { Text = "!", Width = 42, Height = 42, TextAlignment = TextAlignment.Center, FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Background = RenderUi.ErrorBrush });
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8) });
        panel.Children.Add(new TextBlock { Text = summary, Foreground = RenderUi.MutedText, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = exception.Message, Foreground = RenderUi.PrimaryText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 12) });
        var details = new Expander { Header = "Technical details", Foreground = RenderUi.MutedText, Content = new System.Windows.Controls.TextBox { Text = exception.ToString(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 100, Background = RenderUi.InputBackground, Foreground = RenderUi.MutedText, BorderBrush = RenderUi.Border } };
        panel.Children.Add(details);
        var close = RenderUi.Button("Close", true, 100, new Thickness(0, 16, 0, 0), true);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        panel.Children.Add(new TextBlock { Text = $"Job {jobId} · Details saved to the local AI Render log", Foreground = RenderUi.MutedText, FontSize = 10, Margin = new Thickness(0, 10, 0, 0) });
        Content = panel;
    }
}

internal static class RenderLog
{
    private static readonly string LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyRevitPlugin", "Logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "ai-render.log");

    public static void Info(string jobId, string message) => Write("INFO", jobId, message);
    public static void Error(string jobId, Exception exception) => Write("ERROR", jobId, exception.ToString());

    private static void Write(string level, string jobId, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string cleanMessage = message.Replace("\r", " ").Replace("\n", " | ");
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} [{level}] [{jobId}] {cleanMessage}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never interrupt a render or Revit session.
        }
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class OpenRenderGalleryCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument? uiDocument = commandData.Application.ActiveUIDocument;
        if (uiDocument is null)
        {
            TaskDialog.Show("AI Render", "Open a Revit project before opening its render gallery.");
            return Result.Cancelled;
        }
        new RenderGalleryWindow(uiDocument).Show();
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class OpenRenderSettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        new RenderSettingsWindow().ShowDialog();
        return Result.Succeeded;
    }
}

internal sealed class RenderSettingsWindow : Window
{
    public RenderSettingsWindow()
    {
        Title = "AI Render — Render Settings";
        Width = 560;
        Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = RenderUi.WindowBackground;
        Foreground = RenderUi.PrimaryText;

        bool connected = OpenAiCredentialStore.ReadKey() is not null;
        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        panel.Children.Add(new TextBlock { Text = "Rendering connection", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = connected ? "● Connected" : "● Not connected", Foreground = connected ? RenderUi.SuccessBrush : RenderUi.ErrorBrush, Margin = new Thickness(0, 8, 0, 20) });
        panel.Children.Add(RenderUi.FieldLabel("Rendering service key"));
        var key = new PasswordBox { Height = 38, Padding = new Thickness(10, 7, 10, 7), Background = RenderUi.InputBackground, Foreground = RenderUi.PrimaryText, BorderBrush = RenderUi.Border, BorderThickness = new Thickness(1) };
        panel.Children.Add(key);
        panel.Children.Add(new TextBlock { Text = connected ? "A key is already stored. Leave this blank to keep it, or paste a replacement." : "Paste your key. It will be saved in Windows Credential Manager, not in the project or logs.", Foreground = RenderUi.MutedText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 18) });
        var status = new TextBlock { Foreground = RenderUi.ErrorBrush, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = RenderUi.Button("Close", false, 90, new Thickness(0, 0, 10, 0));
        close.Click += (_, _) => Close();
        var save = RenderUi.Button("Save connection", true, 140, new Thickness(0), true);
        save.Click += (_, _) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(key.Password))
                {
                    if (connected) Close();
                    else status.Text = "Enter an API key before saving.";
                    return;
                }
                OpenAiCredentialStore.SaveKey(key.Password);
                key.Clear();
                DialogResult = true;
            }
            catch (Exception exception)
            {
                status.Text = exception.Message;
            }
        };
        buttons.Children.Add(close);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        Content = panel;
    }
}

internal sealed class RenderRequestWindow : Window, IRenderRequest
{
    private readonly System.Windows.Controls.TextBox _prompt;
    private readonly System.Windows.Controls.ComboBox _style;
    private readonly List<string> _referenceImages = new();
    private readonly WrapPanel _referencePanel = new();
    private readonly TextBlock _referenceStatus = new();

    public RenderRequestWindow(string viewName)
    {
        Title = "AI Render — Active View";
        Width = 640;
        Height = 790;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = RenderUi.WindowBackground;
        Foreground = RenderUi.PrimaryText;

        var root = new DockPanel();
        var header = new StackPanel { Margin = new Thickness(26, 20, 26, 18) };
        header.Children.Add(new TextBlock { Text = "Create an AI render", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = RenderUi.PrimaryText });
        header.Children.Add(new TextBlock { Text = $"ACTIVE VIEW  •  {viewName}", Foreground = RenderUi.MutedText, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        var headerContainer = new System.Windows.Controls.Border { Background = RenderUi.HeaderBackground, Child = header };
        DockPanel.SetDock(headerContainer, Dock.Top);
        root.Children.Add(headerContainer);

        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 24) };
        panel.Children.Add(RenderUi.SectionLabel("LOOK & COMPOSITION"));
        panel.Children.Add(RenderUi.FieldLabel("Render style"));
        _style = RenderUi.ComboBox(0);
        _style.Items.Add("Photorealistic architectural");
        _style.Items.Add("Warm golden-hour exterior");
        _style.Items.Add("Soft editorial interior");
        _style.Items.Add("Competition visualization");
        panel.Children.Add(_style);
        panel.Children.Add(RenderUi.FieldLabel("Render area"));
        var area = RenderUi.ComboBox(0);
        area.Items.Add("Current Revit crop region");
        area.Items.Add("Full printable view");
        panel.Children.Add(area);
        _renderArea = area;
        var settingsRow = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 16) };
        settingsRow.ColumnDefinitions.Add(new ColumnDefinition());
        settingsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        settingsRow.ColumnDefinitions.Add(new ColumnDefinition());
        var resolutionPanel = new StackPanel();
        resolutionPanel.Children.Add(RenderUi.FieldLabel("Output resolution"));
        var resolution = RenderUi.ComboBox(1, new Thickness(0, 5, 0, 0));
        resolution.Items.Add("1024 px — Draft");
        resolution.Items.Add("2048 px — Standard");
        resolution.Items.Add("4096 px — Final");
        resolutionPanel.Children.Add(resolution);
        System.Windows.Controls.Grid.SetColumn(resolutionPanel, 0);
        settingsRow.Children.Add(resolutionPanel);
        _resolution = resolution;
        var fidelityPanel = new StackPanel();
        fidelityPanel.Children.Add(RenderUi.FieldLabel("Model fidelity"));
        var fidelity = RenderUi.ComboBox(0, new Thickness(0, 5, 0, 0));
        fidelity.Items.Add("Strict — preserve model geometry");
        fidelity.Items.Add("Balanced — enhance materials and setting");
        fidelity.Items.Add("Creative — allow stronger visual reinterpretation");
        fidelityPanel.Children.Add(fidelity);
        System.Windows.Controls.Grid.SetColumn(fidelityPanel, 2);
        settingsRow.Children.Add(fidelityPanel);
        panel.Children.Add(settingsRow);
        _fidelity = fidelity;
        panel.Children.Add(RenderUi.SectionLabel("RENDER BRIEF"));
        panel.Children.Add(RenderUi.FieldLabel("Describe atmosphere, materials, lighting, and context"));
        _prompt = new System.Windows.Controls.TextBox { AcceptsReturn = true, Height = 80, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 18), Padding = new Thickness(10), Background = RenderUi.InputBackground, Foreground = RenderUi.PrimaryText, BorderBrush = RenderUi.Border, BorderThickness = new Thickness(1), Text = "Natural materials, realistic lighting, refined architectural visualization." };
        panel.Children.Add(_prompt);
        var referenceHeading = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 8) };
        referenceHeading.ColumnDefinitions.Add(new ColumnDefinition());
        referenceHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var referenceLabel = RenderUi.SectionLabel("REFERENCE IMAGES  ·  OPTIONAL");
        referenceLabel.VerticalAlignment = VerticalAlignment.Center;
        referenceHeading.Children.Add(referenceLabel);
        var addReferences = RenderUi.Button("+ Add references", false, 128, new Thickness(0));
        addReferences.Click += (_, _) => AddReferenceImages();
        System.Windows.Controls.Grid.SetColumn(addReferences, 1);
        referenceHeading.Children.Add(addReferences);
        panel.Children.Add(referenceHeading);
        _referencePanel.Orientation = Orientation.Horizontal;
        var referenceScroll = new ScrollViewer { Height = 102, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = RenderUi.CardBackground, BorderBrush = RenderUi.Border, BorderThickness = new Thickness(1), Padding = new Thickness(7), Content = _referencePanel };
        panel.Children.Add(referenceScroll);
        _referenceStatus.Text = "Add material, lighting, landscape, or mood references. The Revit view stays authoritative.";
        _referenceStatus.Foreground = RenderUi.MutedText;
        _referenceStatus.FontSize = 11;
        _referenceStatus.Margin = new Thickness(0, 7, 0, 16);
        panel.Children.Add(_referenceStatus);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
        var cancel = RenderUi.Button("Cancel", false, 96, new Thickness(0, 0, 10, 0));
        cancel.Click += (_, _) => DialogResult = false;
        var render = RenderUi.Button("Export & Render", true, 144, new Thickness(0), true);
        render.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(render);
        panel.Children.Add(buttons);
        root.Children.Add(panel);
        Content = root;
    }

    public string Prompt => _prompt.Text.Trim();
    public string RenderStyle => _style.SelectedItem?.ToString() ?? "Photorealistic architectural";
    public string RenderArea => _renderArea.SelectedItem?.ToString() ?? "Current Revit crop region";
    public string Fidelity => _fidelity.SelectedItem?.ToString() ?? "Strict — preserve model geometry";
    public int PixelSize => _resolution.SelectedIndex switch { 0 => 1024, 2 => 4096, _ => 2048 };
    public IReadOnlyList<string> ReferenceImagePaths => _referenceImages;

    private readonly System.Windows.Controls.ComboBox _renderArea;
    private readonly System.Windows.Controls.ComboBox _resolution;
    private readonly System.Windows.Controls.ComboBox _fidelity;

    private void AddReferenceImages()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose reference images",
            Filter = "Supported images|*.png;*.jpg;*.jpeg;*.webp|PNG|*.png|JPEG|*.jpg;*.jpeg|WebP|*.webp",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true)
            return;

        foreach (string path in dialog.FileNames)
        {
            if (_referenceImages.Count >= 8)
            {
                _referenceStatus.Text = "You can attach up to 8 reference images per render.";
                break;
            }
            if (_referenceImages.Contains(path, StringComparer.OrdinalIgnoreCase))
                continue;
            if (new FileInfo(path).Length > 50L * 1024 * 1024)
            {
                _referenceStatus.Text = $"Skipped {Path.GetFileName(path)} because it is larger than 50 MB.";
                continue;
            }
            _referenceImages.Add(path);
        }
        RefreshReferenceImages();
    }

    private void RefreshReferenceImages()
    {
        _referencePanel.Children.Clear();
        foreach (string path in _referenceImages.ToArray())
        {
            var card = new System.Windows.Controls.Grid { Width = 112, Height = 78, Margin = new Thickness(0, 0, 8, 0), ToolTip = Path.GetFileName(path) };
            card.Children.Add(new Image { Source = LoadReferenceThumbnail(path), Stretch = Stretch.UniformToFill });
            var remove = new Button { Content = "×", Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.White, Foreground = RenderUi.PrimaryText, BorderBrush = RenderUi.Border, Padding = new Thickness(0), FontWeight = FontWeights.Bold };
            remove.Click += (_, _) => { _referenceImages.Remove(path); RefreshReferenceImages(); };
            card.Children.Add(remove);
            _referencePanel.Children.Add(new System.Windows.Controls.Border { Background = RenderUi.CardBackground, BorderBrush = RenderUi.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Child = card });
        }
        _referenceStatus.Text = _referenceImages.Count == 0
            ? "Add material, lighting, landscape, or mood references. The Revit view stays authoritative."
            : $"{_referenceImages.Count} reference image{(_referenceImages.Count == 1 ? string.Empty : "s")} will be sent with the Revit view.";
    }

    private static BitmapImage LoadReferenceThumbnail(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path);
        bitmap.DecodePixelWidth = 224;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}

internal sealed class RenderPreviewWindow : Window
{
    public RenderPreviewWindow(string resultPath, RenderRequestWindow request)
    {
        Title = "AI Render — Result";
        Width = 900;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = RenderUi.WindowBackground;
        Foreground = RenderUi.PrimaryText;

        var panel = new DockPanel { Margin = new Thickness(24) };
        var title = new TextBlock { Text = "Render preview", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = RenderUi.PrimaryText, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);
        var footer = new TextBlock
        {
            Text = $"Style: {request.RenderStyle} | {request.PixelSize}px | {request.Fidelity}\nRender job: {resultPath}",
            TextWrapping = TextWrapping.Wrap,
            Foreground = RenderUi.MutedText,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var saveButton = RenderUi.Button("Save to Gallery", true, 142, new Thickness(0, 10, 0, 0));
        saveButton.Click += (_, _) => SaveToGallery(resultPath, saveButton);
        var copyButton = RenderUi.Button("Copy image", false, 108, new Thickness(8, 10, 0, 0));
        copyButton.Click += (_, _) => Clipboard.SetImage(LoadImage(resultPath));
        var saveAsButton = RenderUi.Button("Save as…", false, 100, new Thickness(8, 10, 0, 0));
        saveAsButton.Click += (_, _) => SaveAs(resultPath);
        var explorerButton = RenderUi.Button("Show in Explorer", false, 138, new Thickness(8, 10, 0, 0));
        explorerButton.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{resultPath}\"") { UseShellExecute = true });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(saveButton);
        actions.Children.Add(copyButton);
        actions.Children.Add(saveAsButton);
        actions.Children.Add(explorerButton);
        var footerPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        footerPanel.Children.Add(footer);
        footerPanel.Children.Add(actions);
        DockPanel.SetDock(footerPanel, Dock.Bottom);
        panel.Children.Add(footerPanel);
        var image = new Image { Source = LoadImage(resultPath), Stretch = Stretch.Uniform, Margin = new Thickness(10) };
        panel.Children.Add(new System.Windows.Controls.Border { Background = RenderUi.CanvasBackground, BorderBrush = RenderUi.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = image });
        Content = panel;
    }

    private static BitmapImage LoadImage(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static void SaveToGallery(string resultPath, Button button)
    {
        string galleryDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MyRevitPlugin", "SavedRenders");
        Directory.CreateDirectory(galleryDirectory);
        string savedPath = Path.Combine(galleryDirectory, $"AI-Render-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        File.Copy(resultPath, savedPath, overwrite: false);
        button.Content = "Saved to Gallery";
        button.IsEnabled = false;
    }

    private static void SaveAs(string resultPath)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save rendered image", FileName = Path.GetFileName(resultPath), DefaultExt = ".png", Filter = "PNG image|*.png" };
        if (dialog.ShowDialog() == true && !string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(resultPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(resultPath, dialog.FileName, overwrite: true);
    }
}

internal sealed class RenderGalleryWindow : Window
{
    private readonly UIDocument _uiDocument;
    private readonly RevitStudioBridge _studioBridge;
    private readonly ProjectGalleryInfo _currentProject;
    private readonly WrapPanel _grid = new();
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly System.Windows.Controls.ComboBox _projectFilter;
    private readonly FileSystemWatcher _watcher;

    public RenderGalleryWindow(UIDocument uiDocument)
    {
        _uiDocument = uiDocument;
        _studioBridge = new RevitStudioBridge();
        _currentProject = RenderGalleryStorage.ForDocument(uiDocument.Document);
        Title = "AI Render — Saved Gallery";
        Width = 1120;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = RenderUi.WindowBackground;
        Foreground = RenderUi.PrimaryText;
        Directory.CreateDirectory(RenderGalleryStorage.RootDirectory);

        var root = new DockPanel { Margin = new Thickness(24) };
        var heading = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 18) };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headingText = new StackPanel();
        _title = new TextBlock { Text = "Saved renders", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = RenderUi.PrimaryText };
        _subtitle = new TextBlock { Text = "Only this Revit project's images are shown.", Foreground = RenderUi.MutedText, Margin = new Thickness(0, 4, 0, 0) };
        headingText.Children.Add(_title);
        headingText.Children.Add(_subtitle);
        heading.Children.Add(headingText);
        var openFolder = RenderUi.Button("Open gallery folder", false, 145, new Thickness(0));
        openFolder.Click += (_, _) => Process.Start(new ProcessStartInfo { FileName = SelectedFilter.DirectoryPath ?? RenderGalleryStorage.RootDirectory, UseShellExecute = true });
        System.Windows.Controls.Grid.SetColumn(openFolder, 1);
        heading.Children.Add(openFolder);
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

        var filterBar = new System.Windows.Controls.Border { Background = Brushes.White, BorderBrush = RenderUi.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 14) };
        var filterRow = new System.Windows.Controls.Grid();
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        filterRow.ColumnDefinitions.Add(new ColumnDefinition());
        filterRow.Children.Add(new TextBlock { Text = "Show", Foreground = RenderUi.MutedText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        _projectFilter = RenderUi.ComboBox(0, new Thickness(0));
        _projectFilter.SelectionChanged += (_, _) => RefreshGallery();
        System.Windows.Controls.Grid.SetColumn(_projectFilter, 1);
        filterRow.Children.Add(_projectFilter);
        filterRow.Children.Add(new TextBlock { Text = "Right-click a render to edit it again, copy, export, or delete it.", Foreground = RenderUi.MutedText, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 0, 0, 0) });
        filterBar.Child = filterRow;
        DockPanel.SetDock(filterBar, Dock.Top);
        root.Children.Add(filterBar);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _grid });
        Content = root;

        PopulateFilters();
        RefreshGallery();
        _watcher = new FileSystemWatcher(RenderGalleryStorage.RootDirectory, "*.png") { IncludeSubdirectories = true, EnableRaisingEvents = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
        FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(RefreshGallery);
        RenamedEventHandler renamed = (_, _) => Dispatcher.BeginInvoke(RefreshGallery);
        _watcher.Created += changed;
        _watcher.Deleted += changed;
        _watcher.Changed += changed;
        _watcher.Renamed += renamed;
        Closed += (_, _) => _watcher.Dispose();
    }

    private GalleryFilter SelectedFilter => _projectFilter.SelectedItem as GalleryFilter ?? GalleryFilter.Current(_currentProject);

    private void PopulateFilters()
    {
        _projectFilter.Items.Clear();
        _projectFilter.Items.Add(GalleryFilter.Current(_currentProject));
        _projectFilter.Items.Add(GalleryFilter.All());
        foreach (ProjectGalleryInfo project in RenderGalleryStorage.DiscoverProjects().Where(project => !string.Equals(project.DirectoryPath, _currentProject.DirectoryPath, StringComparison.OrdinalIgnoreCase)))
            _projectFilter.Items.Add(GalleryFilter.Project(project));
        _projectFilter.SelectedIndex = 0;
    }

    private void RefreshGallery()
    {
        GalleryFilter filter = SelectedFilter;
        string[] images = filter.IncludeAll
            ? Directory.EnumerateFiles(RenderGalleryStorage.RootDirectory, "*.png", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).ToArray()
            : Directory.Exists(filter.DirectoryPath)
                ? Directory.EnumerateFiles(filter.DirectoryPath!, "*.png", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc).ToArray()
                : Array.Empty<string>();
        _title.Text = $"Saved renders  ·  {images.Length}";
        _subtitle.Text = filter.Description;
        _grid.Children.Clear();
        if (images.Length == 0)
        {
            _grid.Children.Add(new TextBlock { Text = "No saved renders in this project yet.\nSave a result from Render Studio and it will appear here automatically.", FontSize = 17, Foreground = RenderUi.MutedText, Margin = new Thickness(8, 30, 0, 0), TextWrapping = TextWrapping.Wrap });
            return;
        }

        foreach (string imagePath in images)
        {
            ProjectGalleryInfo project = ProjectForImage(imagePath);
            var content = new StackPanel();
            content.Children.Add(new Image { Source = LoadThumbnail(imagePath), Width = 316, Height = 184, Stretch = Stretch.UniformToFill });
            content.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(imagePath), FontWeight = FontWeights.SemiBold, Foreground = RenderUi.PrimaryText, Margin = new Thickness(12, 10, 12, 2), TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock { Text = $"{project.DisplayName}  ·  {File.GetLastWriteTime(imagePath):dd MMM yyyy, HH:mm}", Foreground = RenderUi.MutedText, FontSize = 11, Margin = new Thickness(12, 0, 12, 11), TextTrimming = TextTrimming.CharacterEllipsis });
            var card = new System.Windows.Controls.Border { Width = 318, Margin = new Thickness(7), Background = RenderUi.CardBackground, BorderBrush = RenderUi.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Child = content, Cursor = System.Windows.Input.Cursors.Hand, ContextMenu = CreateContextMenu(imagePath, project) };
            card.MouseLeftButtonUp += (_, args) => { if (args.ClickCount == 2) OpenImage(imagePath); };
            _grid.Children.Add(card);
        }
    }

    private ProjectGalleryInfo ProjectForImage(string imagePath)
    {
        string? directory = Path.GetDirectoryName(imagePath);
        if (directory is null || RenderGalleryStorage.IsDirectlyInRoot(imagePath))
            return new ProjectGalleryInfo("legacy", "Legacy / Unassigned", RenderGalleryStorage.RootDirectory);
        return RenderGalleryStorage.DiscoverProjects().FirstOrDefault(project => string.Equals(project.DirectoryPath, directory, StringComparison.OrdinalIgnoreCase))
            ?? new ProjectGalleryInfo(Path.GetFileName(directory), Path.GetFileName(directory), directory);
    }

    private System.Windows.Controls.ContextMenu CreateContextMenu(string imagePath, ProjectGalleryInfo project)
    {
        var menu = new System.Windows.Controls.ContextMenu { Background = Brushes.White, Foreground = RenderUi.PrimaryText, BorderBrush = RenderUi.Border };
        menu.Items.Add(MenuItem("Edit in Render Studio", () => EditInStudio(imagePath, project)));
        menu.Items.Add(MenuItem("Open image", () => OpenImage(imagePath)));
        menu.Items.Add(MenuItem("Show in File Explorer", () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{imagePath}\"") { UseShellExecute = true })));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Copy image", () => Clipboard.SetImage(LoadFullImage(imagePath))));
        menu.Items.Add(MenuItem("Save image as…", () => SaveImageAs(imagePath)));
        menu.Items.Add(new Separator());
        var delete = MenuItem("Delete", () => DeleteImage(imagePath));
        delete.Foreground = RenderUi.ErrorBrush;
        menu.Items.Add(delete);
        return menu;
    }

    private void EditInStudio(string imagePath, ProjectGalleryInfo project)
    {
        Hide();
        try
        {
            ProjectGalleryInfo saveDestination = project.ProjectId == "legacy" ? _currentProject : project;
            var studio = new RenderStudioWindow(_uiDocument, _uiDocument.ActiveView, imagePath, saveDestination, _studioBridge);
            studio.Closed += (_, _) =>
            {
                if (IsLoaded)
                {
                    Show();
                    RefreshGallery();
                }
            };
            studio.Show();
        }
        catch
        {
            Show();
            throw;
        }
    }

    private static System.Windows.Controls.MenuItem MenuItem(string title, Action action)
    {
        var item = new System.Windows.Controls.MenuItem { Header = title, Padding = new Thickness(10, 6, 18, 6) };
        item.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                RenderLog.Error("gallery-action", exception);
                new RenderErrorWindow("That gallery action didn’t finish", "The saved render is unchanged unless you selected Delete.", exception, "gallery-action").ShowDialog();
            }
        };
        return item;
    }

    private static void OpenImage(string imagePath) => Process.Start(new ProcessStartInfo { FileName = imagePath, UseShellExecute = true });

    private static void SaveImageAs(string imagePath)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save rendered image", FileName = Path.GetFileName(imagePath), DefaultExt = ".png", Filter = "PNG image|*.png" };
        if (dialog.ShowDialog() == true && !string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(imagePath), StringComparison.OrdinalIgnoreCase))
            File.Copy(imagePath, dialog.FileName, overwrite: true);
    }

    private void DeleteImage(string imagePath)
    {
        if (MessageBox.Show($"Delete {Path.GetFileName(imagePath)} from the gallery?", "Delete saved render", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        File.Delete(imagePath);
        RefreshGallery();
    }

    private static BitmapImage LoadThumbnail(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path);
        bitmap.DecodePixelWidth = 360;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource LoadFullImage(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private sealed record GalleryFilter(string Label, string Description, string? DirectoryPath, bool IncludeAll)
    {
        public static GalleryFilter Current(ProjectGalleryInfo project) => new($"Current project — {project.DisplayName}", "Only renders saved for the open Revit project are shown.", project.DirectoryPath, false);
        public static GalleryFilter All() => new("All projects", "Renders from every saved Revit project, including legacy unassigned images.", RenderGalleryStorage.RootDirectory, true);
        public static GalleryFilter Project(ProjectGalleryInfo project) => new(project.DisplayName, $"Only renders saved for {project.DisplayName} are shown.", project.DirectoryPath, false);
        public override string ToString() => Label;
    }
}

internal static class RenderUi
{
    public static readonly Brush WindowBackground = Brush("#F6F8FB");
    public static readonly Brush HeaderBackground = Brush("#FFFFFF");
    public static readonly Brush CardBackground = Brush("#FFFFFF");
    public static readonly Brush InputBackground = Brush("#FFFFFF");
    public static readonly Brush CanvasBackground = Brush("#E9EEF5");
    public static readonly Brush Border = Brush("#D7DEE8");
    public static readonly Brush PrimaryText = Brush("#172033");
    public static readonly Brush MutedText = Brush("#667085");
    public static readonly Brush AccentBrush = Brush("#2563EB");
    public static readonly Brush ErrorBrush = Brush("#DC2626");
    public static readonly Brush WarningBrush = Brush("#B45309");
    public static readonly Brush SuccessBrush = Brush("#168A45");

    public static TextBlock SectionLabel(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = AccentBrush, Margin = new Thickness(0, 0, 0, 10) };
    public static TextBlock FieldLabel(string text) => new() { Text = text, Foreground = MutedText, Margin = new Thickness(0, 0, 0, 5) };
    public static System.Windows.Controls.ComboBox ComboBox(int selectedIndex, Thickness? margin = null) => new() { SelectedIndex = selectedIndex, Margin = margin ?? new Thickness(0, 0, 0, 16), Padding = new Thickness(9, 6, 9, 6), Background = InputBackground, Foreground = PrimaryText, BorderBrush = Border, BorderThickness = new Thickness(1) };
    public static Button Button(string content, bool primary, double width, Thickness margin, bool isDefault = false) => new() { Content = content, Width = width, Height = 34, Margin = margin, IsDefault = isDefault, Background = primary ? AccentBrush : CardBackground, Foreground = primary ? Brushes.White : PrimaryText, BorderBrush = primary ? AccentBrush : Border, BorderThickness = new Thickness(1), FontWeight = FontWeights.SemiBold };
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
