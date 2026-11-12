using GeekDesk.Constant;
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Resources;

namespace GeekDesk.Util
{
    /// <summary>
    /// 自研托盘图标（替换 HandyControl 的 hc:NotifyIcon）。
    ///
    /// 为什么不用 hc:NotifyIcon（HandyControl 3.5.1 / master 均有此缺陷）：
    ///   1. 每次控件 Loaded 都会调用 Init()，Init() 里 RegisterClass() + CreateWindowEx()
    ///      新建一个隐藏的消息窗口并覆盖 _messageWindowHandle，旧窗口从不 DestroyWindow；
    ///   2. 内部只用一个 bool _added 表示"是否已注册"，但托盘图标的身份其实是 (hWnd, uID) 二元组，
    ///      且 NIM_ADD / NIM_MODIFY / NIM_DELETE 的返回值全都不检查。
    ///   RDP RemoteApp 断开重连时会重复触发 Loaded 与 TaskbarCreated 刷新：
    ///   DELETE 用新句柄（shell 里没有这条记录，静默失败）→ ADD 用新句柄（成功），
    ///   而旧句柄上的注册从未删除，于是每重连一次托盘就多出一个图标，且新旧图标都还能响应点击
    ///   （旧窗口一直存活、WndProc 是同一个回调）。
    ///
    /// 本实现的约束：
    ///   整个进程只创建一个实例、一个消息窗口、一个固定的 uID；
    ///   每次 Shell_NotifyIcon 调用都检查返回值，失败就把记账改回"未注册"以便下次自愈；
    ///   收到 TaskbarCreated 只对同一个 (hWnd, uID) 重新 ADD，绝不换新窗口、绝不留下旧注册。
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        #region Win32

        private const int WM_TRAYICON = 0x0400 + 1024; // 与 HandyControl 相同的回调消息号

        private const int NIM_ADD = 0x00000000;
        private const int NIM_MODIFY = 0x00000001;
        private const int NIM_DELETE = 0x00000002;

        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;

        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;

        private const int SM_CXSMICON = 49;
        private const int SM_CYSMICON = 50;

        /// <summary>托盘图标在本进程内的唯一 ID（与消息窗口一起构成 shell 里的图标身份）</summary>
        private const uint TRAY_UID = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        #endregion

        private HwndSource _source;
        private System.Drawing.Icon _icon;
        private IntPtr _hIcon = IntPtr.Zero;
        private string _tooltip = string.Empty;
        private uint _taskbarCreatedMessage;
        private bool _added;
        private bool _visible;
        private bool _disposed;

        /// <summary>左键单击 / 双击</summary>
        public event EventHandler Click;
        /// <summary>右键（用于弹出托盘菜单）</summary>
        public event EventHandler RightClick;

        /// <summary>托盘消息窗口句柄（固定不变，从不重建）</summary>
        public IntPtr Handle
        {
            get { return _source == null ? IntPtr.Zero : _source.Handle; }
        }

        public TrayIcon(string tooltip)
        {
            _tooltip = tooltip ?? string.Empty;
            _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

            // 只创建一个消息窗口：1x1、不可见、不进入任务栏，仅用于接收托盘回调与 TaskbarCreated 广播
            HwndSourceParameters parameters = new HwndSourceParameters("GeekDeskTrayIcon_" + Constants.MY_UUID);
            parameters.Width = 1;
            parameters.Height = 1;
            parameters.WindowStyle = 0;
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);

