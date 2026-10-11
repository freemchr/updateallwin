using System.Runtime.InteropServices;
using static UpdateAllWin.Toolbar.Native;

namespace UpdateAllWin.Toolbar;

public sealed record TrayMenuItem(string Text, Action? Action, bool Enabled = true, bool Checked = false, bool IsDefault = false)
{
    public static TrayMenuItem Separator { get; } = new("-", null);
}

/// <summary>
/// Notification-area icon via Shell_NotifyIcon (the supported Windows 11 taskbar integration).
/// Owns a hidden top-level window that receives icon callbacks and can block shutdown during BIOS flashing.
/// Must be created on the UI thread (its messages are pumped by the WinUI message loop).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = WM_APP + 1;
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly WndProc _wndProc; // keep the delegate alive
    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private string _tooltip;
    private bool _added;
    private bool _blockingShutdown;

    public TrayIcon(string tooltip, string iconPath)
    {
        _tooltip = tooltip;
        _wndProc = WindowProc;
        var cls = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "UpdateAllWin.TrayWindow." + Environment.ProcessId,
        };
        RegisterClassEx(ref cls);
        // A hidden top-level popup (not message-only) so ShutdownBlockReasonCreate works with it.
        _hwnd = CreateWindowEx(0, cls.lpszClassName, "UpdateAll Win", unchecked((int)0x80000000) /* WS_POPUP */, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        SetIcon(iconPath);
        Add();
    }

    public event Action? Activated;

    /// <summary>Builds the right-click menu each time it opens.</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }

    public void SetIcon(string iconPath)
    {
        var size = GetSystemMetrics(49 /* SM_CXSMICON */);
        var icon = LoadImage(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        if (icon == IntPtr.Zero) return;
        var old = _icon;
        _icon = icon;
        if (_added) Modify();
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    public void SetTooltip(string text)
    {
        _tooltip = text.Length > 127 ? text[..127] : text;
        if (_added) Modify();
    }

    /// <summary>While true, Windows shows the reason on the shutdown screen and this app vetoes shutdown.</summary>
    public void BlockShutdown(bool block, string reason = "")
    {
        if (block == _blockingShutdown) return;
        _blockingShutdown = block;
        if (block) ShutdownBlockReasonCreate(_hwnd, reason);
        else ShutdownBlockReasonDestroy(_hwnd);
    }

    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    private void Modify()
    {
        var data = Data(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            switch (LowWord(lParam))
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    Activated?.Invoke();
                    break;
                case WM_CONTEXTMENU:
                case WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == TaskbarCreated)
        {
            Add(); // Explorer restarted
            return IntPtr.Zero;
        }
        if (msg == WM_QUERYENDSESSION && _blockingShutdown)
            return IntPtr.Zero; // veto while BIOS is flashing
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var items = MenuProvider?.Invoke() ?? [];
        if (items.Count == 0) return;
        var menu = CreatePopupMenu();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (ReferenceEquals(item, TrayMenuItem.Separator) || item.Text == "-")
                {
                    AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                    continue;
                }
                var flags = MF_STRING | (item.Enabled ? 0 : MF_GRAYED) | (item.Checked ? MF_CHECKED : 0);
                AppendMenu(menu, flags, (UIntPtr)(i + 1), item.Text);
                if (item.IsDefault) SetMenuDefaultItem(menu, (uint)(i + 1), 0);
            }

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd); // required so the menu closes when clicking elsewhere
            var cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_BOTTOMALIGN, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd > 0 && cmd <= items.Count) items[cmd - 1].Action?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        BlockShutdown(false);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        DestroyWindow(_hwnd);
    }
}
