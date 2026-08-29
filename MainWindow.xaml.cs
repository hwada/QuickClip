using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.System;

namespace QuickClip;

public sealed partial class MainWindow : Window
{
    private const int CaptureDelayMilliseconds = 80;
    private const byte WindowOpacity = 230;

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
        Application.Current.Exit();
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

    private static class NativeMethods
    {
        internal const int GWL_EXSTYLE = -20;
        internal const int SW_HIDE = 0;
        internal const int SW_SHOW = 5;
        internal const long WS_EX_LAYERED = 0x00080000L;
        internal const uint LWA_ALPHA = 0x00000002;
        internal const uint CF_BITMAP = 2;
        internal const uint SRCCOPY = 0x00CC0020;
        internal const uint CAPTUREBLT = 0x40000000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        internal static IntPtr GetWindowLongPtr(IntPtr window, int index)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(window, index)
                : new IntPtr(GetWindowLong32(window, index));
        }

        internal static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newValue)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(window, index, newValue)
                : new IntPtr(SetWindowLong32(window, index, newValue.ToInt32()));
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr window, int index, int newValue);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr newValue);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetLayeredWindowAttributes(
            IntPtr window,
            uint colorKey,
            byte alpha,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out RECT rectangle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmFlush();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BitBlt(
            IntPtr destination,
            int destinationX,
            int destinationY,
            int width,
            int height,
            IntPtr source,
            int sourceX,
            int sourceY,
            uint rasterOperation);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(IntPtr deviceContext);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr graphicObject);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenClipboard(IntPtr newOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetClipboardData(uint format, IntPtr memory);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseClipboard();
    }
}
