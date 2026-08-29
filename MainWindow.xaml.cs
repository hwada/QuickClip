using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.System;

namespace QuickClip;

public sealed partial class MainWindow : Window
{
    private const int CaptureDelayMilliseconds = 80;
    private const int MinimumWindowSideLength = 10;
    private const byte WindowOpacity = 230;
    private static readonly string ConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config",
        "QuickClip",
        "config.toml");

    private readonly IntPtr _windowHandle;
    private bool _isCapturing;
    private bool _isDragging;
    private PointInt32 _dragPointerOrigin;
    private PointInt32 _dragWindowOrigin;

    public MainWindow()
    {
        InitializeComponent();

        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ConfigureWindow();
        RestoreWindowPlacement();
        AppWindow.Closing += AppWindow_Closing;
        Activated += MainWindow_Activated;
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState != WindowActivationState.Deactivated)
        {
            CaptureSurface.Focus(FocusState.Programmatic);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SaveWindowPlacement();
        Application.Current.Exit();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SaveWindowPlacement();
    }

    private void RestoreWindowPlacement()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                return;
            }

            int? x = null;
            int? y = null;
            int? width = null;
            int? height = null;

            foreach (string rawLine in File.ReadLines(ConfigFilePath))
            {
                string line = rawLine.Split('#', 2)[0].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                string[] parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2
                    || !int.TryParse(
                        parts[1],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int value))
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "x":
                        x = value;
                        break;
                    case "y":
                        y = value;
                        break;
                    case "width":
                        width = value;
                        break;
                    case "height":
                        height = value;
                        break;
                }
            }

            if (x.HasValue
                && y.HasValue
                && width is >= MinimumWindowSideLength
                && height is >= MinimumWindowSideLength)
            {
                AppWindow.MoveAndResize(new RectInt32(
                    x.Value,
                    y.Value,
                    width.Value,
                    height.Value));
            }
        }
        catch (Exception exception)
        {
            // A damaged or inaccessible config must not prevent the app from starting.
            System.Diagnostics.Debug.WriteLine($"Window placement restore failed: {exception}");
        }
    }

    private void SaveWindowPlacement()
    {
        try
        {
            PointInt32 position = AppWindow.Position;
            SizeInt32 size = AppWindow.Size;
            string? configDirectory = Path.GetDirectoryName(ConfigFilePath);

            if (configDirectory is null)
            {
                return;
            }

            Directory.CreateDirectory(configDirectory);

            string contents = string.Create(
                CultureInfo.InvariantCulture,
                $"# QuickClip window placement{Environment.NewLine}" +
                $"x = {position.X}{Environment.NewLine}" +
                $"y = {position.Y}{Environment.NewLine}" +
                $"width = {size.Width}{Environment.NewLine}" +
                $"height = {size.Height}{Environment.NewLine}");

            string temporaryPath = ConfigFilePath + ".tmp";
            File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
            File.Move(temporaryPath, ConfigFilePath, overwrite: true);
        }
        catch (Exception exception)
        {
            // Failure to persist placement must not prevent the app from closing.
            System.Diagnostics.Debug.WriteLine($"Window placement save failed: {exception}");
        }
    }

    private async void CaptureSurface_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        PointInt32 position = AppWindow.Position;

        switch (e.Key)
        {
            case VirtualKey.Left:
                AppWindow.Move(new PointInt32(position.X - 1, position.Y));
                break;
            case VirtualKey.Right:
                AppWindow.Move(new PointInt32(position.X + 1, position.Y));
                break;
            case VirtualKey.Up:
                AppWindow.Move(new PointInt32(position.X, position.Y - 1));
                break;
            case VirtualKey.Down:
                AppWindow.Move(new PointInt32(position.X, position.Y + 1));
                break;
            case VirtualKey.Enter:
                if (!e.KeyStatus.WasKeyDown)
                {
                    await CaptureWithErrorHandlingAsync();
                }
                break;
            case VirtualKey.F1:
                if (!e.KeyStatus.WasKeyDown)
                {
                    await FitWindowToLargestRectangleAsync();
                }
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void ConfigureWindow()
    {
        // The app draws its own one-pixel border, so the native frame and title bar are removed.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }

        IntPtr extendedStyle = NativeMethods.GetWindowLongPtr(
            _windowHandle,
            NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(
            _windowHandle,
            NativeMethods.GWL_EXSTYLE,
            new IntPtr(extendedStyle.ToInt64() | NativeMethods.WS_EX_LAYERED));

        if (!NativeMethods.SetLayeredWindowAttributes(
                _windowHandle,
                0,
                WindowOpacity,
                NativeMethods.LWA_ALPHA))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private void CaptureSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(CaptureSurface);
        bool canDrag = e.Pointer.PointerDeviceType != PointerDeviceType.Mouse
            || point.Properties.IsLeftButtonPressed;

        if (!canDrag || !NativeMethods.GetCursorPos(out NativeMethods.POINT cursorPosition))
        {
            return;
        }

        _isDragging = CaptureSurface.CapturePointer(e.Pointer);
        if (!_isDragging)
        {
            return;
        }

        _dragPointerOrigin = new PointInt32(cursorPosition.X, cursorPosition.Y);
        _dragWindowOrigin = AppWindow.Position;
        e.Handled = true;
    }

    private void CaptureSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || !NativeMethods.GetCursorPos(out NativeMethods.POINT cursorPosition))
        {
            return;
        }

        AppWindow.Move(new PointInt32(
            _dragWindowOrigin.X + cursorPosition.X - _dragPointerOrigin.X,
            _dragWindowOrigin.Y + cursorPosition.Y - _dragPointerOrigin.Y));

        e.Handled = true;
    }

    private void CaptureSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        StopDragging(e.Pointer);
        e.Handled = true;
    }

    private void CaptureSurface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
    }

    private async void CaptureSurface_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        CaptureSurface.ReleasePointerCaptures();
        _isDragging = false;
        e.Handled = true;

        await CaptureWithErrorHandlingAsync();
    }

    private async Task CaptureWithErrorHandlingAsync()
    {
        if (_isCapturing)
        {
            return;
        }

        _isCapturing = true;

        try
        {
            await CaptureAreaBehindWindowAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Screen capture failed: {exception}");
        }
        finally
        {
            _isCapturing = false;
            CaptureSurface.Focus(FocusState.Programmatic);
        }
    }

    private async Task FitWindowToLargestRectangleAsync()
    {
        if (_isCapturing)
        {
            return;
        }

        _isCapturing = true;
        IntPtr bitmap = IntPtr.Zero;

        try
        {
            if (!NativeMethods.GetWindowRect(_windowHandle, out NativeMethods.RECT windowBounds))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            int width = windowBounds.Right - windowBounds.Left;
            int height = windowBounds.Bottom - windowBounds.Top;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_HIDE);

            try
            {
                await Task.Delay(CaptureDelayMilliseconds);
                NativeMethods.DwmFlush();
                bitmap = CaptureScreenRegion(windowBounds.Left, windowBounds.Top, width, height);
            }
            finally
            {
                NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_SHOW);
                NativeMethods.SetForegroundWindow(_windowHandle);
            }

            byte[] pixels = ReadBitmapPixels(bitmap, width, height);
            DetectedRectangle? rectangle = ImageRectangleDetector.FindLargest(pixels, width, height);

            if (rectangle is DetectedRectangle detected
                && detected.Width >= MinimumWindowSideLength
                && detected.Height >= MinimumWindowSideLength)
            {
                AppWindow.MoveAndResize(new RectInt32(
                    windowBounds.Left + detected.X,
                    windowBounds.Top + detected.Y,
                    detected.Width,
                    detected.Height));
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Rectangle detection failed: {exception}");
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            _isCapturing = false;
            CaptureSurface.Focus(FocusState.Programmatic);
        }
    }

    private static byte[] ReadBitmapPixels(IntPtr bitmap, int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        var bitmapInfo = new NativeMethods.BITMAPINFO
        {
            Header = new NativeMethods.BITMAPINFOHEADER
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = NativeMethods.BI_RGB,
                SizeImage = (uint)pixels.Length
            }
        };

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            int scanLines = NativeMethods.GetDIBits(
                screenDc,
                bitmap,
                0,
                (uint)height,
                pixels,
                ref bitmapInfo,
                NativeMethods.DIB_RGB_COLORS);

            if (scanLines != height)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return pixels;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void StopDragging(Pointer pointer)
    {
        if (!_isDragging)
        {
            return;
        }

        CaptureSurface.ReleasePointerCapture(pointer);
        _isDragging = false;
    }

    private async Task CaptureAreaBehindWindowAsync()
    {
        if (!NativeMethods.GetWindowRect(_windowHandle, out NativeMethods.RECT windowBounds))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        int width = windowBounds.Right - windowBounds.Left;
        int height = windowBounds.Bottom - windowBounds.Top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_HIDE);

        try
        {
            // Give Desktop Window Manager a frame to redraw the content uncovered by this window.
            await Task.Delay(CaptureDelayMilliseconds);
            NativeMethods.DwmFlush();

            IntPtr bitmap = CaptureScreenRegion(windowBounds.Left, windowBounds.Top, width, height);
            CopyBitmapToClipboard(bitmap);
        }
        finally
        {
            NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_SHOW);
            NativeMethods.SetForegroundWindow(_windowHandle);
        }
    }

    private static IntPtr CaptureScreenRegion(int x, int y, int width, int height)
    {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previousObject = IntPtr.Zero;

        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            previousObject = NativeMethods.SelectObject(memoryDc, bitmap);
            bool captured = NativeMethods.BitBlt(
                memoryDc,
                0,
                0,
                width,
                height,
                screenDc,
                x,
                y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

            if (!captured)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return bitmap;
        }
        catch
        {
            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            throw;
        }
        finally
        {
            if (previousObject != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, previousObject);
            }

            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void CopyBitmapToClipboard(IntPtr bitmap)
    {
        bool clipboardOpened = false;

        // The clipboard can briefly be locked by another application, so retry a few times.
        for (int attempt = 0; attempt < 5 && !clipboardOpened; attempt++)
        {
            clipboardOpened = NativeMethods.OpenClipboard(_windowHandle);
            if (!clipboardOpened)
            {
                Thread.Sleep(10);
            }
        }

        if (!clipboardOpened)
        {
            NativeMethods.DeleteObject(bitmap);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!NativeMethods.EmptyClipboard()
                || NativeMethods.SetClipboardData(NativeMethods.CF_BITMAP, bitmap) == IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            // Ownership of the bitmap belongs to the clipboard after SetClipboardData succeeds.
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }
}
