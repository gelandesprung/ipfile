using System.Drawing;
using System.Windows.Forms;

namespace FeigeP2P;

public sealed class MainForm : Form
{
    private readonly AppConfig _config;
    private readonly DiscoveryService _discovery;
    private readonly TransferService _transfer;

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
    private readonly ToolStripMenuItem _menuRefresh;
    private readonly ToolStripMenuItem _menuManual;
    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _menuOpenFolder = new("打开收件夹");

    /// <summary>真正的退出标志：点窗口关闭键只是收进托盘，只有托盘「退出」才结束进程。</summary>
    private bool _reallyExit;
    private bool _trayHintShown;

    private Peer? _menuTarget;
    private string _lastLog = "";
    private DateTime _lastLogTime = DateTime.MinValue;

    public MainForm()
    {
        _config = AppConfig.Load();
        _discovery = new DiscoveryService(_config);
        _transfer = new TransferService(_config);

        _menuSend = new ToolStripMenuItem("发送文件…");
        _menuCopyIp = new ToolStripMenuItem("复制 IP 地址");
        _menuRefresh = new ToolStripMenuItem("刷新设备");
        _menuManual = new ToolStripMenuItem("手动输入 IP 直连…");

        BuildUi();
        WireEvents();
        BuildTray();

        // 两个服务分别启动：任一失败都要给出明确原因，而不是整个程序闪退
        string? tcpError = null, udpError = null;
        try { _transfer.Start(); }
        catch (Exception ex) { tcpError = ex.Message; }

        try { _discovery.Start(); }
        catch (Exception ex) { udpError = ex.Message; }

        AppendLog($"已启动 · 本机 {_config.DeviceName} ({_discovery.LocalIp}:{_config.TcpPort})", Color.FromArgb(25, 100, 180));

        if (tcpError == null)
            AppendLog($"接收服务已就绪 · 监听 0.0.0.0:{_config.TcpPort}，等待对方发送", Color.FromArgb(25, 140, 60));
        else
            AppendLog($"接收服务启动失败：{tcpError}（端口可能被占用，或已有飞鸽在运行）", Color.FromArgb(190, 40, 40));

        if (udpError != null)
            AppendLog($"设备发现启动失败：{udpError}", Color.FromArgb(190, 40, 40));

        AppendLog("右键在线设备发送文件；列表为空时右键空白处可用「手动输入 IP 直连」", Color.Gray);
        AppendLog("若对方连得上却看不到本机，请确认 Windows 防火墙已允许本程序访问「专用网络」", Color.Gray);
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

        _localInfo.Text = $"{_discovery.LocalIp}:{_config.TcpPort}   ·   协议 v1.0   ·   点击名称可修改";
        _localInfo.ForeColor = Color.FromArgb(130, 138, 148);
        _localInfo.Font = new Font("微软雅黑", 9F);
        _localInfo.AutoSize = true;

        _onlineDot.Text = "● 在线";
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
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = new Padding(0, 0, 6, 0), Padding = Padding.Empty, BackColor = Color.White
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

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
            Text = "在线设备", Font = new Font("微软雅黑", 11F, FontStyle.Bold),
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

        _devices.View = View.Details;
        _devices.FullRowSelect = true;
        _devices.MultiSelect = false;
        _devices.Dock = DockStyle.Fill;
        _devices.BorderStyle = BorderStyle.None;
        _devices.BackColor = Color.White;
        _devices.Font = new Font("微软雅黑", 9.5F);
        _devices.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _devices.ContextMenuStrip = _deviceMenu;
        _devices.Columns.Add("设备名", 180);
        _devices.Columns.Add("IP 地址", 140);
        _devices.Columns.Add("端口", 70);
        _deviceMenu.Items.AddRange(new ToolStripItem[]
        {
            _menuSend, _menuCopyIp, new ToolStripSeparator(), _menuOpenFolder, _menuManual, _menuRefresh
        });
        _deviceMenu.Opening += DeviceMenu_Opening;

        _emptyHint.Text = "暂无在线设备\n\n启动手机 App 或另一台电脑，稍候将自动出现在此处";
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
        left.Controls.Add(listHost, 0, 1);

        // 列宽按列表实际宽度分配（用 SizeChanged 而不是窗体 Resize，避免拿到未布局的宽度）
        void FitColumns()
        {
            int w = Math.Max(_devices.ClientSize.Width - 4, 200);
            _devices.Columns[0].Width = (int)(w * 0.48);
            _devices.Columns[1].Width = (int)(w * 0.36);
            _devices.Columns[2].Width = w - _devices.Columns[0].Width - _devices.Columns[1].Width;
        }
        _devices.SizeChanged += (_, _) => FitColumns();
        FitColumns();
        return left;
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
        _progressLabel.Text = "就绪  ·  右键在线设备即可发送文件";
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
        _menuRefresh.Click += (_, _) =>
        {
            _discovery.BroadcastOnce();
            AppendLog("已重新广播本机信息", Color.FromArgb(180, 130, 20));
        };
        _menuManual.Click += (_, _) => ManualSendAsync().FireAndForget();

        _discovery.PeerChanged += (peer, change) => Ui(() =>
        {
            var existing = FindItem(peer.Key);
            switch (change)
            {
                case PeerChange.Added:
                    if (existing == null)
                    {
                        _devices.Items.Add(new ListViewItem(new[] { peer.Name, peer.Ip, peer.TcpPort.ToString() }) { Tag = peer });
                        AppendLog($"发现设备：{peer.Name} ({peer.Ip})", Color.FromArgb(180, 130, 20));
                    }
                    else
                    {
                        existing.SubItems[0].Text = peer.Name;
                        existing.Tag = peer;
                    }
                    break;

                case PeerChange.Updated:
                    // 周期性广播只是刷新，绝不能删行（这正是「设备上线就消失」的根因）
                    if (existing != null)
                    {
                        existing.SubItems[0].Text = peer.Name;
                        existing.SubItems[1].Text = peer.Ip;
                        existing.SubItems[2].Text = peer.TcpPort.ToString();
                        existing.Tag = peer;
                    }
                    break;

                case PeerChange.Removed:
                    if (existing != null)
                    {
                        _devices.Items.Remove(existing);
                        AppendLog($"设备离线：{peer.Name}", Color.FromArgb(180, 130, 20));
                    }
                    break;
            }
            // 空状态与列表互斥显示：透明标签叠在 ListView 上在部分环境画不出来
            _emptyHint.Visible = _devices.Items.Count == 0;
            _devices.Visible = !_emptyHint.Visible;
        });

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
            _discovery.Dispose();
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

    private void DeviceMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 列表为空时不再取消菜单——否则「手动输入 IP 直连」永远点不到
        var sel = _devices.SelectedItems.Count > 0 ? _devices.SelectedItems[0].Tag as Peer : null;
        _menuTarget = sel;
        _menuSend.Enabled = sel != null;
        _menuCopyIp.Enabled = sel != null;
        _menuSend.Text = sel != null ? $"发送文件到 {sel.Name}…" : "发送文件（先选中设备）";
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
        using var ipDlg = new InputDialog("手动直连", "输入对方电脑/手机的局域网 IP 地址：", _menuTarget?.Ip ?? "") { Owner = this };
        if (ipDlg.ShowDialog(this) != DialogResult.OK) return;
        var ip = ipDlg.Value.Trim();
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
        _discovery.SetDeviceName(newName);
        _nameLabel.Text = newName;
        _nameBox.Visible = false;
        _nameLabel.Visible = true;
        AppendLog($"设备名已改为「{newName}」，已通知局域网", Color.FromArgb(180, 130, 20));
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

    // 发现诊断日志：相同内容 8 秒内只记一次，避免周期性广播刷屏
    private string _lastDiag = "";
    private DateTime _lastDiagTime = DateTime.MinValue;
    private void AppendLogDiag(string msg)
    {
        var now = DateTime.Now;
        if (msg == _lastDiag && (now - _lastDiagTime).TotalSeconds < 8) return;
        _lastDiag = msg;
        _lastDiagTime = now;
        AppendLog("· " + msg, Color.FromArgb(160, 165, 172));
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

internal static class FireAndForgetExt
{
    public static async void FireAndForget(this Task t)
    {
        try { await t; } catch { }
    }
}
