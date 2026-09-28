using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TrayQr.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Launcher = Windows.System.Launcher;
using WinRT.Interop;

namespace TrayQr.Views;

/// <summary>
/// The flyout's content: a Create tab that turns text into a QR code as you type, and a Read tab
/// that finds codes in a pasted, dropped, opened, or snipped image. Nothing runs while hidden.
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

    private bool IsReadMode => ModeSelector.SelectedItem == ReadItem;

    public void OnShown()
    {
        if (!IsReadMode)
        {
            InputBox.Focus(FocusState.Programmatic);
            InputBox.SelectAll();
        }
    }

    public void OnHidden()
    {
        _encodeTimer.Stop();
        SettingsService.LastText = InputBox.Text;
    }

    public void Dispose() => _encodeTimer.Stop();

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
        CreatePanel.Visibility = IsReadMode ? Visibility.Collapsed : Visibility.Visible;
        ReadPanel.Visibility = IsReadMode ? Visibility.Visible : Visibility.Collapsed;
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