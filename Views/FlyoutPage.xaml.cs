using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using TrayQr.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Launcher = Windows.System.Launcher;
using WinRT.Interop;

namespace TrayQr.Views;

/// <summary>
/// The flyout's content: a Create tab that turns text into a QR code as you type, a Read tab that
/// finds codes in a pasted, dropped, opened, or snipped image, and a Camera tab that reads codes
/// from a webcam. Nothing runs while hidden: the camera is on only while its tab is on screen.
/// </summary>
public sealed partial class FlyoutPage : Page, IDisposable
{
    // Big enough for a crisp paste into documents; each module stays a whole number of pixels.
    private const int ExportPixels = 1024;
    private const int PreviewPixels = 512;

    private static readonly (string Letter, string Name)[] ErrorCorrectionLevels =
    [
        ("L", "Low (7%)"),
        ("M", "Medium (15%)"),
        ("Q", "Quartile (25%)"),
        ("H", "High (30%)"),
    ];

    private readonly DispatcherQueueTimer _encodeTimer;
    private QrMatrix? _matrix;
    private QrErrorCorrection _level;
    private QrColor _foreground;
    private QrColor _background;

    // Camera starts and stops run one at a time; a newer request bumps the generation so an older
    // start that is still awaiting gives up instead of leaving the camera on.
    private readonly SemaphoreSlim _cameraLock = new(1, 1);
    private CameraScanner? _scanner;
    private int _cameraGeneration;
    private bool _isCameraFrozen;

    public FlyoutPage()
    {
        InitializeComponent();

        _encodeTimer = DispatcherQueue.CreateTimer();
        _encodeTimer.Interval = TimeSpan.FromMilliseconds(120);
        _encodeTimer.IsRepeating = false;
        _encodeTimer.Tick += (_, _) => UpdateQrCode();

        for (int i = 0; i < ErrorCorrectionLevels.Length; i++)
        {
            RadioMenuFlyoutItem item = new() { Text = ErrorCorrectionLevels[i].Name, Tag = (QrErrorCorrection)i, GroupName = "ErrorCorrection" };
            item.Click += ErrorCorrectionItem_Click;
            ErrorCorrectionMenu.Items.Add(item);
        }

        _level = SettingsService.ErrorCorrection;
        _foreground = SettingsService.ForegroundColor;
        _background = SettingsService.BackgroundColor;
        ShowErrorCorrection();
        ShowColors();
        InputBox.Text = SettingsService.LastText;
        UpdateQrCode();
    }

    /// <summary>The flyout window's handle, for parenting file pickers.</summary>
    public nint WindowHandle { get; set; }

    /// <summary>Brings the flyout back if it light-dismissed while a file picker had focus.</summary>
    public Action? RequestShow { get; set; }

    /// <summary>Gives the flyout focus back after Windows' camera consent prompt is answered.</summary>
    public Action? RequestFocus { get; set; }

    /// <summary>True while the camera consent prompt may be up; the flyout doesn't light-dismiss meanwhile.</summary>
    public bool IsAwaitingCameraConsent { get; private set; }

    private bool IsCameraMode => ModeSelector.SelectedItem == CameraItem;

    public void OnShown()
    {
        if (ModeSelector.SelectedItem == CreateItem)
        {
            InputBox.Focus(FocusState.Programmatic);
            InputBox.SelectAll();
        }
        else if (IsCameraMode)
        {
            _ = StartCameraAsync();
        }
    }

    public void OnHidden()
    {
        _encodeTimer.Stop();
        _ = StopCameraAsync();
        SettingsService.LastText = InputBox.Text;
    }

    public void Dispose()
    {
        _encodeTimer.Stop();
        _ = StopCameraAsync();
    }

