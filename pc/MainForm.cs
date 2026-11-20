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
    private readonly RichTextBox _log = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _progressLabel = new();
    private readonly ContextMenuStrip _deviceMenu = new();
    private readonly ToolTip _tip = new();
    private readonly ToolStripMenuItem _menuSend;
    private readonly ToolStripMenuItem _menuCopyIp;
    private readonly ToolStripMenuItem _menuRefresh;

    private Peer? _menuTarget;

    public MainForm()
    {
        _config = AppConfig.Load();
        _discovery = new DiscoveryService(_config);
        _transfer = new TransferService(_config);

        _menuSend = new ToolStripMenuItem("发送文件…");
        _menuCopyIp = new ToolStripMenuItem("复制 IP 地址");
        _menuRefresh = new ToolStripMenuItem("刷新设备");

        BuildUi();
        WireEvents();

        _transfer.Start();
        _discovery.Start();
        AppendLog($"已启动 · 本机 {_config.DeviceName} ({_discovery.LocalIp}:{_config.TcpPort})", Color.FromArgb(25, 100, 180));
        AppendLog("右键在线设备即可发送文件；文件保存在程序目录 received/", Color.Gray);
    }

    // ---------------- UI 构建 ----------------

    private void BuildUi()
    {
        Text = "飞鸽 P2P · 局域网互传";
        Size = new Size(920, 620);
        MinimumSize = new Size(720, 460);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(250, 250, 250);
        Font = new Font("微软雅黑", 9F);

        // 顶部本机信息条
        var top = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.White, Padding = new Padding(14, 0, 14, 0) };
        top.Controls.Add(_nameLabel);
        _nameLabel.Text = _config.DeviceName;
        _nameLabel.Font = new Font("微软雅黑", 11F, FontStyle.Bold);
        _nameLabel.ForeColor = Color.FromArgb(30, 30, 30);
        _nameLabel.Location = new Point(14, 7);
        _nameLabel.AutoSize = true;
        _nameLabel.Cursor = Cursors.Hand;
        _tip.SetToolTip(_nameLabel, "点击修改本机设备名");

        _nameBox.Location = new Point(12, 6);
        _nameBox.Size = new Size(200, 26);
        _nameBox.Font = new Font("微软雅黑", 11F);
        _nameBox.Visible = false;
        top.Controls.Add(_nameBox);

        _localInfo.Location = new Point(14, 27);
        _localInfo.AutoSize = true;
        _localInfo.ForeColor = Color.Gray;
        _localInfo.Font = new Font("微软雅黑", 8.5F);
        _localInfo.Text = $"{_discovery.LocalIp}:{_config.TcpPort} · 协议 v1.0";

        _onlineDot.Text = "● 在线";
        _onlineDot.ForeColor = Color.FromArgb(30, 160, 60);
        _onlineDot.Font = new Font("微软雅黑", 9F);
        _onlineDot.Dock = DockStyle.Right;
        _onlineDot.TextAlign = ContentAlignment.MiddleCenter;
        _onlineDot.Width = 80;
        top.Controls.Add(_onlineDot);
        top.Controls.Add(_nameLabel);
        _nameLabel.BringToFront();
        Controls.Add(top);

        // 主体：左右分栏
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 380,
            BackColor = Color.FromArgb(230, 232, 235),
            Panel1 = { Padding = new Padding(10, 10, 6, 10) },
            Panel2 = { Padding = new Padding(6, 10, 10, 10) }
        };

        // 左：设备列表 + 收件夹按钮
        var leftPanel = new Panel { Dock = DockStyle.Fill };
        var deviceTitle = new Label
        {
            Text = "在线设备（右键发送）",
            Dock = DockStyle.Top,
            Height = 26,
            Font = new Font("微软雅黑", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(50, 50, 50)
        };
        var openFolder = new Button
        {
            Text = "打开收件夹",
            Dock = DockStyle.Bottom,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(40, 90, 160)
        };
        openFolder.FlatAppearance.BorderColor = Color.FromArgb(200, 210, 225);
        openFolder.Click += (_, _) => OpenReceiveFolder();

        _devices.View = View.Details;
        _devices.FullRowSelect = true;
        _devices.MultiSelect = false;
        _devices.Dock = DockStyle.Fill;
        _devices.Columns.Add("设备名", 200);
        _devices.Columns.Add("IP 地址", 130);
        _devices.Columns.Add("端口", 60);
        _devices.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _devices.BackColor = Color.White;
        _devices.BorderStyle = BorderStyle.FixedSingle;
        _devices.ContextMenuStrip = _deviceMenu;
        _deviceMenu.Items.AddRange(new ToolStripItem[] { _menuSend, _menuCopyIp, new ToolStripSeparator(), _menuRefresh });
        _deviceMenu.Opening += DeviceMenu_Opening;

        leftPanel.Controls.Add(_devices);
        leftPanel.Controls.Add(deviceTitle);
        leftPanel.Controls.Add(openFolder);
        _devices.BringToFront();
        split.Panel1.Controls.Add(leftPanel);

        // 右：传输日志
        var rightPanel = new Panel { Dock = DockStyle.Fill };
        var logTitle = new Label
        {
            Text = "传输日志",
            Dock = DockStyle.Top,
            Height = 26,
            Font = new Font("微软雅黑", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(50, 50, 50)
        };
        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        _log.BackColor = Color.White;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Font = new Font("Consolas", 9F);
        rightPanel.Controls.Add(_log);
        rightPanel.Controls.Add(logTitle);
        _log.BringToFront();
        split.Panel2.Controls.Add(rightPanel);

        Controls.Add(split);

        // 底部进度条
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Color.White, Padding = new Padding(14, 8, 14, 8) };
        _progress.Dock = DockStyle.Bottom;
        _progress.Height = 12;
        _progress.Minimum = 0;
        _progress.Maximum = 1000;
        _progress.Value = 0;
        _progressLabel.Dock = DockStyle.Fill;
        _progressLabel.Text = "就绪";
        _progressLabel.ForeColor = Color.Gray;
        bottom.Controls.Add(_progress);
        bottom.Controls.Add(_progressLabel);
        Controls.Add(bottom);
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

        _discovery.PeerChanged += (peer, added) => Ui(() =>
        {
            if (added)
            {
                var existing = FindItem(peer.Key);
                if (existing == null)
                {
                    var item = new ListViewItem(new[] { peer.Name, peer.Ip, peer.TcpPort.ToString() }) { Tag = peer };
                    _devices.Items.Add(item);
                }
                else
                {
                    existing.SubItems[0].Text = peer.Name;
                    existing.Tag = peer;
                }
                AppendLog($"发现设备：{peer.Name} ({peer.Ip})", Color.FromArgb(180, 130, 20));
            }
            else
            {
                var item = FindItem(peer.Key);
                if (item != null) _devices.Items.Remove(item);
                AppendLog($"设备离线：{peer.Name}", Color.FromArgb(180, 130, 20));
            }
        });

        _transfer.Log += msg => Ui(() => AppendLog(msg, Color.FromArgb(40, 40, 40)));
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

        FormClosing += (_, _) =>
        {
            _discovery.Dispose();
            _transfer.Dispose();
        };
    }

    private void DeviceMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_devices.SelectedItems.Count == 0) { e.Cancel = true; return; }
        _menuTarget = _devices.SelectedItems[0].Tag as Peer;
        if (_menuTarget == null) { e.Cancel = true; return; }
        _menuSend.Text = $"发送文件到 {_menuTarget.Name}…";
    }

    private async Task SendFromMenuAsync()
    {
        if (_menuTarget == null) return;
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = $"发送文件到 {_menuTarget.Name}",
            Filter = "所有文件|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var target = _menuTarget;
        foreach (var file in dlg.FileNames)
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
            _log.SelectionStart = _log.TextLength;
            _log.SelectionColor = color;
            _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
            _log.ScrollToCaret();
        });
    }

    private void Ui(Action action)
    {
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}
