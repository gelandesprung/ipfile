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
        Size = new Size(1040, 680);
        MinimumSize = new Size(820, 540);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 246, 248);
        Font = new Font("微软雅黑", 9F);

        // 根容器：3 行（顶部信息 / 主体 / 底部进度）
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = Padding.Empty, Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
        Controls.Add(root);

        root.Controls.Add(BuildTop(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        root.Controls.Add(BuildBottom(), 0, 2);

        // 顶部条高度按字体实测结果计算，高 DPI 下也不会把名称压到副标题上
        var topHeight = _nameLabel.PreferredHeight + _localInfo.PreferredHeight + 28;
        root.RowStyles[0].Height = Math.Max(72, topHeight);
        root.PerformLayout();
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
        const int padX = 22;
        var right = host.ClientSize.Width - padX;

        _onlineDot.Location = new Point(
            Math.Max(padX, right - _onlineDot.Width),
            Math.Max(0, (host.ClientSize.Height - _onlineDot.Height) / 2));

        if (_nameBox.Visible)
        {
            var maxW = Math.Max(160, _onlineDot.Left - padX - 20);
            _nameBox.Width = Math.Min(320, maxW);
            _nameBox.Location = new Point(padX, Math.Max(8, (host.ClientSize.Height - _nameBox.Height) / 2));
        }
        else
        {
            _nameLabel.Location = new Point(padX, 10);
            _localInfo.Location = new Point(padX, _nameLabel.Bottom + 2);
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
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Margin = new Padding(0, 0, 6, 0), Padding = Padding.Empty, BackColor = Color.White
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));

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
            _menuSend, _menuCopyIp, new ToolStripSeparator(), _menuManual, _menuRefresh
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

        var folderBar = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.White, Padding = new Padding(12, 7, 12, 7) };
        var openFolder = new Button
        {
            Text = "打开收件夹", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(246, 249, 253), ForeColor = Color.FromArgb(40, 96, 168),
            Font = new Font("微软雅黑", 9.5F), Cursor = Cursors.Hand
        };
        openFolder.FlatAppearance.BorderColor = Color.FromArgb(206, 218, 234);
        openFolder.Click += (_, _) => OpenReceiveFolder();
        folderBar.Controls.Add(openFolder);
        left.Controls.Add(folderBar, 0, 2);

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
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = new Padding(6, 0, 0, 0), Padding = Padding.Empty, BackColor = Color.White
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, Padding = new Padding(14, 0, 14, 0)
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var logTitle = new Label
        {
            Text = "传输日志", Font = new Font("微软雅黑", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 46, 54), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
        };
        _clearLogBtn.Text = "清空";
        _clearLogBtn.Font = new Font("微软雅黑", 8.5F);
        _clearLogBtn.ForeColor = Color.FromArgb(120, 128, 138);
        _clearLogBtn.FlatStyle = FlatStyle.Flat;
        _clearLogBtn.FlatAppearance.BorderSize = 0;
        _clearLogBtn.BackColor = Color.Transparent;
        _clearLogBtn.Dock = DockStyle.Fill;
        _clearLogBtn.Click += (_, _) => _log.Clear();
        head.Controls.Add(logTitle, 0, 0);
        head.Controls.Add(_clearLogBtn, 1, 0);
        right.Controls.Add(head, 0, 0);

        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Color.White;
        _log.Font = new Font("Consolas", 9.5F);
        _log.Margin = new Padding(10, 0, 10, 8);
        right.Controls.Add(_log, 0, 1);
        return right;
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
            _emptyHint.Visible = _devices.Items.Count == 0;
        });

        _discovery.Diag += msg => Ui(() => AppendLogDiag(msg));
        _transfer.Log += msg => Ui(() => AppendLog(msg, Color.FromArgb(40, 40, 40)));
        _transfer.ConfirmReceive += ConfirmReceiveAsync;
        _transfer.Progress += (dir, name, done, total) => Ui(() =>
        {
            var pct = total > 0 ? (int)(1000.0 * done / total) : 0;
            _progress.Value = Math.Min(1000, Math.Max(0, pct));
            _progressLabel.Text = $"{dir} {name}  {TransferService.FormatSize(done)}/{TransferService.FormatSize(total)}  {pct / 10}%";
        });
        _transfer.TransferFinished += (dir, name, peer, size, ok, msg) => Ui(() =>
        {
            if (ok)
            {
                AppendLog($"✓ {dir} {name} 完成", Color.FromArgb(25, 140, 60));
                _progress.Value = 1000;
                _progressLabel.Text = $"{dir} {name} 完成 · {TransferService.FormatSize(size)}";
            }
            else
            {
                AppendLog($"✗ {dir} {name} 失败：{msg}", Color.FromArgb(190, 40, 40));
                _progressLabel.Text = $"{dir} {name} 失败";
            }
        });

        FormClosing += (_, _) => { _discovery.Dispose(); _transfer.Dispose(); };
    }

    /// <summary>收到发文件请求：在 UI 线程弹确认框（可选另存位置），用户点「接收」才返回同意。</summary>
    private Task<ReceiveDecision> ConfirmReceiveAsync(IncomingRequest req)
    {
        var tcs = new TaskCompletionSource<ReceiveDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui(() =>
        {
            try
            {
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
            if (!ok) AppendLog(msg, Color.FromArgb(190, 40, 40));
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
            _log.ScrollToCaret();
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

/// <summary>接收确认对话框：显示文件名/大小/来源，可选保存位置；60 秒不操作自动拒绝。</summary>
internal sealed class ReceiveConfirmDialog : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Label _countdown = new();
    private readonly Label _pathLabel = new();
    private readonly IncomingRequest _req;
    private int _left = 60;
    private string _savePath;

    /// <summary>用户选定的保存路径（默认即收件夹下的同名文件）。</summary>
    public string SavePath => _savePath;

    public ReceiveConfirmDialog(IncomingRequest req)
    {
        _req = req;
        _savePath = Path.Combine(AppConfig.ReceiveDirectory, SafeFileName(req.FileName));

        Text = "收到文件";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 300);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.White;
        Font = new Font("微软雅黑", 9.5F);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(22, 18, 22, 14)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));

        body.Controls.Add(new Label
        {
            Text = "收到文件",
            Font = new Font("微软雅黑", 13F, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 32, 38),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        body.Controls.Add(new Label
        {
            Text = req.FileName,
            Font = new Font("微软雅黑", 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 100, 180),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        }, 0, 1);

        body.Controls.Add(new Label
        {
            Text = $"{TransferService.FormatSize(req.Size)}   ·   来自 {req.RemoteIp}",
            ForeColor = Color.FromArgb(120, 128, 138),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 2);

        // 保存位置：默认收件夹，可点「选择…」另存
        var saveRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
            Margin = new Padding(0), Padding = new Padding(0)
        };
        saveRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        saveRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82F));
        saveRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 18F));
        saveRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));

        var saveTitle = new Label
        {
            Text = "保存到",
            Font = new Font("微软雅黑", 8.5F),
            ForeColor = Color.FromArgb(140, 148, 158),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        saveRow.Controls.Add(saveTitle, 0, 0);
        saveRow.SetColumnSpan(saveTitle, 2);

        _pathLabel.Text = _savePath;
        _pathLabel.ForeColor = Color.FromArgb(60, 68, 78);
        _pathLabel.Dock = DockStyle.Fill;
        _pathLabel.TextAlign = ContentAlignment.MiddleLeft;
        _pathLabel.AutoEllipsis = true;
        saveRow.Controls.Add(_pathLabel, 0, 1);

        var pickBtn = new Button
        {
            Text = "选择…",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(246, 249, 253),
            ForeColor = Color.FromArgb(40, 96, 168),
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 0, 0, 0)
        };
        pickBtn.FlatAppearance.BorderColor = Color.FromArgb(206, 218, 234);
        pickBtn.Click += (_, _) => PickPath();
        saveRow.Controls.Add(pickBtn, 1, 1);
        body.Controls.Add(saveRow, 0, 3);

        _countdown.Text = "若 60 秒内未选择，将自动拒绝并通知对方";
        _countdown.ForeColor = Color.FromArgb(170, 135, 40);
        _countdown.Dock = DockStyle.Fill;
        _countdown.TextAlign = ContentAlignment.MiddleLeft;
        body.Controls.Add(_countdown, 0, 4);

        var btns = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            WrapContents = false
        };
        var acceptBtn = new Button
        {
            Text = "接收",
            DialogResult = DialogResult.OK,
            Size = new Size(96, 32),
            BackColor = Color.FromArgb(30, 111, 217),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("微软雅黑", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(8, 4, 0, 0)
        };
        acceptBtn.FlatAppearance.BorderSize = 0;
        var rejectBtn = new Button
        {
            Text = "拒绝",
            DialogResult = DialogResult.Cancel,
            Size = new Size(88, 32),
            BackColor = Color.FromArgb(245, 246, 248),
            ForeColor = Color.FromArgb(70, 78, 88),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 4, 0, 0)
        };
        rejectBtn.FlatAppearance.BorderColor = Color.FromArgb(214, 220, 228);
        btns.Controls.Add(acceptBtn);
        btns.Controls.Add(rejectBtn);
        body.Controls.Add(btns, 0, 5);

        Controls.Add(body);
        AcceptButton = acceptBtn;
        CancelButton = rejectBtn;

        _timer.Tick += (_, _) =>
        {
            _left--;
            if (_left <= 0) { DialogResult = DialogResult.Cancel; Close(); return; }
            _countdown.Text = $"若 {_left} 秒内未选择，将自动拒绝并通知对方";
        };
        _timer.Start();
        FormClosed += (_, _) => { _timer.Stop(); _timer.Dispose(); };
    }

    private void PickPath()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "选择保存位置",
            FileName = SafeFileName(_req.FileName),
            InitialDirectory = AppConfig.ReceiveDirectory,
            Filter = "所有文件|*.*",
            OverwritePrompt = true
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _savePath = dlg.FileName;
            _pathLabel.Text = _savePath;
        }
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