    // The settings page covers this one, so the camera stops while it's open.
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _ = StopCameraAsync();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (IsCameraMode)
        {
            _ = StartCameraAsync();
        }
    }

    /// <summary>Switches to the Read tab and reads whatever image is on the clipboard (after a screen snip).</summary>
    public Task ReadFromClipboardAsync()
    {
        ModeSelector.SelectedItem = ReadItem;
        return ReadClipboardAsync();
    }

    // ---- Create ----

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _encodeTimer.Stop();
        _encodeTimer.Start();
    }

    private void ErrorCorrectionItem_Click(object sender, RoutedEventArgs e)
    {
        _level = (QrErrorCorrection)((FrameworkElement)sender).Tag;
        SettingsService.ErrorCorrection = _level;
        ShowErrorCorrection();
        UpdateQrCode();
    }

    private void ShowErrorCorrection()
    {
        (string letter, string name) = ErrorCorrectionLevels[(int)_level];
        ErrorCorrectionText.Text = letter;
        ToolTipService.SetToolTip(ErrorCorrectionButton, $"Error correction: {name}. Higher levels survive more damage but make a denser code.");
        foreach (RadioMenuFlyoutItem item in ErrorCorrectionMenu.Items.OfType<RadioMenuFlyoutItem>())
        {
            item.IsChecked = (QrErrorCorrection)item.Tag == _level;
        }
    }

    private void ColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        QrColor color = new(args.NewColor.R, args.NewColor.G, args.NewColor.B);
        if (sender == ForegroundPicker)
        {
            _foreground = color;
        }
        else
        {
            _background = color;
        }

        ShowColorSwatches();
        RenderPreview();
    }

    private void ColorFlyout_Closed(object sender, object e)
    {
        SettingsService.ForegroundColor = _foreground;
        SettingsService.BackgroundColor = _background;
    }

    private void ResetColors_Click(object sender, RoutedEventArgs e)
    {
        _foreground = QrColor.Black;
        _background = QrColor.White;
        ShowColors();
        RenderPreview();
    }

    private void ShowColors()
    {
        ForegroundPicker.Color = ToColor(_foreground);
        BackgroundPicker.Color = ToColor(_background);
        ShowColorSwatches();
    }

    private void ShowColorSwatches()
    {
        ForegroundSwatch.Foreground = new SolidColorBrush(ToColor(_foreground));
        BackgroundSwatch.Fill = new SolidColorBrush(ToColor(_background));
    }

    private static Windows.UI.Color ToColor(QrColor color) => ColorHelper.FromArgb(255, color.R, color.G, color.B);

    private void UpdateQrCode()
    {
        string text = InputBox.Text;
        _matrix = QrCodec.Encode(text, _level);

        bool hasCode = _matrix is not null;
        CopyImageButton.IsEnabled = hasCode;
        SaveImageButton.IsEnabled = hasCode;
        QrImage.CanDrag = hasCode;
        ToolTipService.SetToolTip(QrImage, hasCode ? "Drag into a folder, chat, or document" : null);
        QrPlaceholderIcon.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TooLongMessage.Visibility = !hasCode && text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        RenderPreview();
    }

    private void RenderPreview()
    {
        LowContrastWarning.Visibility = _matrix is not null && QrColor.ContrastRatio(_foreground, _background) < QrCodec.MinReadableContrast
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_matrix is null)
        {
            QrImage.Source = null;
            return;
        }

        int scale = QrCodec.ScaleFor(_matrix, PreviewPixels);
        QrImage.Source = QrImageService.ToBitmap(QrCodec.RenderBgra(_matrix, scale, _foreground, _background), _matrix.Size * scale);
    }

    private (byte[] Pixels, int Size)? RenderExport()
    {
        if (_matrix is null)
        {
            return null;
        }

        int scale = QrCodec.ScaleFor(_matrix, ExportPixels);
        return (QrCodec.RenderBgra(_matrix, scale, _foreground, _background), _matrix.Size * scale);
    }

    private async void CopyImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (RenderExport() is not { } image)
        {
            return;
        }

        try
        {
            await QrImageService.CopyPngAsync(image.Pixels, image.Size);
            ShowButtonDone(CopyImageButton, "Copied");
        }
        catch (Exception ex)
        {
            ToolTipService.SetToolTip(CopyImageButton, $"Couldn't copy: {ex.Message}");
        }
    }

    private async void SaveImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (RenderExport() is not { } image)
        {
            return;
        }

        FileSavePicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = SuggestFileName(InputBox.Text),
        };
        picker.FileTypeChoices.Add("PNG image", [".png"]);
        InitializeWithWindow.Initialize(picker, WindowHandle);

        StorageFile? file = await picker.PickSaveFileAsync();
        RequestShow?.Invoke();
        if (file is not null)
        {
            await QrImageService.SavePngAsync(image.Pixels, image.Size, file);
            ShowButtonDone(SaveImageButton, "Saved");
        }
    }

    // Offers the code as both a PNG file (Explorer, chat apps) and a bitmap (documents, editors).
    private async void QrImage_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (RenderExport() is not { } image)
        {
            args.Cancel = true;
            return;
        }

        DragOperationDeferral deferral = args.GetDeferral();
        try
        {
            StorageFile file = await QrImageService.SaveDragPngAsync(image.Pixels, image.Size, $"{SuggestFileName(InputBox.Text)}.png");
            args.AllowedOperations = DataPackageOperation.Copy;
            args.Data.RequestedOperation = DataPackageOperation.Copy;
            args.Data.SetStorageItems([file]);
            args.Data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        }
        catch (Exception)
        {
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static string SuggestFileName(string text)
    {
        string name = new(text.Trim()
            .Replace("https://", "").Replace("http://", "")
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsWhiteSpace(c) ? '-' : c)
            .Take(40)
            .ToArray());
        return string.IsNullOrWhiteSpace(name.Trim('-')) ? "QR code" : $"QR {name.Trim('-')}";
    }

    private void ShowButtonDone(Button button, string message)
    {
        object original = button.Content;
        object tip = ToolTipService.GetToolTip(button);
        button.Content = new FontIcon { FontSize = 16, Glyph = "" };
        ToolTipService.SetToolTip(button, message);

        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1.5);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            button.Content = original;
            ToolTipService.SetToolTip(button, tip);
        };
        timer.Start();
    }

    // ---- Read ----

    private void ModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        CreatePanel.Visibility = ModeSelector.SelectedItem == CreateItem ? Visibility.Visible : Visibility.Collapsed;
        ReadPanel.Visibility = ModeSelector.SelectedItem == ReadItem ? Visibility.Visible : Visibility.Collapsed;

        if (IsCameraMode)
        {
            // x:Load defers the panel and its media element until the tab is first opened.
            if (CameraPanel is null)
            {
                FindName(nameof(CameraPanel));
            }

            CameraPanel!.Visibility = Visibility.Visible;
            _ = StartCameraAsync();
        }
        else
        {
            if (CameraPanel is not null)
            {
                CameraPanel.Visibility = Visibility.Collapsed;
            }

            _ = StopCameraAsync();
        }
    }

    private void ScanScreenButton_Click(object sender, RoutedEventArgs e) => App.Current.StartScreenScan();

    private async void PasteButton_Click(object sender, RoutedEventArgs e) => await ReadClipboardAsync();

    private async void Paste_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ReadClipboardAsync();
    }

    private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
    {
        FileOpenPicker picker = new() { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (string extension in QrImageService.ImageExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(picker, WindowHandle);
        StorageFile? file = await picker.PickSingleFileAsync();
        RequestShow?.Invoke();
        if (file is not null)
        {
            await ReadImageAsync(async () => await file.OpenReadAsync());
        }
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Bitmap))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Read QR code";
            DropHighlight.Visibility = Visibility.Visible;
        }
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e) => DropHighlight.Visibility = Visibility.Collapsed;

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);
        await ReadDataAsync(e.DataView);
    }

    private async Task ReadClipboardAsync()
    {
        try
        {
            await ReadDataAsync(Clipboard.GetContent());
        }
        catch (Exception ex)
        {
            ShowReadMessage($"Couldn't read the clipboard: {ex.Message}");
        }
    }

    private async Task ReadDataAsync(DataPackageView data)
    {
        if (data.Contains(StandardDataFormats.Bitmap))
        {
            RandomAccessStreamReference reference = await data.GetBitmapAsync();
            await ReadImageAsync(async () => await reference.OpenReadAsync());
            return;
        }

        if (data.Contains(StandardDataFormats.StorageItems))
        {
            IReadOnlyList<IStorageItem> items = await data.GetStorageItemsAsync();
            if (items.FirstOrDefault(QrImageService.IsImageFile) is StorageFile file)
            {
                await ReadImageAsync(async () => await file.OpenReadAsync());
                return;
            }
        }

        ShowReadMessage("That isn't an image. Copy or drop a picture that contains a QR code.");
    }

    private async Task ReadImageAsync(Func<Task<IRandomAccessStream>> open)
    {
        ResultsList.ItemsSource = null;
        ReadStatusText.Visibility = Visibility.Collapsed;
        ReadingRing.IsActive = true;
        try
        {
            using IRandomAccessStream stream = await open();

            BitmapImage preview = new() { DecodePixelHeight = 280 };
            await preview.SetSourceAsync(stream.CloneStream());
            SourceImage.Source = preview;
            DropHint.Visibility = Visibility.Collapsed;

            stream.Seek(0);
            (byte[] pixels, int width, int height) = await QrImageService.LoadBgraAsync(stream);
            IReadOnlyList<string> results = await Task.Run(() => QrCodec.Decode(pixels, width, height));

            if (results.Count == 0)
            {
                ShowReadMessage("No QR code found. Try a sharper or larger image.");
                return;
            }

            ResultsList.ItemsSource = results.Select(ScanResult.From).ToList();

        }
        catch (Exception ex)
        {
            ShowReadMessage($"Couldn't open that image: {ex.Message}");
        }
        finally
        {
            ReadingRing.IsActive = false;
        }
    }

    private void ShowReadMessage(string message)
    {
        ReadStatusText.Text = message;
        ReadStatusText.Visibility = Visibility.Visible;
    }

    private static ScanResult ResultOf(object sender) => (ScanResult)((FrameworkElement)sender).DataContext;

    private async void OpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (ResultOf(sender).Link is { } link)
        {
            await Launcher.LaunchUriAsync(link);
        }
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        DataPackage package = new();
        package.SetText(ResultOf(sender).Text);
        Clipboard.SetContent(package);
        ShowButtonDone((Button)sender, "Copied");
    }

    private void EditResult_Click(object sender, RoutedEventArgs e)
    {
        InputBox.Text = ResultOf(sender).Text;
        ModeSelector.SelectedItem = CreateItem;
    }

    // ---- Camera ----

    private async Task StartCameraAsync()
    {
        int generation = ++_cameraGeneration;
        await _cameraLock.WaitAsync();
        CameraScanner? scanner = null;
        try
        {
            await DisposeScannerAsync();
            if (generation != _cameraGeneration || _isCameraFrozen || CameraPanel is null)
            {
                return;
            }

            ShowCameraStatus(null);
            CameraStartingRing.IsActive = true;

            IReadOnlyList<DeviceInformation> cameras = await CameraScanner.FindCamerasAsync();
            if (generation != _cameraGeneration)
            {
                return;
            }

            string? id = CameraPolicy.PickCamera(cameras.Select(c => c.Id).ToList(), SettingsService.CameraId);
            ShowCameraChoices(cameras, id);
            if (cameras.FirstOrDefault(c => c.Id == id) is not { } camera)
            {
                ShowCameraStatus("No camera found. Connect one and try again.");
                return;
            }

            // Mirror every camera except one on the back of the device, so moving a code feels natural.
            CameraMirror.ScaleX = camera.EnclosureLocation?.Panel == Windows.Devices.Enumeration.Panel.Back ? 1 : -1;

            scanner = new CameraScanner();
            CameraScanner started = scanner;
            scanner.CodesFound += (codes, snapshot) => DispatcherQueue.TryEnqueue(() => _ = ShowCameraCodesAsync(started, codes, snapshot));
            scanner.Failed += problem => DispatcherQueue.TryEnqueue(() => _ = StopCameraWithProblemAsync(started, problem));
            // On first use, starting the camera shows Windows' consent prompt, which takes focus.
            // The window keeps the flyout open behind it, and takes focus back once it's answered.
            IsAwaitingCameraConsent = NeedsCameraConsent();
            try
            {
                await scanner.StartAsync(camera.Id, CameraPreview);
            }
            finally
            {
                if (IsAwaitingCameraConsent)
                {
                    IsAwaitingCameraConsent = false;
                    RequestFocus?.Invoke();
                }
            }

            if (generation == _cameraGeneration)
            {
                _scanner = scanner;
                scanner = null;
            }
        }
        catch (Exception ex)
        {
            // The exception's text is diagnostic detail; the user gets a stable message they can act on.
            Trace.WriteLine($"TrayQr camera didn't start: 0x{ex.HResult:X8} {ex}");
            if (generation == _cameraGeneration)
            {
                ShowCameraProblem(CameraProblems.FromException(ex));
            }
        }
        finally
        {
            try
            {
                // A start that was overtaken (or failed halfway) leaves nothing running.
                if (scanner is not null)
                {
                    await scanner.DisposeAsync();
                }
            }
            finally
            {
                if (CameraPanel is not null)
                {
                    CameraStartingRing.IsActive = false;
                }

                _cameraLock.Release();
            }
        }
    }

    private static bool NeedsCameraConsent()
    {
        // AppCapability arrived in Windows 10 1903; older builds just light-dismiss behind the prompt.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            return false;
        }

        try
        {
            return AppCapability.Create("Webcam").CheckAccess() == AppCapabilityAccessStatus.UserPromptRequired;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task StopCameraAsync()
    {
        _cameraGeneration++;
        await _cameraLock.WaitAsync();
        try
        {
            await DisposeScannerAsync();
        }
        finally
        {
            _cameraLock.Release();
        }
    }

    private async Task DisposeScannerAsync()
    {
        if (_scanner is { } scanner)
        {
            _scanner = null;
            await scanner.DisposeAsync();
        }
    }

    private async Task StopCameraWithProblemAsync(CameraScanner scanner, CameraProblem problem)
    {
        if (scanner != _scanner)
        {
            return;
        }

        await StopCameraAsync();
        ShowCameraProblem(problem);
    }

    // Freezes on the frame with the code and turns the camera off until Scan again.
    private async Task ShowCameraCodesAsync(CameraScanner scanner, IReadOnlyList<string> codes, SoftwareBitmap snapshot)
    {
        if (scanner != _scanner)
        {
            snapshot.Dispose();
            return;
        }

        _isCameraFrozen = true;
        _ = StopCameraAsync();

        SoftwareBitmapSource source = new();
        await source.SetBitmapAsync(snapshot);
        snapshot.Dispose();
        CameraSnapshot.Source = source;

        CameraResultsList.ItemsSource = codes.Select(ScanResult.From).ToList();
        CameraHintText.Visibility = Visibility.Collapsed;
        ScanAgainText.Text = "Scan again";
        ScanAgainButton.Visibility = Visibility.Visible;
    }

    private void ShowCameraChoices(IReadOnlyList<DeviceInformation> cameras, string? selectedId)
    {
        CameraMenu.Items.Clear();
        foreach (DeviceInformation camera in cameras)
        {
            RadioMenuFlyoutItem item = new() { Text = camera.Name, Tag = camera.Id, GroupName = "Camera", IsChecked = camera.Id == selectedId };
            item.Click += CameraChoice_Click;
            CameraMenu.Items.Add(item);
        }

        CameraNameText.Text = cameras.FirstOrDefault(c => c.Id == selectedId)?.Name ?? string.Empty;
        CameraButton.Visibility = cameras.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowCameraProblem(CameraProblem problem) =>
        ShowCameraStatus(CameraProblems.MessageFor(problem), showPrivacyLink: problem == CameraProblem.AccessDenied);

    // Every camera message ends in "try again", so it comes with a button that does.
    private void ShowCameraStatus(string? message, bool showPrivacyLink = false)
    {
        CameraStatusText.Text = message ?? string.Empty;
        CameraStatusPanel.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        PrivacySettingsLink.Visibility = showPrivacyLink ? Visibility.Visible : Visibility.Collapsed;
        ScanAgainText.Text = "Try again";
        ScanAgainButton.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResumeScanning()
    {
        _isCameraFrozen = false;
        CameraSnapshot.Source = null;
        CameraResultsList.ItemsSource = null;
        CameraHintText.Visibility = Visibility.Visible;
        ScanAgainButton.Visibility = Visibility.Collapsed;
        _ = StartCameraAsync();
    }

    private void CameraChoice_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.CameraId = (string)((FrameworkElement)sender).Tag;
        ResumeScanning();
    }

    private void ScanAgainButton_Click(object sender, RoutedEventArgs e) => ResumeScanning();

    private async void PrivacySettingsLink_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-webcam"));

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();
}

/// <summary>One decoded code, with a launchable link when the text is one.</summary>
public sealed class ScanResult(string text, Uri? link)
{
    private static readonly string[] LaunchableSchemes = ["http", "https", "mailto", "tel", "sms", "geo"];

    public string Text { get; } = text;

    public Uri? Link { get; } = link;

    public Visibility LinkVisibility => Link is null ? Visibility.Collapsed : Visibility.Visible;

    public static ScanResult From(string text) =>
        new(text, Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri) && LaunchableSchemes.Contains(uri.Scheme) ? uri : null);
}