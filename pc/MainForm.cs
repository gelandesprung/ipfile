using System.Drawing;
using System.Windows.Forms;

namespace FeigeP2P;

public sealed class MainForm : Form
{
    private readonly AppConfig _config;
    private readonly TransferService _transfer;
    private readonly DiscoveryService _discovery;

    private readonly Label _nameLabel = new();
    private readonly TextBox _nameBox = new();
    private readonly Label _localInfo = new();
    private readonly Label _onlineDot = new();
    private readonly ListView _devices = new();
    private readonly Label _emptyHint = new();
    private readonly ListView _recent = new();
    private readonly Label _recentEmpty = new();
    private readonly RichTextBox _log = new();
    private readonly Button _clearLogBtn = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _progressLabel = new();
    private readonly ContextMenuStrip _deviceMenu = new();
    private readonly ToolTip _tip = new();
    private readonly ToolStripMenuItem _menuSend;
    private readonly ToolStripMenuItem _menuCopyIp;
    private readonly ToolStripMenuItem _menuRemove;
    private readonly ToolStripMenuItem _menuRefresh;
    private readonly ToolStripMenuItem _menuManual;
    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _menuOpenFolder = new("打开收件夹");

    // 配对入口与展开面板（默认收起，只有点击入口才显示二维码）
    private readonly Button _addDeviceBtn = new();
    private readonly Button _webBtn = new();
    private readonly Panel _pairPanel = new();
    private readonly PictureBox _qrBox = new();
    private readonly Label _expiryLabel = new();
    private readonly Label _pairAddr = new();
    private readonly Button _refreshQr = new();
    private readonly Button _collapseQr = new();
    private readonly Panel _expiryTrack = new();
    private double _expiryFraction = 1;
    private Color _expiryColor = Color.FromArgb(30, 111, 217);
    private readonly System.Windows.Forms.Timer _pairTimer = new() { Interval = 1000 };

    private TableLayoutPanel? _leftGrid;
    private RowStyle? _entryRowStyle;
    private RowStyle? _pairRowStyle;
    private bool _pairExpanded;
    private string _localIp = "";

    /// <summary>真正的退出标志：点窗口关闭键只是收进托盘，只有托盘「退出」才结束进程。</summary>
    private bool _reallyExit;
    private bool _trayHintShown;

    private Peer? _menuTarget;
    /// <summary>设备列表的唯一数据源：局域网发现与扫码配对都写进这里，再统一渲染到 ListView。</summary>
    private readonly Dictionary<string, Peer> _peers = new();
    private string _lastLog = "";
    private DateTime _lastLogTime = DateTime.MinValue;
    private string _lastDiag = "";
    private DateTime _lastDiagTime = DateTime.MinValue;

    /// <summary>网页模式服务（懒启动）：窗口关了服务继续跑，程序退出才释放。</summary>
    private WebHostService? _web;
    private WebModeForm? _webForm;

    public MainForm()
    {
        _config = AppConfig.Load();
        _localIp = AppConfig.GetLocalIp();
        _transfer = new TransferService(_config);
        _discovery = new DiscoveryService(_config);

        _menuSend = new ToolStripMenuItem("发送文件…");
        _menuCopyIp = new ToolStripMenuItem("复制 IP 地址");
        _menuRemove = new ToolStripMenuItem("从列表移除");
        _menuRefresh = new ToolStripMenuItem("刷新设备");
        _menuManual = new ToolStripMenuItem("手动输入 IP 直连…");

        BuildUi();
        WireEvents();
        BuildTray();

        // 接收服务失败时要给出明确原因，而不是整个程序闪退
        string? tcpError = null;
        try { _transfer.Start(); }
        catch (Exception ex) { tcpError = ex.Message; }

        // UDP 自动发现是独立的尽力而为通道，绑定失败不能影响主流程
        string? udpError = null;
        try { _discovery.Start(); }
        catch (Exception ex) { udpError = ex.Message; }

        AppendLog($"已启动 · 本机 {_config.DeviceName} ({_localIp}:{_config.TcpPort})", Color.FromArgb(25, 100, 180));

        if (tcpError == null)
            AppendLog($"接收服务已就绪 · 监听 [::]:{_config.TcpPort}（IPv4/IPv6 双栈）", Color.FromArgb(25, 140, 60));
        else
            AppendLog($"接收服务启动失败：{tcpError}（端口可能被占用，或已有飞鸽在运行）", Color.FromArgb(190, 40, 40));

        if (udpError == null)
            AppendLog($"局域网自动发现已开启 · 广播端口 {_config.UdpPort}，同网段设备会自动出现在列表中", Color.FromArgb(25, 140, 60));
        else
            AppendLog($"局域网自动发现不可用：{udpError}（仍可用扫码配对）", Color.FromArgb(180, 130, 20));

        AppendLog("跨网段或自动发现不到时，点「扫码添加设备」出示二维码配对", Color.Gray);
        AppendLog("若对方扫码后连不上，请确认 Windows 防火墙已允许本程序访问「专用网络」", Color.Gray);
    }

    // ---------------- UI 构建 ----------------

    private void BuildUi()
    {
        Text = "飞鸽 P2P · 局域网互传";
        Icon = LoadAppIcon(new Size(48, 48));
        // 下面的尺寸都是 96 DPI 的设计值，AutoScaleMode.None + 手工 Scale 会整体放大
        Size = new Size(920, 660);
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 246, 248);
        Font = new Font("微软雅黑", 9F);
        // 自己按 DPI 等比缩放整套坐标：交给 WinForms 的自动缩放时，这台 200% 的机器上
        // 只放大了窗口、没放大行高，文字就会互相压叠。设计基准是 96 DPI。
        AutoScaleMode = AutoScaleMode.None;

        // 根容器：3 行（顶部信息 / 主体 / 底部进度）
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = Padding.Empty, Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
        Controls.Add(root);

        root.Controls.Add(BuildTop(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        root.Controls.Add(BuildBottom(), 0, 2);

        // 一次性把整套坐标按 DPI 等比放大（含行高、边距、控件尺寸），字号本来就会随 DPI 放大，
        // 这样两者始终同比例，任何缩放下都不会挤在一起。
        var scale = DeviceDpi / 96f;
        if (scale > 1.01f) Scale(new SizeF(scale, scale));

        root.PerformLayout();
    }

    /// <summary>把 96 DPI 的设计像素换算成当前 DPI 的实际像素（用于 ListView 行高这类不会自动缩放的值）。</summary>
    private int D(int design) => (int)Math.Round(design * DeviceDpi / 96.0);

    /// <summary>窗口真正显示后再把日志对齐到末尾：布局完成前滚动位置会算错，导致顶部露出半行。</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // 布局完成后按真实条目数决定「空状态 / 列表」谁显示：
        // 窗体句柄创建时 ListView 会被重新置为可见，必须在显示后校正一次。
        UpdateDeviceEmptyState();
        ScrollLogToEnd();
    }

    /// <summary>
    /// 用 EM_LINESCROLL 往文件末尾滚：内容超出时贴底，没超出时自动夹到顶部，
    /// 不像 ScrollToCaret 那样会在可视区放得下时留下残行。
    /// </summary>
    private void ScrollLogToEnd()
    {
        if (!_log.IsHandleCreated) return;
        SendMessage(_log.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(-10000));
        // 末尾多一个空行（每条日志都以换行结束），回退一行让最后一条完整可见
        SendMessage(_log.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)1);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int EM_LINESCROLL = 0x00B6;