            LoadIcon();
            Application.Current.Exit += OnApplicationExit;
        }

        #region 对外接口

        public string Tooltip
        {
            get { return _tooltip; }
            set
            {
                _tooltip = value ?? string.Empty;
                if (_added) ModifyIcon();
            }
        }

        public bool IsVisible
        {
            get { return _visible; }
        }

        public void SetVisible(bool visible)
        {
            if (visible)
            {
                _visible = true;
                EnsureAdded();
            }
            else
            {
                _visible = false;
                Remove();
            }
        }

        /// <summary>托盘刷新一次（例如主题/图标变化后）</summary>
        public void Refresh()
        {
            Remove();
            EnsureAdded();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Remove(); } catch { }
            try
            {
                if (Application.Current != null) Application.Current.Exit -= OnApplicationExit;
            }
            catch { }
            try
            {
                if (_source != null)
                {
                    _source.RemoveHook(WndProc);
                    _source.Dispose();
                    _source = null;
                }
            }
            catch { }
            try
            {
                if (_icon != null)
                {
                    _icon.Dispose();
                    _icon = null;
                }
            }
            catch { }
            _hIcon = IntPtr.Zero;
        }

        #endregion

        #region 内部实现

        private NOTIFYICONDATA CreateData()
        {
            NOTIFYICONDATA data = new NOTIFYICONDATA();
            data.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            data.hWnd = Handle;
            data.uID = TRAY_UID;
            data.szTip = string.Empty;
            data.szInfo = string.Empty;
            data.szInfoTitle = string.Empty;
            data.guidItem = Guid.Empty;
            return data;
        }

        private void LoadIcon()
        {
            if (_hIcon != IntPtr.Zero)
            {
                return;
            }
            try
            {
                StreamResourceInfo info = Application.GetResourceStream(new Uri("pack://application:,,,/Logo.ico"));
                if (info == null || info.Stream == null)
                {
                    LogUtil.WriteErrorLog("托盘图标资源 Logo.ico 未找到", "");
                    return;
                }
                int cx = GetSystemMetrics(SM_CXSMICON);
                int cy = GetSystemMetrics(SM_CYSMICON);
                if (cx <= 0) cx = 16;
                if (cy <= 0) cy = 16;
                using (System.IO.Stream stream = info.Stream)
                {
                    _icon = new System.Drawing.Icon(stream, cx, cy);
                    _hIcon = _icon.Handle;
                }
            }
            catch (Exception ex)
            {
                LogUtil.WriteErrorLog(ex, "托盘图标加载失败");
            }
        }

        /// <summary>
        /// 确保图标已注册到 shell。失败时保持"未注册"状态，等下次 TaskbarCreated / 显隐切换再补。
        /// </summary>
        private bool EnsureAdded()
        {
            if (_disposed || !_visible) return false;
            if (_added) return true;
            if (_hIcon == IntPtr.Zero) LoadIcon();
            if (_hIcon == IntPtr.Zero) return false;

            NOTIFYICONDATA data = CreateData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            data.uCallbackMessage = WM_TRAYICON;
            data.hIcon = _hIcon;
            data.szTip = _tooltip;

            if (!Shell_NotifyIcon(NIM_ADD, ref data))
            {
                // shell 尚未准备好（例如 explorer 正在重启）→ 不记账，等 TaskbarCreated 再来一次
                LogUtil.WriteLog("Shell_NotifyIcon(NIM_ADD) 失败");
                _added = false;
                return false;
            }
            _added = true;
            return true;
        }

        private void Remove()
        {
            if (!_added) return;
            NOTIFYICONDATA data = CreateData();
            data.uFlags = 0;
            bool ok = Shell_NotifyIcon(NIM_DELETE, ref data);
            // 无论成功与否都把记账清零：下次 ADD 会重新注册，避免"以为还在、实际已经没了"
            _added = false;
            if (!ok) LogUtil.WriteLog("Shell_NotifyIcon(NIM_DELETE) 失败");
        }

        private void ModifyIcon()
        {
            if (!_added) return;
            NOTIFYICONDATA data = CreateData();
            data.uFlags = NIF_ICON | NIF_TIP;
            data.hIcon = _hIcon;
            data.szTip = _tooltip;
            if (!Shell_NotifyIcon(NIM_MODIFY, ref data))
            {
                _added = false;
                EnsureAdded();
            }
        }

        /// <summary>
        /// 任务栏重建（explorer 重启、RemoteApp 重连导致 rdpinit 重建托盘区）：
        /// shell 里的旧图标全部失效，必须对同一个 (hWnd, uID) 重新 ADD 一次。
        /// </summary>
        private void OnTaskbarCreated()
        {
            _added = false;
            EnsureAdded();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_TRAYICON)
            {
                int mouseMessage = lParam.ToInt32() & 0xFFFF;
                // 只认 WM_LBUTTONUP：双击会先发 LBUTTONUP，再按 DBLCLK 处理会导致面板被切换两次
                if (mouseMessage == WM_LBUTTONUP)
                {
                    handled = true;
                    EventHandler click = Click;
                    if (click != null) click(this, EventArgs.Empty);
                }
                else if (mouseMessage == WM_RBUTTONUP)
                {
                    handled = true;
                    EventHandler rightClick = RightClick;
                    if (rightClick != null) rightClick(this, EventArgs.Empty);
                }
                return IntPtr.Zero;
            }

            if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
            {
                handled = true;
                OnTaskbarCreated();
                return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        /// <summary>把菜单窗口提到前台，否则点击菜单外部时菜单不会自动关闭</summary>
        public static void BringToForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try { SetForegroundWindow(hwnd); } catch { }
        }

        private void OnApplicationExit(object sender, EventArgs e)
        {
            Dispose();
        }

        #endregion
    }
}