    /// <summary>窗口开出来不能超出屏幕工作区（高缩放下 1.5 倍尺寸很容易超出）。</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var wa = Screen.FromControl(this).WorkingArea;
        var w = Math.Min(Width, (int)(wa.Width * 0.94));
        var h = Math.Min(Height, (int)(wa.Height * 0.94));
        if (w != Width || h != Height) Size = new Size(w, h);
        if (Left < wa.Left || Top < wa.Top || Left > wa.Right - 100 || Top > wa.Bottom - 100)
        {
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
        }
    }

    private Control BuildTop()
    {
        // 不用 TableLayoutPanel 的百分比行：AutoSize 的大字号标签会溢出单元格造成重叠。
        // 改为「实测尺寸 + 手工定位」，任意 DPI 下都精确。
        var host = new Panel
        {
            Dock = DockStyle.Fill, BackColor = Color.White, Margin = Padding.Empty, Padding = Padding.Empty
        };

        _nameLabel.Text = _config.DeviceName;
        _nameLabel.Font = new Font("微软雅黑", 15F, FontStyle.Bold);
        _nameLabel.ForeColor = Color.FromArgb(28, 32, 38);
        _nameLabel.AutoSize = true;
        _nameLabel.Cursor = Cursors.Hand;
        _tip.SetToolTip(_nameLabel, "点击修改本机设备名");

        _nameBox.Font = new Font("微软雅黑", 12F);
        _nameBox.Visible = false;

        _localInfo.Text = $"{_localIp}:{_config.TcpPort}   ·   广播发现 + 扫码配对   ·   点击名称可修改";
        _localInfo.ForeColor = Color.FromArgb(130, 138, 148);
        _localInfo.Font = new Font("微软雅黑", 9F);
        _localInfo.AutoSize = true;

        _onlineDot.Text = "● 接收就绪";
        _onlineDot.ForeColor = Color.FromArgb(32, 168, 72);
        _onlineDot.Font = new Font("微软雅黑", 10F, FontStyle.Bold);
        _onlineDot.AutoSize = true; // 不 Dock：AutoSize 才能保证「● 在线」不被裁成「● 在」
        _onlineDot.BackColor = Color.Transparent;

        host.Controls.Add(_nameLabel);
        host.Controls.Add(_nameBox);
        host.Controls.Add(_localInfo);
        host.Controls.Add(_onlineDot);

        var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(224, 228, 233) };
        host.Controls.Add(line);

        host.Layout += (_, _) => LayoutTopBar(host);
        return host;
    }

    /// <summary>顶部条定位：右侧状态贴右，名称与副标题纵向堆叠，按实测高度排布。</summary>
    private void LayoutTopBar(Panel host)
    {
        // 运行期算出来的坐标不会被自动缩放，设计值要自己换算
        var padX = D(22);
        var right = host.ClientSize.Width - padX;

        _onlineDot.Location = new Point(
            Math.Max(padX, right - _onlineDot.Width),
            Math.Max(0, (host.ClientSize.Height - _onlineDot.Height) / 2));

        if (_nameBox.Visible)
        {
            var maxW = Math.Max(D(160), _onlineDot.Left - padX - D(20));
            _nameBox.Width = Math.Min(D(320), maxW);
            _nameBox.Location = new Point(padX, Math.Max(D(8), (host.ClientSize.Height - _nameBox.Height) / 2));
        }
        else
        {
            _nameLabel.Location = new Point(padX, D(10));
            _localInfo.Location = new Point(padX, _nameLabel.Bottom + D(2));
        }
    }

    private Control BuildBody()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = new Padding(12, 10, 12, 8), Padding = Padding.Empty
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56F));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44F));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        body.Controls.Add(BuildLeft(), 0, 0);
        body.Controls.Add(BuildRight(), 1, 0);
        return body;
    }

    private Control BuildLeft()
    {
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Margin = new Padding(0, 0, 6, 0), Padding = Padding.Empty, BackColor = Color.White
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));   // 标题
        _entryRowStyle = new RowStyle(SizeType.Absolute, 40F);      // 唯一配对入口
        left.RowStyles.Add(_entryRowStyle);
        _pairRowStyle = new RowStyle(SizeType.Absolute, 0F);        // 点击后才展开
        left.RowStyles.Add(_pairRowStyle);
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 设备列表
        _leftGrid = left;

        // 标题行也用 grid，避免 Width 计算导致副标题被裁
        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, Padding = new Padding(14, 0, 14, 0)
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var titleLbl = new Label
        {
            Text = "设备", Font = new Font("微软雅黑", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 46, 54), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
        };
        var subLbl = new Label
        {
            Text = "右键设备发送文件", Font = new Font("微软雅黑", 8.5F),
            ForeColor = Color.FromArgb(150, 156, 164), Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight, AutoSize = true, Margin = new Padding(0, 0, 0, 0)
        };
        head.Controls.Add(titleLbl, 0, 0);
        head.Controls.Add(subLbl, 1, 0);
        left.Controls.Add(head, 0, 0);

        left.Controls.Add(BuildPairEntry(), 0, 1);
        left.Controls.Add(BuildPairPanel(), 0, 2);

        _devices.View = View.Details;
        _devices.FullRowSelect = true;
        _devices.MultiSelect = false;
        _devices.Dock = DockStyle.Fill;
        _devices.BorderStyle = BorderStyle.None;
        _devices.BackColor = Color.White;
        _devices.Font = new Font("微软雅黑", 9.5F);
        _devices.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _devices.ShowItemToolTips = true;   // IPv6 地址列会被截断，悬停看完整值
        _devices.ContextMenuStrip = _deviceMenu;
        _devices.Columns.Add("设备名", 160);
        _devices.Columns.Add("IP 地址", 130);
        _devices.Columns.Add("端口", 54);
        _devices.Columns.Add("状态", 62);
        _deviceMenu.Items.AddRange(new ToolStripItem[]
        {
            _menuSend, _menuCopyIp, new ToolStripSeparator(), _menuRemove, _menuRefresh,
            new ToolStripSeparator(), _menuOpenFolder, _menuManual
        });
        _deviceMenu.Opening += DeviceMenu_Opening;

        _emptyHint.Text = "暂无设备\n\n同网段设备会自动出现；跨网段请点上方「扫码添加设备」";
        _emptyHint.TextAlign = ContentAlignment.MiddleCenter;
        _emptyHint.ForeColor = Color.FromArgb(165, 170, 178);
        _emptyHint.Font = new Font("微软雅黑", 10F);
        _emptyHint.Dock = DockStyle.Fill;

        var listHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.White };
        listHost.Controls.Add(_devices);
        listHost.Controls.Add(_emptyHint);
        _emptyHint.BringToFront(); // 空状态提示要浮在列表之上，列表有内容时再隐藏
        // 空白处右键也能出菜单，「手动输入 IP 直连」不会因为列表为空而不可用
        listHost.ContextMenuStrip = _deviceMenu;
        _emptyHint.ContextMenuStrip = _deviceMenu;
        left.Controls.Add(listHost, 0, 3);

        // 列宽按列表实际宽度分配（用 SizeChanged 而不是窗体 Resize，避免拿到未布局的宽度）
        void FitColumns()
        {
            int w = Math.Max(_devices.ClientSize.Width - 4, 200);
            var portW = D(54);
            var stateW = D(104);
            _devices.Columns[0].Width = (int)((w - portW - stateW) * 0.55);
            _devices.Columns[1].Width = w - portW - stateW - _devices.Columns[0].Width;
            _devices.Columns[2].Width = portW;
            _devices.Columns[3].Width = stateW;
        }
        _devices.SizeChanged += (_, _) => FitColumns();
        FitColumns();
        return left;
    }

    /// <summary>默认态的两个并列入口：装 App 的走扫码配对，浏览器访客走网页模式。</summary>
    private Control BuildPairEntry()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill, BackColor = Color.White,
            Padding = new Padding(14, 0, 14, 6)
        };

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.White
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _addDeviceBtn.Text = "＋   扫码添加设备";
        _addDeviceBtn.Dock = DockStyle.Fill;
        _addDeviceBtn.FlatStyle = FlatStyle.Flat;
        _addDeviceBtn.FlatAppearance.BorderColor = Color.FromArgb(210, 217, 227);
        _addDeviceBtn.FlatAppearance.BorderSize = 1;
        _addDeviceBtn.BackColor = Color.FromArgb(247, 249, 252);
        _addDeviceBtn.ForeColor = Color.FromArgb(30, 111, 217);
        _addDeviceBtn.Font = new Font("微软雅黑", 9.5F);
        _addDeviceBtn.TextAlign = ContentAlignment.MiddleLeft;
        _addDeviceBtn.Padding = new Padding(10, 0, 0, 0);
        _addDeviceBtn.Cursor = Cursors.Hand;
        _addDeviceBtn.Margin = new Padding(0, 0, 4, 0);
        _tip.SetToolTip(_addDeviceBtn, "出示二维码，让对方扫码添加这台设备");

        // 实心蓝：与「添加设备」并列，但一眼看出这是新增的、且是给没装 App 的人的入口
        _webBtn.Text = "🌐  网页模式";
        _webBtn.Dock = DockStyle.Fill;
        _webBtn.FlatStyle = FlatStyle.Flat;
        _webBtn.FlatAppearance.BorderSize = 0;
        _webBtn.BackColor = Color.FromArgb(30, 111, 217);
        _webBtn.ForeColor = Color.White;
        _webBtn.Font = new Font("微软雅黑", 9.5F);
        _webBtn.TextAlign = ContentAlignment.MiddleCenter;
        _webBtn.Cursor = Cursors.Hand;
        _webBtn.Margin = new Padding(4, 0, 0, 0);
        _tip.SetToolTip(_webBtn, "让朋友用浏览器连进来收/发文件，不用装 App");

        row.Controls.Add(_addDeviceBtn, 0, 0);
        row.Controls.Add(_webBtn, 1, 0);
        host.Controls.Add(row);
        return host;
    }

    /// <summary>展开态：二维码 + 倒计时 + 刷新 + 收起。所有控件初始可见但父面板隐藏。</summary>
    private Control BuildPairPanel()
    {
        _pairPanel.Dock = DockStyle.Fill;
        _pairPanel.Visible = false;
        _pairPanel.BackColor = Color.White;
        _pairPanel.Padding = new Padding(14, 0, 14, 8);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.White
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        grid.Controls.Add(new Label
        {
            Text = "扫码添加设备", Font = new Font("微软雅黑", 10.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 46, 54), Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _collapseQr.Text = "收起";
        _collapseQr.AutoSize = true;
        _collapseQr.FlatStyle = FlatStyle.Flat;
        _collapseQr.FlatAppearance.BorderSize = 0;
        _collapseQr.BackColor = Color.Transparent;
        _collapseQr.ForeColor = Color.FromArgb(120, 128, 138);
        _collapseQr.Font = new Font("微软雅黑", 8.5F);
        _collapseQr.Cursor = Cursors.Hand;
        _collapseQr.Anchor = AnchorStyles.Right;
        _collapseQr.Margin = new Padding(0, 2, 0, 0);
        grid.Controls.Add(_collapseQr, 1, 0);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.White
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        _qrBox.Dock = DockStyle.Fill;
        _qrBox.SizeMode = PictureBoxSizeMode.Zoom;
        _qrBox.BackColor = Color.White;
        _qrBox.BorderStyle = BorderStyle.FixedSingle;
        _qrBox.Margin = new Padding(0, 2, 10, 0);
        content.Controls.Add(_qrBox, 0, 0);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.White
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 剩余时间
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 6F));  // 时间条
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // 地址
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 刷新

        _expiryLabel.Text = "剩余 10:00";
        _expiryLabel.Font = new Font("微软雅黑", 10.5F, FontStyle.Bold);
        _expiryLabel.ForeColor = Color.FromArgb(30, 111, 217);
        _expiryLabel.AutoSize = true;
        _expiryLabel.Margin = new Padding(0, 0, 0, 4);
        right.Controls.Add(_expiryLabel, 0, 0);

        // 自绘细条：系统 ProgressBar 在浅色主题下是绿色且无法改色，与设计色板不一致
        _expiryTrack.Dock = DockStyle.Fill;
        _expiryTrack.BackColor = Color.FromArgb(232, 235, 240);
        _expiryTrack.Margin = new Padding(0, 0, 0, 6);
        _expiryTrack.Paint += (_, e) =>
        {
            var w = (int)Math.Round(_expiryTrack.ClientSize.Width * _expiryFraction);
            if (w <= 0) return;
            using var brush = new SolidBrush(_expiryColor);
            e.Graphics.FillRectangle(brush, 0, 0, w, _expiryTrack.ClientSize.Height);
        };
        right.Controls.Add(_expiryTrack, 0, 1);

        _pairAddr.Text = "";
        _pairAddr.Font = new Font("Consolas", 8F);
        _pairAddr.ForeColor = Color.FromArgb(136, 136, 136);
        _pairAddr.Dock = DockStyle.Fill;
        _pairAddr.AutoSize = false;
        _pairAddr.AutoEllipsis = true;
        _pairAddr.TextAlign = ContentAlignment.MiddleLeft;
        _pairAddr.Margin = Padding.Empty;
        right.Controls.Add(_pairAddr, 0, 2);

        _refreshQr.Text = "刷新二维码";
        _refreshQr.AutoSize = true;
        _refreshQr.FlatStyle = FlatStyle.Flat;
        _refreshQr.FlatAppearance.BorderColor = Color.FromArgb(210, 217, 227);
        _refreshQr.FlatAppearance.BorderSize = 1;
        _refreshQr.BackColor = Color.White;
        _refreshQr.ForeColor = Color.FromArgb(30, 111, 217);
        _refreshQr.Font = new Font("微软雅黑", 8.5F);
        _refreshQr.Padding = new Padding(10, 4, 10, 4);
        _refreshQr.Cursor = Cursors.Hand;
        _refreshQr.Margin = new Padding(0, 4, 0, 0);
        right.Controls.Add(_refreshQr, 0, 3);

        content.Controls.Add(right, 1, 0);
        grid.Controls.Add(content, 0, 1);
        grid.SetColumnSpan(content, 2);
        _pairPanel.Controls.Add(grid);
        return _pairPanel;
    }

    private Control BuildRight()
    {
        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Margin = new Padding(6, 0, 0, 0), Padding = Padding.Empty, BackColor = Color.White
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));   // 「最近传输」标题
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 148F));  // 最近 5 条
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));   // 「传输日志」标题
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 日志正文

        right.Controls.Add(SectionHead("最近传输", "点击打开所在位置", null), 0, 0);

        _recent.View = View.Details;
        _recent.HeaderStyle = ColumnHeaderStyle.None;
        _recent.FullRowSelect = true;
        _recent.MultiSelect = false;
        _recent.HideSelection = true;
        _recent.ShowItemToolTips = true;
        _recent.BorderStyle = BorderStyle.None;
        _recent.BackColor = Color.White;
        _recent.Dock = DockStyle.Fill;
        _recent.Font = new Font("微软雅黑", 9F);
        // Details 视图的行高只能靠小图标列表撑开：1×28 的透明图像即每行 28px（随 DPI 换算）
        _recent.SmallImageList = new ImageList { ImageSize = new Size(1, D(28)) };
        _recent.Columns.Add("文件", 210);
        _recent.Columns.Add("大小", 62, HorizontalAlignment.Right);
        _recent.Columns.Add("时间", 46, HorizontalAlignment.Right);
        _recent.MouseClick += (_, e) =>
        {
            var hit = _recent.GetItemAt(e.X, e.Y);
            if (hit?.Tag is string path) OpenFileLocation(path);
        };
        _recent.MouseMove += (_, e) =>
        {
            var over = _recent.GetItemAt(e.X, e.Y) != null;
            Cursor = over ? Cursors.Hand : Cursors.Default;
        };
        _recent.MouseLeave += (_, _) => Cursor = Cursors.Default;

        _recentEmpty.Text = "还没有传输记录\n\n收发的文件会显示在这里，最多保留 5 条";
        _recentEmpty.TextAlign = ContentAlignment.MiddleCenter;
        _recentEmpty.ForeColor = Color.FromArgb(170, 175, 182);
        _recentEmpty.Font = new Font("微软雅黑", 9F);
        _recentEmpty.Dock = DockStyle.Fill;

        var recentHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(10, 0, 10, 8), BackColor = Color.White };
        recentHost.Controls.Add(_recent);
        recentHost.Controls.Add(_recentEmpty);
        _recentEmpty.BringToFront();
        right.Controls.Add(recentHost, 0, 1);

        _clearLogBtn.Text = "清空";
        _clearLogBtn.Font = new Font("微软雅黑", 8.5F);
        _clearLogBtn.ForeColor = Color.FromArgb(120, 128, 138);
        _clearLogBtn.FlatStyle = FlatStyle.Flat;
        _clearLogBtn.FlatAppearance.BorderSize = 0;
        _clearLogBtn.BackColor = Color.Transparent;
        _clearLogBtn.Size = new Size(40, 22);
        _clearLogBtn.Cursor = Cursors.Hand;
        _clearLogBtn.Click += (_, _) => _log.Clear();
        right.Controls.Add(SectionHead("传输日志", null, _clearLogBtn), 0, 2);

        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Color.White;
        _log.Font = new Font("Consolas", 9.5F);
        _log.Margin = new Padding(10, 4, 10, 8);
        right.Controls.Add(_log, 0, 3);

        void FitRecentColumns()
        {
            // ListView 的列宽不是控件，不会随 DPI 缩放，这里自己换算
            int w = Math.Max(_recent.ClientSize.Width - D(4), D(160));
            var sizeW = D(70);
            var timeW = D(58);
            _recent.Columns[2].Width = timeW;
            _recent.Columns[1].Width = sizeW;
            _recent.Columns[0].Width = Math.Max(D(80), w - sizeW - timeW);
        }
        _recent.SizeChanged += (_, _) => FitRecentColumns();
        FitRecentColumns();
        return right;
    }

    /// <summary>分区标题行：左侧标题，右侧可选提示文字与操作控件。</summary>
    private static TableLayoutPanel SectionHead(string title, string? hint, Control? action)
    {
        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
            Margin = Padding.Empty, Padding = new Padding(14, 0, 12, 0), BackColor = Color.White
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        head.Controls.Add(new Label
        {
            Text = title, Font = new Font("微软雅黑", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 46, 54), Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        if (!string.IsNullOrEmpty(hint))
        {
            head.Controls.Add(new Label
            {
                Text = hint, Font = new Font("微软雅黑", 8.5F), AutoSize = true,
                ForeColor = Color.FromArgb(150, 156, 164), Anchor = AnchorStyles.Right
            }, 1, 0);
        }

        if (action != null) head.Controls.Add(action, 2, 0);
        return head;
    }

    private Control BuildBottom()
    {
        var bottom = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = Padding.Empty, Padding = new Padding(20, 10, 20, 8) };
        _progressLabel.Dock = DockStyle.Fill;
        _progressLabel.Text = "就绪  ·  同网段设备自动发现，右键设备即可发送文件";
        _progressLabel.ForeColor = Color.FromArgb(110, 118, 128);
        _progressLabel.Font = new Font("微软雅黑", 9F);
        _progress.Dock = DockStyle.Bottom;
        _progress.Height = 8;
        _progress.Minimum = 0;
        _progress.Maximum = 1000;
        _progress.Value = 0;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Visible = false;   // 空闲时不显示进度条，避免底部多出一条空槽
        bottom.Controls.Add(_progressLabel);
        bottom.Controls.Add(_progress);
        var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(224, 228, 233), Margin = Padding.Empty };
        bottom.Controls.Add(line);
        return bottom;
    }

    private void WireEvents()
    {
        _nameLabel.Click += (_, _) => BeginEditName();
        _nameBox.KeyDown += NameBox_KeyDown;
        _nameBox.LostFocus += (_, _) => CommitNameIfVisible();

        _menuSend.Click += async (_, _) => await SendFromMenuAsync();
        _menuCopyIp.Click += (_, _) =>
        {
            if (_menuTarget != null)
            {
                Clipboard.SetText(_menuTarget.Ip);
                AppendLog($"已复制 IP：{_menuTarget.Ip}", Color.Gray);
            }
        };
        _menuRemove.Click += (_, _) => RemoveSelectedPeer();
        _menuRefresh.Click += (_, _) =>
        {
            _discovery.BroadcastOnce();
            AppendLog("已重新广播本机信息，等待同网段设备回应", Color.FromArgb(180, 130, 20));
        };
        _menuManual.Click += (_, _) => ManualSendAsync().FireAndForget();

        // 配对入口：默认收起，点击才生成 token 并展开二维码
        _addDeviceBtn.Click += (_, _) => ExpandPairPanel();
        _webBtn.Click += (_, _) => OpenWebMode();
        _collapseQr.Click += (_, _) => CollapsePairPanel();
        _refreshQr.Click += (_, _) => RefreshPairing();
        _pairTimer.Tick += (_, _) => UpdatePairCountdown();
        _transfer.ConfirmPair += ConfirmPairAsync;
        _transfer.PeerPaired += OnPeerPaired;

        // 局域网自动发现：与扫码配对写入同一个列表
        _discovery.PeerChanged += (peer, change) => Ui(() => OnLanPeerChanged(peer, change));
        _discovery.Diag += msg => Ui(() => AppendLogDiag(msg));

        _transfer.Log += msg => Ui(() => AppendLog(msg, Color.FromArgb(40, 40, 40)));
        _transfer.ConfirmReceive += ConfirmReceiveAsync;
        _transfer.Progress += (dir, name, done, total) => Ui(() =>
        {
            var pct = total > 0 ? (int)(1000.0 * done / total) : 0;
            if (!_progress.Visible) _progress.Visible = true;
            _progress.Value = Math.Min(1000, Math.Max(0, pct));
            _progressLabel.Text = $"{dir} {name}  {TransferService.FormatSize(done)}/{TransferService.FormatSize(total)}  {pct / 10}%";
        });
        _transfer.TransferFinished += (dir, name, peer, size, ok, msg) => Ui(() =>
        {
            _progress.Visible = false;
            if (ok)
            {
                AppendLog($"✓ {dir} {name} 完成", Color.FromArgb(25, 140, 60));
                _progress.Value = 1000;
                _progressLabel.Text = $"{dir} {name} 完成 · {TransferService.FormatSize(size)}";
                // 接收成功的 msg 就是落盘路径；发送在 SendFilesToAsync 里单独登记
                if (dir == "收到" && msg.Length > 0) AddRecent(msg, name, size, true);
            }
            else
            {
                AppendLog($"✗ {dir} {name} 失败：{msg}", Color.FromArgb(190, 40, 40));
                _progressLabel.Text = $"{dir} {name} 失败";
            }
        });

        _menuOpenFolder.Click += (_, _) => OpenReceiveFolder();

        RefreshRecent();
        UpdateDeviceEmptyState();

        FormClosing += (_, e) =>
        {
            // 关闭键只收进托盘：传输服务继续跑，随时还能接收文件
            if (!_reallyExit)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            _tray.Visible = false;
            _tray.Dispose();
            _pairTimer.Stop();
            _pairTimer.Dispose();
            _webForm?.ForceClose();
            _webForm = null;
            _web?.Dispose();     // 停掉网页服务，浏览器访客全部断开
            _web = null;
            _discovery.Dispose();   // 会先发一个 BYE，让对方立刻把本机从列表里摘掉
            _transfer.Dispose();
        };
    }

    /// <summary>收到发文件请求：在 UI 线程弹确认框（可选另存位置），用户点「接收」才返回同意。</summary>
    private Task<ReceiveDecision> ConfirmReceiveAsync(IncomingRequest req)
    {
        var tcs = new TaskCompletionSource<ReceiveDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui(() =>
        {
            try
            {
                // 收在托盘里也要能把确认框弹出来，否则对方会一直等到超时
                if (!Visible) ShowFromTray();
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                using var dlg = new ReceiveConfirmDialog(req) { Owner = this };
                var accept = dlg.ShowDialog(this) == DialogResult.OK;
                if (accept)
                    AppendLog($"已接受接收：{req.FileName} → {dlg.SavePath}", Color.FromArgb(25, 140, 60));
                else
                    AppendLog($"已拒绝接收：{req.FileName}（来自 {req.RemoteIp}，已通知对方）", Color.FromArgb(190, 40, 40));
                tcs.TrySetResult(new ReceiveDecision(accept, accept ? dlg.SavePath : null));
            }
            catch (Exception ex)
            {
                AppendLog($"确认框异常：{ex.Message}", Color.FromArgb(190, 40, 40));
                tcs.TrySetResult(new ReceiveDecision(false));
            }
        });
        return tcs.Task;
    }

    // ---------------- 二维码配对 ----------------

    /// <summary>展开唯一入口：此刻才生成 token / 二维码，10 分钟从这一刻起算。</summary>
    private void ExpandPairPanel()
    {
        if (_pairExpanded) return;

        _localIp = AppConfig.GetLocalIp();
        _localInfo.Text = $"{_localIp}:{_config.TcpPort}   ·   广播发现 + 扫码配对   ·   点击名称可修改";

        var session = _transfer.BeginPairing();
        _pairExpanded = true;
        _addDeviceBtn.Visible = false;
        if (_entryRowStyle != null) _entryRowStyle.Height = 0F;
        if (_pairRowStyle != null) _pairRowStyle.Height = D(158);
        _pairPanel.Visible = true;

        _pairAddr.Text = $"{_localIp}:{_config.TcpPort}";
        _tip.SetToolTip(_pairAddr, $"{_localIp}:{_config.TcpPort}");
        _refreshQr.Text = "刷新二维码";
        SetExpiryProgress(1, Color.FromArgb(30, 111, 217));
        _expiryLabel.ForeColor = Color.FromArgb(30, 111, 217);
        RenderQr(session);
        _pairTimer.Start();
        UpdatePairCountdown();
        _leftGrid?.PerformLayout();

        AppendLog("已生成配对二维码（10 分钟有效），等待对方扫码…", Color.FromArgb(25, 100, 180));
    }

    /// <summary>刷新 = 立即作废旧 token 并生成新码。</summary>
    private void RefreshPairing()
    {
        if (!_pairExpanded)
        {
            ExpandPairPanel();
            return;
        }

        _localIp = AppConfig.GetLocalIp();
        var session = _transfer.BeginPairing();
        RenderQr(session);
        _pairAddr.Text = $"{_localIp}:{_config.TcpPort}";
        _tip.SetToolTip(_pairAddr, $"{_localIp}:{_config.TcpPort}");
        _expiryLabel.ForeColor = Color.FromArgb(30, 111, 217);
        _refreshQr.Text = "刷新二维码";
        SetExpiryProgress(1, Color.FromArgb(30, 111, 217));
        _pairTimer.Start();
        UpdatePairCountdown();
        AppendLog("二维码已刷新，旧码立即失效", Color.FromArgb(180, 130, 20));
    }

    /// <summary>收起 = 作废 token，回到「只有一个入口」的默认态。</summary>
    private void CollapsePairPanel(bool silent = false)
    {
        if (!_pairExpanded) return;
        _pairExpanded = false;
        _pairTimer.Stop();
        _transfer.CancelPairing();

        _pairPanel.Visible = false;
        if (_pairRowStyle != null) _pairRowStyle.Height = 0F;
        if (_entryRowStyle != null) _entryRowStyle.Height = D(40);
        _addDeviceBtn.Visible = true;

        if (_qrBox.Image != null)
        {
            _qrBox.Image.Dispose();
            _qrBox.Image = null;
        }
        SetExpiryProgress(0, Color.FromArgb(30, 111, 217));
        _leftGrid?.PerformLayout();

        if (!silent) AppendLog("已收起二维码，旧码立即失效", Color.Gray);
    }

    private void UpdatePairCountdown()
    {
        if (!_pairExpanded) return;
        var session = _transfer.CurrentSession;
        if (session == null)
        {
            CollapsePairPanel(silent: true);
            return;
        }

        var left = session.Remaining;
        if (left <= TimeSpan.Zero)
        {
            _pairTimer.Stop();
            _expiryLabel.Text = "二维码已过期";
            _expiryLabel.ForeColor = Color.FromArgb(198, 40, 40);
            SetExpiryProgress(0, Color.FromArgb(198, 40, 40));
            _refreshQr.Text = "重新生成";
            AppendLog("配对二维码已过期，点「重新生成」获取新码", Color.FromArgb(180, 130, 20));
            return;
        }

        _expiryLabel.Text = $"剩余 {(int)left.TotalMinutes:00}:{left.Seconds:00}";
        _expiryLabel.ForeColor = left.TotalSeconds <= 60
            ? Color.FromArgb(184, 134, 11)
            : Color.FromArgb(30, 111, 217);
        SetExpiryProgress(
            left.TotalSeconds / 600.0,
            left.TotalSeconds <= 60 ? Color.FromArgb(184, 134, 11) : Color.FromArgb(30, 111, 217));
    }

    private void SetExpiryProgress(double fraction, Color color)
    {
        _expiryFraction = Math.Clamp(fraction, 0, 1);
        _expiryColor = color;
        _expiryTrack.Invalidate();
    }

    private void RenderQr(PairSession session)
    {
        var payload = Protocol.BuildQrPayload(_config.DeviceName, _localIp, _config.TcpPort, session.Token);
        var png = QrCode.RenderPng(payload);
        if (png == null)
        {
            AppendLog("二维码生成失败，请点「刷新二维码」重试", Color.FromArgb(190, 40, 40));
            return;
        }

        using var ms = new MemoryStream(png);
        using var tmp = Image.FromStream(ms);
        var image = new Bitmap(tmp);
        var old = _qrBox.Image;
        _qrBox.Image = image;
        old?.Dispose();
    }

    /// <summary>收到 FG_HELLO：在 UI 线程弹窗确认对方身份，用户允许才回 OK。</summary>
    private Task<bool> ConfirmPairAsync(PairRequest req)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui(() =>
        {
            try
            {
                if (!Visible) ShowFromTray();
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                using var dlg = new PairConfirmDialog(req) { Owner = this };
                var accept = dlg.ShowDialog(this) == DialogResult.OK;
                AppendLog(accept
                    ? $"已允许配对：{req.PeerName}（{req.Address}）"
                    : $"已拒绝配对：{req.PeerName}（已通知对方）",
                    accept ? Color.FromArgb(25, 140, 60) : Color.FromArgb(190, 40, 40));
                tcs.TrySetResult(accept);
            }
            catch (Exception ex)
            {
                AppendLog($"配对确认框异常：{ex.Message}", Color.FromArgb(190, 40, 40));
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
    }

    private void OnPeerPaired(Peer peer)
    {
        Ui(() =>
        {
            MergePairedPeer(peer);
            AppendLog($"✓ 配对成功：{peer.Name}（{peer.Address}）已加入设备列表", Color.FromArgb(25, 140, 60));
            _progressLabel.Text = $"已与 {peer.Name} 配对 · 右键设备即可发送文件";

            // 让「已连接」状态短暂可见，然后自动收起回默认的单一入口
            var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                if (!IsDisposed) CollapsePairPanel(silent: true);
            };
            timer.Start();
        });
    }

    // ---------------- 设备列表：局域网发现 + 扫码配对，两路来源合一 ----------------

    /// <summary>列表行里显示的状态文案：区分来源，配对过的优先显示配对状态。</summary>
    private static string StatusOf(Peer p)
    {
        if (p.JustPaired) return "刚配对";
        if (p.IsPaired && p.IsLan) return "已配对·在线";
        if (p.IsPaired) return "已配对";
        return "在线";
    }

    private static Color ColorOf(Peer p) =>
        p.IsPaired ? Color.FromArgb(30, 100, 180) : Color.FromArgb(40, 46, 54);

    /// <summary>
    /// 局域网广播事件。关键点：同一台设备可能既配对过、又在广播，
    /// 这时必须合并成一行（沿用配对时的地址，IPv6 优先），不能出现两行。
    /// </summary>
    private void OnLanPeerChanged(Peer lan, PeerChange change)
    {
        if (change == PeerChange.Removed)
        {
            // BYE 或超时：配对过的行保留，只把状态改回「已配对」；纯局域网来源才删行
            var row = _peers.TryGetValue(lan.Key, out var exact) ? exact : FindPairedTwin(lan);
            if (row == null) return;
            row.IsLan = false;
            if (row.IsPaired)
            {
                RenderPeerRow(row);
                AppendLog($"设备离线：{row.Name}（仍保留在已配对列表）", Color.FromArgb(180, 130, 20));
            }
            else
            {
                RemovePeerRow(row.Key);
                AppendLog($"设备离线：{row.Name}", Color.FromArgb(180, 130, 20));
            }
            return;
        }

        var twin = FindPairedTwin(lan);
        if (twin != null)
        {
            var wasOnline = twin.IsLan;
            twin.IsLan = true;
            twin.LastSeen = DateTime.Now;
            RenderPeerRow(twin);
            if (!wasOnline && change == PeerChange.Added)
                AppendLog($"设备在线：{twin.Name}（局域网已发现）", Color.FromArgb(25, 140, 60));
            return;
        }

        if (_peers.TryGetValue(lan.Key, out var slot))
        {
            var wasOnline = slot.IsLan;
            slot.Name = lan.Name;
            slot.Ip = lan.Ip;
            slot.TcpPort = lan.TcpPort;
            slot.LastSeen = DateTime.Now;
            slot.IsLan = true;
            RenderPeerRow(slot);
            if (!wasOnline) AppendLog($"设备上线：{slot.Name}（{slot.Ip}）", Color.FromArgb(25, 140, 60));
        }
        else
        {
            lan.IsLan = true;
            lan.LastSeen = DateTime.Now;
            _peers[lan.Key] = lan;
            AddPeerRow(lan);
            AppendLog($"发现设备：{lan.Name}（{lan.Ip}）", Color.FromArgb(180, 130, 20));
        }
    }

    /// <summary>
    /// 找「同一台设备」的已配对行：名字与端口都相同。
    /// 只有在同名的已配对设备恰好一台时才合并——两台同名手机（同型号默认名）不会被错误并成一行。
    /// </summary>
    private Peer? FindPairedTwin(Peer other)
    {
        var name = other.Name.Trim();
        var matches = _peers.Values
            .Where(p => p.IsPaired && p.TcpPort == other.TcpPort &&
                        string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>扫码配对成功后的入列。</summary>
    private void MergePairedPeer(Peer peer)
    {
        peer.IsPaired = true;
        peer.JustPaired = true;

        // 之前由广播发现的同名同端口行并进来，避免同一台设备两行
        var lanTwin = _peers.Values.FirstOrDefault(p =>
            !p.IsPaired && p.TcpPort == peer.TcpPort &&
            string.Equals(p.Name.Trim(), peer.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (lanTwin != null)
        {
            peer.IsLan = lanTwin.IsLan;
            _peers.Remove(lanTwin.Key);
            RemovePeerRow(lanTwin.Key);
        }

        _peers[peer.Key] = peer;
        if (FindItem(peer.Key) == null) AddPeerRow(peer, select: true);
        else RenderPeerRow(peer, select: true);

        // 「刚配对」只是短暂提示，5 秒后回到普通状态，避免状态列一直停在刚配对
        var badge = new System.Windows.Forms.Timer { Interval = 5000 };
        badge.Tick += (_, _) =>
        {
            badge.Stop();
            badge.Dispose();
            if (peer.JustPaired && !IsDisposed)
            {
                peer.JustPaired = false;
                RenderPeerRow(peer);
            }
        };
        badge.Start();
    }

    private void AddPeerRow(Peer peer, bool select = false)
    {
        var item = new ListViewItem(new[] { peer.Name, peer.Ip, peer.TcpPort.ToString(), StatusOf(peer) })
        {
            Tag = peer,
            ForeColor = ColorOf(peer),
            ToolTipText = $"{peer.Name}\n{peer.Address}"
        };
        // 配对行置顶，局域网行按发现顺序追加，避免每 2 秒广播把列表抖来抖去
        if (peer.IsPaired) _devices.Items.Insert(0, item);
        else _devices.Items.Add(item);
        if (select)
        {
            item.Selected = true;
            item.EnsureVisible();
        }
        UpdateDeviceEmptyState();
    }

    private void RenderPeerRow(Peer peer, bool select = false)
    {
        var item = FindItem(peer.Key);
        if (item == null) { AddPeerRow(peer, select); return; }
        item.SubItems[0].Text = peer.Name;
        item.SubItems[1].Text = peer.Ip;
        item.SubItems[2].Text = peer.TcpPort.ToString();
        item.SubItems[3].Text = StatusOf(peer);
        item.Tag = peer;
        item.ToolTipText = $"{peer.Name}\n{peer.Address}";
        item.ForeColor = ColorOf(peer);
        if (select) { item.Selected = true; item.EnsureVisible(); }
        UpdateDeviceEmptyState();
    }

    private void RemovePeerRow(string key)
    {
        var item = FindItem(key);
        if (item != null) _devices.Items.Remove(item);
        _peers.Remove(key);
        UpdateDeviceEmptyState();
    }

    private void UpdateDeviceEmptyState()
    {
        _emptyHint.Visible = _devices.Items.Count == 0;
        _devices.Visible = !_emptyHint.Visible;
    }

    private void RemoveSelectedPeer()
    {
        if (_devices.SelectedItems.Count == 0) return;
        var item = _devices.SelectedItems[0];
        var peer = item.Tag as Peer;
        var name = peer?.Name ?? item.SubItems[0].Text;
        var addr = peer?.Address ?? item.SubItems[1].Text;
        _devices.Items.Remove(item);
        if (peer != null) _peers.Remove(peer.Key);
        UpdateDeviceEmptyState();
        // 局域网来源的设备下一轮广播还会回来，说明清楚免得用户以为没生效
        if (peer != null && !peer.IsPaired)
            AppendLog($"已从列表移除：{name}（{addr}）· 仍在局域网广播时会被重新发现", Color.Gray);
        else if (peer != null)
            AppendLog($"已从列表移除：{name}（{addr}）· 需要重新扫码才能再加回来", Color.Gray);
    }

    private void DeviceMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 列表为空时不再取消菜单——否则「手动输入 IP 直连」永远点不到
        var sel = _devices.SelectedItems.Count > 0 ? _devices.SelectedItems[0].Tag as Peer : null;
        _menuTarget = sel;
        _menuSend.Enabled = sel != null;
        _menuCopyIp.Enabled = sel != null;
        _menuRemove.Enabled = sel != null;
        _menuSend.Text = sel != null ? $"发送文件到 {sel.Name}…" : "发送文件（先选中设备）";
        _menuRemove.Text = sel != null ? $"从列表移除 {sel.Name}" : "从列表移除";
        _menuRefresh.Enabled = true;
    }

    private async Task SendFromMenuAsync()
    {
        if (_menuTarget == null) return;
        using var dlg = new OpenFileDialog { Multiselect = true, Title = $"发送文件到 {_menuTarget.Name}", Filter = "所有文件|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var target = _menuTarget;
        await SendFilesToAsync(target, dlg.FileNames);
    }

    private async Task ManualSendAsync()
    {
        using var ipDlg = new InputDialog("手动直连", "输入对方电脑/手机 IP（支持 IPv6，可带 %网卡序号）：", _menuTarget?.Ip ?? "") { Owner = this };
        if (ipDlg.ShowDialog(this) != DialogResult.OK) return;
        var ip = ipDlg.Value.Trim().Trim('[', ']');
        if (string.IsNullOrWhiteSpace(ip)) return;
        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            MessageBox.Show(this, "IP 地址格式不正确", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        using var dlg = new OpenFileDialog { Multiselect = true, Title = $"发送文件到 {ip}", Filter = "所有文件|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var target = new Peer { Name = ip, Ip = ip, TcpPort = _config.TcpPort };
        await SendFilesToAsync(target, dlg.FileNames);
    }

    private async Task SendFilesToAsync(Peer target, string[] files)
    {
        foreach (var file in files)
        {
            _progress.Value = 0;
            AppendLog($"开始发送：{Path.GetFileName(file)} → {target.Name}", Color.FromArgb(30, 100, 180));
            var (ok, msg) = await _transfer.SendFileAsync(target, file);
            if (ok) AddRecent(file, Path.GetFileName(file), new FileInfo(file).Length, false);
            else AppendLog(msg, Color.FromArgb(190, 40, 40));
        }
    }

    // ---------------- 网页模式（浏览器访客） ----------------

    /// <summary>点「🌐 网页模式」= 进入独立页面，顺带把服务起起来（复用「点入口才启动」的哲学）。</summary>
    private void OpenWebMode()
    {
        EnsureWebService();
        StartWebService();

        if (_webForm == null || _webForm.IsDisposed)
        {
            _webForm = new WebModeForm(_web!, UpdateWebEntry);
            _webForm.FormClosed += (_, _) =>
            {
                _webForm = null;
                UpdateWebEntry();
            };
        }

        _webForm.Show();
        if (_webForm.WindowState == FormWindowState.Minimized) _webForm.WindowState = FormWindowState.Normal;
        _webForm.BringToFront();
        _webForm.Activate();
        UpdateWebEntry();
    }

    /// <summary>首次用到网页模式时才建服务并接线，没点过就不占端口、不占资源。</summary>
    private void EnsureWebService()
    {
        if (_web != null) return;

        _web = new WebHostService(_config);
        _web.Log += msg => Ui(() => AppendLog(msg, Color.FromArgb(30, 100, 180)));
        _web.Progress += (dir, name, peer, done, total) => Ui(() =>
        {
            var pct = total > 0 ? (int)(1000.0 * done / total) : 0;
            if (!_progress.Visible) _progress.Visible = true;
            _progress.Value = Math.Min(1000, Math.Max(0, pct));
            _progressLabel.Text = $"{dir} {name} {peer}  {TransferService.FormatSize(done)}/{TransferService.FormatSize(total)}  {pct / 10}%";
        });
        _web.Finished += (dir, name, peer, size, ok, msg) => Ui(() =>
        {
            _progress.Visible = false;
            if (ok)
            {
                AppendLog($"✓ {dir} {name}（{peer}）完成", Color.FromArgb(25, 140, 60));
                _progressLabel.Text = $"{dir} {name} 完成 · {TransferService.FormatSize(size)}";
                // 收到的 msg 就是落盘路径，与 P2P 接收同一套「最近传输」登记
                if (dir == "收到" && msg.Length > 0) AddRecent(msg, name, size, true);
            }
            else
            {
                AppendLog($"✗ {dir} {name}（{peer}）失败：{msg}", Color.FromArgb(190, 40, 40));
                _progressLabel.Text = $"{dir} {name} 失败";
            }
            UpdateWebEntry();
        });

        // 访客上传复用现有接收确认框：文件名/大小/来源 → 「接收…」→ 选保存位置 → 才开始收
        _web.ConfirmUpload += ConfirmReceiveAsync;
        _web.GuestsChanged += () => Ui(UpdateWebEntry);
    }

    private bool StartWebService()
    {
        if (_web == null) return false;
        if (_web.Running) return true;

        // 端口不落配置：每次启动随机抽一个可用的（见 WebHostService.RandomPort）
        var (ok, msg) = _web.Start();
        if (!ok)
        {
            AppendLog($"网页模式启动失败：{msg}", Color.FromArgb(190, 40, 40));
            MessageBox.Show(this,
                $"{msg}\n\n可以在网页模式窗口里点「换端口」再随机一个，或先关掉占用该端口的程序。",
                "网页模式", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        AppendLog(msg, Color.FromArgb(25, 140, 60));
        AppendLog("朋友连同一 WiFi / 热点，用浏览器打开该地址，输入 PIN 加入即可收发文件", Color.Gray);
        AppendLog("若朋友打不开页面，请确认 Windows 防火墙已允许本程序访问「专用网络」", Color.Gray);
        return true;
    }

    /// <summary>入口按钮始终显示服务与访客状态，关掉网页模式窗口后也能一眼看到还在跑。</summary>
    private void UpdateWebEntry()
    {
        var running = _web?.Running == true;
        var guests = running ? _web!.SnapshotGuests().Count : 0;
        _webBtn.Text = running
            ? (guests > 0 ? $"🌐  网页模式 · {guests} 人" : "🌐  网页模式 · 运行中")
            : "🌐  网页模式";
        _tip.SetToolTip(_webBtn, running
            ? $"服务运行中 · 端口 {_web!.Port} · 在线访客 {guests} 人\n点击打开网页模式窗口"
            : "让朋友用浏览器连进来收/发文件，不用装 App");
    }

    private void BeginEditName()
    {
        _nameLabel.Visible = false;
        _nameBox.Text = _config.DeviceName;
        _nameBox.Visible = true;
        _nameBox.BringToFront();
        _nameBox.Focus();
        _nameBox.SelectAll();
    }

    private void NameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { CommitNameIfVisible(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) { _nameBox.Visible = false; _nameLabel.Visible = true; }
    }

    private void CommitNameIfVisible()
    {
        if (!_nameBox.Visible) return;
        var newName = _nameBox.Text.Trim();
        if (newName.Length == 0) newName = Environment.MachineName;
        _config.DeviceName = newName;
        _config.Save();
        _nameLabel.Text = newName;
        _nameBox.Visible = false;
        _nameLabel.Visible = true;
        // 立刻广播一次新名字，同网段的设备不用等下一轮 2 秒广播
        try { _discovery.SetDeviceName(newName); } catch { }
        AppendLog($"设备名已改为「{newName}」", Color.FromArgb(180, 130, 20));
    }

    private ListViewItem? FindItem(string key) =>
        _devices.Items.Cast<ListViewItem>().FirstOrDefault(i => (i.Tag as Peer)?.Key == key);

    private void OpenReceiveFolder()
    {
        try
        {
            var dir = AppConfig.ReceiveDirectory;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex) { AppendLog($"打开收件夹失败：{ex.Message}", Color.FromArgb(190, 40, 40)); }
    }

    /// <summary>在资源管理器中选中该文件；文件已被移走时退回到打开其所在目录。</summary>
    private void OpenFileLocation(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
                AppendLog($"文件已不在原处，已打开所在目录：{dir}", Color.FromArgb(180, 130, 20));
                return;
            }

            AppendLog($"找不到文件：{path}", Color.FromArgb(190, 40, 40));
        }
        catch (Exception ex)
        {
            AppendLog($"打开位置失败：{ex.Message}", Color.FromArgb(190, 40, 40));
        }
    }

    /// <summary>登记一条最近传输（最新在前，最多 5 条），并立即刷新列表。</summary>
    private void AddRecent(string path, string name, long size, bool received)
    {
        _config.Recent.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        _config.Recent.Insert(0, new RecentItem
        {
            Path = path, Name = name, Size = size, Received = received, Time = DateTime.Now
        });
        while (_config.Recent.Count > MaxRecent) _config.Recent.RemoveAt(_config.Recent.Count - 1);
        _config.Save();
        Ui(RefreshRecent);
    }

    private const int MaxRecent = 5;

    private void RefreshRecent()
    {
        // 有的文件可能已被用户移走或删掉：仍然显示，点开时再提示找不到
        _recent.BeginUpdate();
        try
        {
            _recent.Items.Clear();
            foreach (var r in _config.Recent.Take(MaxRecent))
            {
                var item = new ListViewItem((r.Received ? "↓ " : "↑ ") + r.Name)
                {
                    Tag = r.Path,
                    ToolTipText = $"{r.Path}\n{r.Time:MM-dd HH:mm}",
                    ForeColor = r.Received ? Color.FromArgb(30, 100, 180) : Color.FromArgb(30, 120, 80)
                };
                item.SubItems.Add(TransferService.FormatSize(r.Size));
                item.SubItems.Add(r.Time.ToString("HH:mm"));
                _recent.Items.Add(item);
            }
        }
        finally { _recent.EndUpdate(); }

        _recentEmpty.Visible = _recent.Items.Count == 0;
        _recent.Visible = !_recentEmpty.Visible;
    }

    // ---------------- 托盘 ----------------

    private void BuildTray()
    {
        _tray.Text = $"飞鸽 P2P · {_config.DeviceName}";
        // 托盘按系统小图标尺寸取帧，150%/200% 缩放下也不会糊
        _tray.Icon = LoadAppIcon(SystemInformation.SmallIconSize) ?? SystemIcons.Application;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();

        var menu = new ContextMenuStrip { Font = new Font("微软雅黑", 9F) };
        menu.Items.Add(new ToolStripMenuItem("打开主界面", null, (_, _) => ShowFromTray()) { Font = new Font("微软雅黑", 9F, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("网页模式…", null, (_, _) => OpenWebMode()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出飞鸽 P2P", null, (_, _) => ExitApp()));
        _tray.ContextMenuStrip = menu;
    }

    private void HideToTray()
    {
        Hide();
        if (_trayHintShown) return;
        _trayHintShown = true;
        _tray.ShowBalloonTip(3000, "飞鸽 P2P 仍在后台运行",
            "点右上角关闭只是收进托盘，仍可接收文件。要彻底退出请右键托盘图标选「退出」。", ToolTipIcon.Info);
        AppendLog("窗口已收进托盘，仍可接收文件；彻底退出请右键托盘图标选「退出」", Color.FromArgb(180, 130, 20));
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        Close();
    }

    /// <summary>从内嵌的 app.ico 里取最贴近目标尺寸的一帧；标题栏、托盘、任务栏共用同一份图标。</summary>
    private static Icon? LoadAppIcon(Size size)
    {
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico");
            return stream == null ? null : new Icon(stream, size);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载图标失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>发现通道的诊断日志：同一句 8 秒内只记一次，否则每 2 秒一条会刷屏。</summary>
    private void AppendLogDiag(string msg)
    {
        var now = DateTime.Now;
        if (msg == _lastDiag && (now - _lastDiagTime).TotalSeconds < 8) return;
        _lastDiag = msg;
        _lastDiagTime = now;
        AppendLog("· " + msg, Color.FromArgb(160, 165, 172));
    }

    private void AppendLog(string msg, Color color)
    {
        Ui(() =>
        {
            var now = DateTime.Now;
            if (msg == _lastLog && (now - _lastLogTime).TotalMilliseconds < 500) return;
            _lastLog = msg;
            _lastLogTime = now;
            _log.SelectionStart = _log.TextLength;
            _log.SelectionColor = color;
            _log.AppendText($"[{now:HH:mm:ss}] {msg}{Environment.NewLine}");
            ScrollLogToEnd();
        });
    }

    private void Ui(Action action)
    {
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}

/// <summary>简单的文本输入对话框（手动 IP 直连用）。</summary>
internal sealed class InputDialog : Form
{
    private readonly TextBox _box = new();
    public string Value => _box.Text;

    public InputDialog(string title, string label, string initial)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(360, 130);
        MaximizeBox = false; MinimizeBox = false;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(14) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        root.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, AutoSize = true }, 0, 0);
        _box.Text = initial;
        _box.Dock = DockStyle.Fill;
        root.Controls.Add(_box, 0, 1);
        var btns = new Panel { Dock = DockStyle.Fill };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = 80 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Width = 80 };
        btns.Controls.Add(ok);
        btns.Controls.Add(cancel);
        root.Controls.Add(btns, 0, 2);
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/// <summary>
/// 接收确认对话框：显示文件名/大小/来源，点「接收」即弹出保存位置选择，选好才开始接收。
/// 所有行高都由实测文字尺寸决定（AutoSize），并关闭 WinForms 的字体坐标缩放，
/// 这样在 125%/150% 缩放下也不会出现文字互相压叠、右侧被裁的情况。
/// 60 秒不操作按拒绝处理并通知对方。
/// </summary>
internal sealed class ReceiveConfirmDialog : Form
{
    private const int ConfirmSeconds = 60;
    private const int TextWidth = 420;      // 文本折行宽度，窗口宽度由此确定
    private static string? _lastDir;        // 本次运行内记住上次选的目录

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Label _countdown = new();
    private readonly IncomingRequest _req;
    private int _left = ConfirmSeconds;
    private string _savePath;

    /// <summary>用户选定的保存路径（点「接收」并选好位置后才有意义）。</summary>
    public string SavePath => _savePath;

    public ReceiveConfirmDialog(IncomingRequest req)
    {
        _req = req;
        _savePath = Path.Combine(AppConfig.ReceiveDirectory, SafeFileName(req.FileName));

        Text = "收到文件";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = Color.White;
        Font = new Font("微软雅黑", 9.5F);
        // 不让 WinForms 按字体比例缩放坐标（那会把行高缩掉、文字叠在一起）；
        // 窗口尺寸改为按内容实测尺寸算，缩放下自然对得上。
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = false;

        var body = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 5,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 20, 24, 18),
            BackColor = Color.White
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var i = 0; i < 5; i++) body.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        body.Controls.Add(new Label
        {
            Text = "收到文件",
            Font = new Font("微软雅黑", 13F, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 32, 38),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);

        body.Controls.Add(new Label
        {
            Text = req.FileName,
            Font = new Font("微软雅黑", 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 100, 180),
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),   // 长文件名折行，不再被裁
            Margin = new Padding(0, 0, 0, 4)
        }, 0, 1);

        body.Controls.Add(new Label
        {
            Text = $"{TransferService.FormatSize(req.Size)}   ·   来自 {req.RemoteIp}",
            ForeColor = Color.FromArgb(120, 128, 138),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 16)
        }, 0, 2);

        _countdown.Text = $"点「接收」后选择保存位置；{ConfirmSeconds} 秒内未操作将自动拒绝并通知对方";
        _countdown.ForeColor = Color.FromArgb(170, 135, 40);
        _countdown.AutoSize = true;
        _countdown.MaximumSize = new Size(TextWidth, 0);
        _countdown.Margin = new Padding(0, 0, 0, 16);
        body.Controls.Add(_countdown, 0, 3);

        var btnFont = new Font("微软雅黑", 9.5F);
        var acceptFont = new Font("微软雅黑", 9.5F, FontStyle.Bold);
        var btnHeight = Math.Max(34, TextRenderer.MeasureText("接收…", acceptFont).Height + 14);
        var acceptSize = new Size(TextRenderer.MeasureText("接收…", acceptFont).Width + 40, btnHeight);
        var rejectSize = new Size(TextRenderer.MeasureText("拒绝", btnFont).Width + 40, btnHeight);

        var acceptBtn = new Button
        {
            Text = "接收…",
            Size = acceptSize,
            BackColor = Color.FromArgb(30, 111, 217),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = acceptFont,
            Cursor = Cursors.Hand,
            Margin = new Padding(8, 0, 0, 0)
        };
        acceptBtn.FlatAppearance.BorderSize = 0;
        acceptBtn.Click += (_, _) => AcceptWithLocation();

        var rejectBtn = new Button
        {
            Text = "拒绝",
            Size = rejectSize,
            BackColor = Color.FromArgb(245, 246, 248),
            ForeColor = Color.FromArgb(70, 78, 88),
            FlatStyle = FlatStyle.Flat,
            Font = btnFont,
            Cursor = Cursors.Hand,
            Margin = Padding.Empty
        };
        rejectBtn.FlatAppearance.BorderColor = Color.FromArgb(214, 220, 228);
        rejectBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var btns = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty
        };
        btns.Controls.Add(acceptBtn);
        btns.Controls.Add(rejectBtn);
        body.Controls.Add(btns, 0, 4);

        Controls.Add(body);
        AcceptButton = acceptBtn;
        CancelButton = rejectBtn;

        // 窗口尺寸 = 内容实测尺寸：文字多大，窗口就多大，不会挤在一起或右侧被裁
        var wanted = body.PreferredSize;
        ClientSize = new Size(Math.Max(440, wanted.Width), wanted.Height);

        _timer.Tick += (_, _) =>
        {
            _left--;
            if (_left <= 0)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            _countdown.Text = $"点「接收」后选择保存位置；{_left} 秒内未操作将自动拒绝并通知对方";
        };
        _timer.Start();
        FormClosed += (_, _) => { _timer.Stop(); _timer.Dispose(); };
    }

    /// <summary>「接收」= 确认 + 选位置：选好路径才真正开始接收；取消选择退回确认状态（不算拒绝）。</summary>
    private void AcceptWithLocation()
    {
        _timer.Stop();  // 正在挑目录，不算超时
        try
        {
            var startDir = (!string.IsNullOrEmpty(_lastDir) && Directory.Exists(_lastDir))
                ? _lastDir!
                : AppConfig.ReceiveDirectory;

            using var dlg = new SaveFileDialog
            {
                Title = "选择保存位置",
                FileName = Path.GetFileName(_savePath),
                InitialDirectory = startDir,
                Filter = "所有文件|*.*",
                OverwritePrompt = true,
                CheckPathExists = true
            };

            if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.FileName))
            {
                _savePath = dlg.FileName;
                _lastDir = Path.GetDirectoryName(_savePath);
                DialogResult = DialogResult.OK;     // 关闭确认框，MainForm 用 SavePath 开始接收
                return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"选择保存位置失败：{ex.Message}", "飞鸽 P2P",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        _timer.Start();     // 取消选择：继续等用户决定
    }

    /// <summary>把对端发来的文件名里不合法的字符替换掉，避免保存对话框直接报错。</summary>
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim().Trim('.');
        return s.Length == 0 ? "received_file" : s;
    }
}

/// <summary>
/// 配对确认框：收到 FG_HELLO 后显示对方设备名与地址，用户点「允许配对」才建立信任。
/// 与「是否接收这个文件」是两层独立确认。
/// </summary>
internal sealed class PairConfirmDialog : Form
{
    private const int TextWidth = 420;

    public PairConfirmDialog(PairRequest req)
    {
        Text = "设备请求配对";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = Color.White;
        Font = new Font("微软雅黑", 9.5F);
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = false;

        var body = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 5,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 20, 24, 18),
            BackColor = Color.White
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var i = 0; i < 5; i++) body.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        body.Controls.Add(new Label
        {
            Text = "设备请求配对",
            Font = new Font("微软雅黑", 13F, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 32, 38),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);

        body.Controls.Add(new Label
        {
            Text = req.PeerName,
            Font = new Font("微软雅黑", 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 100, 180),
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            Margin = new Padding(0, 0, 0, 4)
        }, 0, 1);

        body.Controls.Add(new Label
        {
            Text = $"{req.Address}\n\n配对后这台设备会加入你的设备列表，之后可从列表右键给它发送文件。",
            ForeColor = Color.FromArgb(110, 118, 128),
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            Margin = new Padding(0, 0, 0, 14)
        }, 0, 2);

        body.Controls.Add(new Label
        {
            Text = "30 秒内未操作将自动拒绝并通知对方",
            ForeColor = Color.FromArgb(170, 135, 40),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 14)
        }, 0, 3);

        var btnFont = new Font("微软雅黑", 9.5F);
        var allowFont = new Font("微软雅黑", 9.5F, FontStyle.Bold);
        var btnHeight = Math.Max(34, TextRenderer.MeasureText("允许配对", allowFont).Height + 14);
        var allowSize = new Size(TextRenderer.MeasureText("允许配对", allowFont).Width + 40, btnHeight);
        var denySize = new Size(TextRenderer.MeasureText("拒绝", btnFont).Width + 40, btnHeight);

        var allowBtn = new Button
        {
            Text = "允许配对",
            Size = allowSize,
            BackColor = Color.FromArgb(30, 111, 217),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = allowFont,
            Cursor = Cursors.Hand,
            DialogResult = DialogResult.OK,
            Margin = new Padding(8, 0, 0, 0)
        };
        allowBtn.FlatAppearance.BorderSize = 0;

        var denyBtn = new Button
        {
            Text = "拒绝",
            Size = denySize,
            BackColor = Color.FromArgb(245, 246, 248),
            ForeColor = Color.FromArgb(70, 78, 88),
            FlatStyle = FlatStyle.Flat,
            Font = btnFont,
            Cursor = Cursors.Hand,
            DialogResult = DialogResult.Cancel,
            Margin = Padding.Empty
        };
        denyBtn.FlatAppearance.BorderColor = Color.FromArgb(214, 220, 228);

        var btns = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty
        };
        btns.Controls.Add(allowBtn);
        btns.Controls.Add(denyBtn);
        body.Controls.Add(btns, 0, 4);

        Controls.Add(body);
        AcceptButton = allowBtn;
        CancelButton = denyBtn;

        var wanted = body.PreferredSize;
        ClientSize = new Size(Math.Max(440, wanted.Width), wanted.Height);
    }
}

internal static class FireAndForgetExt
{
    public static async void FireAndForget(this Task t)
    {
        try { await t; } catch { }
    }
}
