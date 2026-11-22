using System.Drawing;
using System.Windows.Forms;

namespace FeigeP2P;

/// <summary>
/// 网页模式窗口（Host 端）：地址族切换 + 二维码 + PIN + 在线访客 + 群发。
/// 关窗口只是收起，服务继续跑；要真正停服点「停止服务」（决策：服务与窗口生命周期分离）。
/// </summary>
internal sealed class WebModeForm : Form
{
    private readonly WebHostService _web;
    private readonly Action _onStateChanged;

    private readonly Label _status = new();
    private readonly Button _toggleBtn = new();
    private readonly Button _v4 = new();
    private readonly Button _v6 = new();
    private readonly PictureBox _qr = new();
    private readonly Label _url = new();
    private readonly Button _copy = new();
    private readonly Label _pin = new();
    private readonly Button _pinRefresh = new();
    private readonly Label _guestsTitle = new();
    private readonly Button _group = new();
    private readonly ListView _list = new();
    private readonly Label _empty = new();
    private readonly Panel _headerRow = new();
    private readonly Panel _listDivider = new();
    private readonly Button _portBtn = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _menuSend = new("发送文件…");
    private readonly ToolStripMenuItem _menuCopyIp = new("复制 IP 地址");
    private readonly ToolStripMenuItem _menuKick = new("断开此访客");
    private readonly ToolTip _tip = new();
    /// <summary>PIN 被自动更换后短暂高亮，提示 Host 注意看新码。</summary>
    private readonly System.Windows.Forms.Timer _pinFlash = new() { Interval = 2400 };

    // 默认 IPv6：本机没有可用 IPv6 时，RefreshAddress 会自动回退到 IPv4
    private string _family = "v6";
    private bool _allowClose;

    public WebModeForm(WebHostService web, Action onStateChanged)
    {
        _web = web;
        _onStateChanged = onStateChanged;

        BuildUi();
        WireEvents();
        UpdateStatus();
        RefreshGuests();
    }

    private int D(int design) => (int)Math.Round(design * DeviceDpi / 96.0);

    private void Ui(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    // ---------------- 构建 ----------------

    private void BuildUi()
    {
        Text = "网页模式 · 让朋友用浏览器收文件";
        // 尺寸一律用 D() 换算成当前 DPI 的实际像素，不再对窗体做二次 Scale：
        // 之前 Scale() 之后又有代码把控件尺寸重置成 96 DPI 的字面值，
        // 在 150%/200% 下按钮比文字还矮，分段控件就被压扁了。
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(D(700), D(608));
        MinimumSize = new Size(D(600), D(520));
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(245, 246, 248);
        Font = new Font("微软雅黑", 9F);
        KeyPreview = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Padding = Padding.Empty, Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, D(64)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, D(206)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, D(46)));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildAddressCard(), 0, 1);
        root.Controls.Add(BuildGuests(), 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);

        root.PerformLayout();
    }

    private Control BuildHeader()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = Padding.Empty };
        var title = new Label
        {
            Text = "网页模式",
            Font = new Font("微软雅黑", 15F, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 32, 38),
            AutoSize = true
        };

        _status.AutoSize = true;
        _status.Font = new Font("微软雅黑", 9F);
        _status.ForeColor = Color.FromArgb(130, 138, 148);

        // 停止服务是个危险操作，给描边而不是裸文字：尺寸固定，避免 AutoSize 在不同 DPI 下忽大忽小
        _toggleBtn.AutoSize = false;
        _toggleBtn.Size = new Size(D(84), D(26));
        _toggleBtn.FlatStyle = FlatStyle.Flat;
        _toggleBtn.FlatAppearance.BorderSize = 1;
        _toggleBtn.FlatAppearance.BorderColor = Color.FromArgb(222, 176, 176);
        _toggleBtn.BackColor = Color.White;
        _toggleBtn.ForeColor = Color.FromArgb(198, 40, 40);
        _toggleBtn.Font = new Font("微软雅黑", 9F);
        _toggleBtn.Cursor = Cursors.Hand;

        host.Controls.Add(title);
        host.Controls.Add(_status);
        host.Controls.Add(_toggleBtn);
        host.Controls.Add(new Panel
        {
            Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(224, 228, 233)
        });

        host.Layout += (_, _) =>
        {
            var padX = D(22);
            title.Location = new Point(padX, D(7));
            _status.Location = new Point(padX, title.Bottom + D(3));
            _toggleBtn.Location = new Point(
                Math.Max(padX, host.ClientSize.Width - padX - _toggleBtn.Width),
                D(19));
        };
        return host;
    }

    private Control BuildAddressCard()
    {
        var wrap = new Panel
        {
            Dock = DockStyle.Fill, BackColor = Color.FromArgb(245, 246, 248),
            Padding = new Padding(D(22), D(10), D(22), D(10)), Margin = Padding.Empty
        };

        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            BackColor = Color.White, Margin = Padding.Empty,
            Padding = new Padding(D(16), D(12), D(16), D(12))
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, D(152)));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        // 二维码容器固定正方形（152 设计值），避免之前用 156×整列高导致上下大片留白
        _qr.Dock = DockStyle.Fill;
        _qr.SizeMode = PictureBoxSizeMode.Zoom;
        _qr.BackColor = Color.White;
        _qr.BorderStyle = BorderStyle.FixedSingle;
        _qr.Margin = new Padding(0, 0, D(18), 0);
        card.Controls.Add(_qr, 0, 0);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.White
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, D(30)));   // 地址族
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, D(46)));   // URL + 复制（IPv6 地址长，允许换两行）
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, D(52)));   // PIN
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));     // 说明

        // 地址族分段控件：用 Panel 手工定位，垂直居中；尺寸只在初始化时定一次
        var seg = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.White };
        InitSegment(_v4, "IPv4");
        InitSegment(_v6, "IPv6");
        seg.Controls.Add(_v4);
        seg.Controls.Add(_v6);
        seg.Layout += (_, _) =>
        {
            var y = Math.Max(0, (seg.ClientSize.Height - _v4.Height) / 2);
            _v4.Location = new Point(0, y);
            _v6.Location = new Point(_v4.Right + D(6), y);
        };
        right.Controls.Add(seg, 0, 0);

        // 地址与「复制」同一行：之前复制按钮掉到下一行，视觉上是断开的
        var urlRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        urlRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _url.Dock = DockStyle.Fill;
        _url.Font = new Font("Consolas", 9.5F);
        _url.ForeColor = Color.FromArgb(30, 111, 217);
        _url.TextAlign = ContentAlignment.MiddleLeft;
        // IPv6 的 URL 有 50 多个字符，单行放不下。让 Label 自己折行会把 ":20481/" 劈成
        // ":204" + "81/"（端口是 5 位随机数），所以下面按 "]" 手动断点，端口整体落到第二行。
        _url.AutoEllipsis = false;
        _url.Cursor = Cursors.Hand;
        _url.Margin = new Padding(0, 0, D(10), 0);
        urlRow.Controls.Add(_url, 0, 0);

        _copy.Text = "复制";
        _copy.AutoSize = false;
        _copy.Size = new Size(D(60), D(24));
        _copy.Anchor = AnchorStyles.Right;
        _copy.FlatStyle = FlatStyle.Flat;
        _copy.FlatAppearance.BorderColor = Color.FromArgb(210, 217, 227);
        _copy.FlatAppearance.BorderSize = 1;
        _copy.BackColor = Color.White;
        _copy.ForeColor = Color.FromArgb(30, 111, 217);
        _copy.Font = new Font("微软雅黑", 8.5F);
        _copy.Cursor = Cursors.Hand;
        _copy.Margin = Padding.Empty;
        urlRow.Controls.Add(_copy, 1, 0);
        right.Controls.Add(urlRow, 0, 1);

        var pinRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        pinRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pinRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        pinRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pinRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        pinRow.Controls.Add(new Label
        {
            Text = "访问 PIN", AutoSize = true, Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(130, 138, 148), Font = new Font("微软雅黑", 9F),
            Margin = new Padding(0, 0, D(14), 0)
        }, 0, 0);

        _pin.Dock = DockStyle.Fill;
        _pin.Font = new Font("Consolas", 17F, FontStyle.Bold);
        _pin.ForeColor = Color.FromArgb(28, 32, 38);
        _pin.TextAlign = ContentAlignment.MiddleLeft;
        _pin.Margin = Padding.Empty;
        pinRow.Controls.Add(_pin, 1, 0);

        _pinRefresh.Text = "刷新 PIN";
        _pinRefresh.AutoSize = true;
        _pinRefresh.FlatStyle = FlatStyle.Flat;
        _pinRefresh.FlatAppearance.BorderSize = 0;
        _pinRefresh.BackColor = Color.White;
        _pinRefresh.ForeColor = Color.FromArgb(30, 111, 217);
        _pinRefresh.Font = new Font("微软雅黑", 8.5F);
        _pinRefresh.Cursor = Cursors.Hand;
        _pinRefresh.Anchor = AnchorStyles.Right;
        _pinRefresh.Margin = new Padding(D(8), 0, 0, 0);
        pinRow.Controls.Add(_pinRefresh, 2, 0);
        right.Controls.Add(pinRow, 0, 2);

        right.Controls.Add(new Label
        {
            Text = "让朋友扫上面的码，或手输地址 + PIN 加入；连续输错 3 次 PIN 会自动更换。\r\n切换地址族只影响新扫码的人，已在线的访客不受影响。",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(150, 156, 164),
            Font = new Font("微软雅黑", 8.5F),
            AutoSize = false,
            Margin = new Padding(0, D(4), 0, 0)
        }, 0, 3);

        card.Controls.Add(right, 1, 0);
        wrap.Controls.Add(card);
        return wrap;
    }

    /// <summary>分段按钮只在构建时定一次尺寸与样式（尺寸按设计值换算，之后不再重设）。</summary>
    private void InitSegment(Button b, string text)
    {
        b.Text = text;
        b.AutoSize = false;
        b.Size = new Size(D(70), D(26));
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.Font = SegmentFont;
        b.Cursor = Cursors.Hand;
        b.Margin = new Padding(0, 0, D(6), 0);
    }

    /// <summary>只切选中态配色，尺寸完全不动——避免刷新状态时把按钮压回 96 DPI 的尺寸。</summary>
    private static void PaintSegment(Button b, bool on)
    {
        b.FlatAppearance.BorderColor = on ? Color.FromArgb(30, 111, 217) : Color.FromArgb(214, 220, 228);
        b.BackColor = on ? Color.FromArgb(30, 111, 217) : Color.White;
        b.ForeColor = on ? Color.White : Color.FromArgb(110, 118, 128);
    }

    /// <summary>分段按钮共用同一个字体实例，避免每次刷新状态都新建 GDI 字体。</summary>
    private static readonly Font SegmentFont = new("微软雅黑", 9F);

    private Control BuildGuests()
    {
        var box = new Panel
        {
            Dock = DockStyle.Fill, BackColor = Color.White,
            Padding = new Padding(D(22), D(6), D(22), D(12)), Margin = Padding.Empty
        };

        var head = new Panel { Dock = DockStyle.Top, Height = D(38), BackColor = Color.White };
        _guestsTitle.Text = "在线访客";
        _guestsTitle.Font = new Font("微软雅黑", 11F, FontStyle.Bold);
        _guestsTitle.ForeColor = Color.FromArgb(40, 46, 54);
        _guestsTitle.AutoSize = true;

        // 群发是次要动作：描边蓝字即可。之前用实心蓝 + 大内边距，在 150% DPI 下
        // 变成一个 44px 高的蓝方块，比页面标题还抢眼，和整页的轻量风格不搭。
        _group.Text = "群发…";
        _group.AutoSize = false;
        _group.Size = new Size(D(74), D(26));
        _group.FlatStyle = FlatStyle.Flat;
        _group.FlatAppearance.BorderSize = 1;
        _group.FlatAppearance.BorderColor = Color.FromArgb(180, 206, 240);
        _group.BackColor = Color.White;
        _group.ForeColor = Color.FromArgb(30, 111, 217);
        _group.Font = new Font("微软雅黑", 9F);
        _group.Cursor = Cursors.Hand;

        head.Controls.Add(_guestsTitle);
        head.Controls.Add(_group);
        head.Layout += (_, _) =>
        {
            _guestsTitle.Location = new Point(0, Math.Max(0, (head.ClientSize.Height - _guestsTitle.Height) / 2));
            _group.Location = new Point(
                Math.Max(0, head.ClientSize.Width - _group.Width),
                Math.Max(0, (head.ClientSize.Height - _group.Height) / 2));
        };

        // 列头自绘：ListView 自带的表头带竖分隔线，像张数据表，和卡片的轻量边界不协调
        var headerRow = _headerRow;
        headerRow.Dock = DockStyle.Top;
        headerRow.Height = D(26);
        headerRow.BackColor = Color.White;
        Label MakeHeader(string text) => new()
        {
            Text = text, AutoSize = true,
            ForeColor = Color.FromArgb(152, 158, 166),
            Font = new Font("微软雅黑", 8.5F)
        };
        var hNick = MakeHeader("昵称");
        var hIp = MakeHeader("IP 地址");
        var hState = MakeHeader("状态");
        headerRow.Controls.Add(hNick);
        headerRow.Controls.Add(hIp);
        headerRow.Controls.Add(hState);

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HeaderStyle = ColumnHeaderStyle.None;   // 用上面自绘的那一行
        _list.BorderStyle = BorderStyle.None;
        _list.Dock = DockStyle.Fill;
        _list.BackColor = Color.White;
        _list.Font = new Font("微软雅黑", 9.5F);
        _list.ShowItemToolTips = true;
        _list.ContextMenuStrip = _menu;
        _list.Columns.Add("昵称", D(200));
        _list.Columns.Add("IP 地址", D(130));
        _list.Columns.Add("状态", D(160));
        // 行高：ListView 不会随 DPI 缩放行高，用一条 1px 宽的占位图把行撑开
        _list.SmallImageList = new ImageList { ImageSize = new Size(1, D(28)), ColorDepth = ColorDepth.Depth32Bit };

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _menuSend, _menuCopyIp, new ToolStripSeparator(), _menuKick
        });
        _menu.Opening += Menu_Opening;

        _empty.Text = "还没有访客\r\n\r\n让朋友连你的热点或同一 WiFi，用浏览器打开上面的地址，输入 PIN 加入";
        _empty.TextAlign = ContentAlignment.MiddleCenter;
        _empty.ForeColor = Color.FromArgb(165, 170, 178);
        _empty.Font = new Font("微软雅黑", 9.5F);
        _empty.Dock = DockStyle.Fill;

        var listHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        listHost.Controls.Add(_list);
        listHost.Controls.Add(_empty);
        _empty.BringToFront();
        listHost.ContextMenuStrip = _menu;

        var divider = _listDivider;
        divider.Dock = DockStyle.Top;
        divider.Height = 1;
        divider.BackColor = Color.FromArgb(236, 238, 242);

        box.Controls.Add(listHost);
        box.Controls.Add(divider);
        box.Controls.Add(headerRow);
        box.Controls.Add(head);

        void PlaceHeaders()
        {
            if (_list.Columns.Count < 3) return;
            // ListView 是原生控件，文字左内边距按物理像素算（约 4~5px），不随 WinForms 的 DPI 缩放
            const int nativeTextPad = 4;
            var firstIndent = D(3) + nativeTextPad;
            var colIndent = D(2) + nativeTextPad;
            var c0 = _list.Columns[0].Width;
            var y = Math.Max(0, (headerRow.ClientSize.Height - hNick.Height) / 2);
            hNick.Location = new Point(firstIndent, y);
            hIp.Location = new Point(c0 + colIndent, y);
            hState.Location = new Point(c0 + _list.Columns[1].Width + colIndent, y);
        }

        void FitColumns()
        {
            var w = Math.Max(_list.ClientSize.Width - 2, D(260));
            var nickW = (int)(w * 0.40);
            var ipW = (int)(w * 0.26);
            _list.Columns[0].Width = nickW;
            _list.Columns[1].Width = ipW;
            _list.Columns[2].Width = Math.Max(D(110), w - nickW - ipW);
            PlaceHeaders();
        }
        _list.SizeChanged += (_, _) => FitColumns();
        headerRow.SizeChanged += (_, _) => PlaceHeaders();
        FitColumns();

        return box;
    }

    private Control BuildFooter()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill, BackColor = Color.White,
            Padding = new Padding(D(22), D(8), D(22), D(8)), Margin = Padding.Empty
        };

        var hint = new Label
        {
            Text = "窗口可关闭，服务继续运行 · 收文件时会弹确认框，与手机互传用同一套落盘逻辑",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(150, 156, 164),
            Font = new Font("微软雅黑", 8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Padding = new Padding(0, 0, D(76), 0)
        };

        _portBtn.Text = "换端口";
        _portBtn.AutoSize = false;
        _portBtn.Size = new Size(D(58), D(24));
        _portBtn.FlatStyle = FlatStyle.Flat;
        _portBtn.FlatAppearance.BorderSize = 0;
        _portBtn.BackColor = Color.White;
        _portBtn.ForeColor = Color.FromArgb(120, 128, 138);
        _portBtn.Font = new Font("微软雅黑", 8.5F);
        _portBtn.Cursor = Cursors.Hand;
        _tip.SetToolTip(_portBtn, "随机换一个端口并重启服务（现有访客会断开）");

        host.Controls.Add(hint);
        host.Controls.Add(_portBtn);
        host.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(224, 228, 233) });
        _portBtn.BringToFront();   // 压在 Fill 的提示文字上面，避免被盖住

        host.Layout += (_, _) =>
        {
            _portBtn.Location = new Point(
                Math.Max(0, host.ClientSize.Width - D(22) - _portBtn.Width),
                Math.Max(0, (host.ClientSize.Height - _portBtn.Height) / 2));
        };
        return host;
    }

    private void WireEvents()
    {
        _toggleBtn.Click += (_, _) => ToggleService();
        _v4.Click += (_, _) => SetFamily("v4");
        _v6.Click += (_, _) => SetFamily("v6");
        _copy.Click += (_, _) => CopyUrl();
        _url.Click += (_, _) => CopyUrl();
        _pinRefresh.Click += (_, _) =>
        {
            _web.RefreshPin();
            _pin.Text = _web.Pin;
        };
        _portBtn.Click += (_, _) => RollPort();
        _group.Click += (_, _) => GroupSendAsync().FireAndForget();
        _menuSend.Click += (_, _) => SendToSelectedAsync().FireAndForget();
        _menuCopyIp.Click += (_, _) =>
        {
            var guest = SelectedGuest();
            if (guest != null) Clipboard.SetText(guest.RemoteIp);
        };
        _menuKick.Click += (_, _) =>
        {
            var guest = SelectedGuest();
            if (guest != null) _web.DisconnectGuest(guest.Id);
        };
        _list.DoubleClick += (_, _) => SendToSelectedAsync().FireAndForget();

        _web.GuestsChanged += () => Ui(() =>
        {
            RefreshGuests();
            UpdateStatus();
        });

        _web.PinRotated += pin => Ui(() =>
        {
            _pin.Text = pin;
            _pin.ForeColor = Color.FromArgb(30, 111, 217);
            _pinFlash.Stop();
            _pinFlash.Start();
        });

        _pinFlash.Tick += (_, _) =>
        {
            _pinFlash.Stop();
            _pin.ForeColor = Color.FromArgb(28, 32, 38);
        };

        FormClosing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>真正关闭（程序退出时由 MainForm 调用）。</summary>
    public void ForceClose()
    {
        _allowClose = true;
        _pinFlash.Stop();
        _pinFlash.Dispose();
        Close();
    }

    /// <summary>
    /// 句柄创建后 WinForms 会把 ListView 重新置为可见（主窗口也踩过同样的坑），
    /// 这里按真实访客数再校正一次「列表 / 空状态 / 列头」的显示。
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshGuests();
    }

    // ---------------- 状态与地址 ----------------

    private void UpdateStatus()
    {
        var running = _web.Running;
        _status.Text = running
            ? $"● 服务运行中 · 端口 {_web.Port} · 在线访客 {_web.SnapshotGuests().Count} 人"
            : "服务已停止（访客全部断开）";
        _status.ForeColor = running ? Color.FromArgb(32, 168, 72) : Color.FromArgb(150, 156, 164);
        _toggleBtn.Text = running ? "停止服务" : "启动服务";
        _toggleBtn.ForeColor = running ? Color.FromArgb(198, 40, 40) : Color.FromArgb(30, 111, 217);
        _pin.Text = _web.Pin;
        _pin.Enabled = running;
        _pinRefresh.Enabled = running;
        _copy.Enabled = running;
        _group.Enabled = running && _web.SnapshotGuests().Count > 0;

        var v6 = AppConfig.GetLocalIpV6Only();
        var v4 = AppConfig.GetLocalIpV4Only();
        _v6.Enabled = v6 != null;
        _v4.Enabled = v4 != null;

        // 当前选中的地址族取不到地址时自动回退，避免显示一个连不上的链接
        if (_family == "v6" && v6 == null) _family = v4 != null ? "v4" : "v6";
        if (_family == "v4" && v4 == null) _family = "v6";

        PaintSegment(_v4, _family == "v4");
        PaintSegment(_v6, _family == "v6");

        var url = CurrentUrl();
        _url.Text = FormatUrlForDisplay(url);
        _url.ForeColor = url.Length > 0 ? Color.FromArgb(30, 111, 217) : Color.FromArgb(150, 156, 164);
        var tip = url.Length > 0 ? url : "请检查网络连接";
        _url.Tag = url;
        _tip.SetToolTip(_url, tip);
        RenderQr(url);
        _onStateChanged();
    }

    /// <summary>
    /// 只影响显示：IPv6 的 URL 太长，在 <c>]</c> 之后插一个换行，让 <c>:端口/</c> 整体落到第二行，
    /// 避免 Label 自动折行把端口劈开。复制用的仍是 <c>_url.Tag</c> 里的完整 URL。
    /// </summary>
    private static string FormatUrlForDisplay(string url)
    {
        if (url.Length == 0) return "（本机没有可用地址）";
        var cut = url.IndexOf("]:", StringComparison.Ordinal);
        return cut > 0 ? url[..(cut + 1)] + "\r\n" + url[(cut + 1)..] : url;
    }

    private string CurrentUrl()
    {
        if (!_web.Running) return "";
        var ip = _family == "v6" ? AppConfig.GetLocalIpV6Only() : AppConfig.GetLocalIpV4Only();
        if (ip == null) return "";
        var host = ip.Contains(':') ? $"[{ip}]" : ip;
        return $"http://{host}:{_web.Port}/";
    }

    private void RenderQr(string url)
    {
        var old = _qr.Image;
        if (url.Length == 0)
        {
            _qr.Image = null;
            old?.Dispose();
            return;
        }
        var png = QrCode.RenderPng(url);
        if (png == null) return;
        using var ms = new MemoryStream(png);
        using var tmp = Image.FromStream(ms);
        _qr.Image = new Bitmap(tmp);
        old?.Dispose();
    }

    private void SetFamily(string family)
    {
        if (_family == family) return;
        _family = family;
        UpdateStatus();
    }

    private void CopyUrl()
    {
        if (_url.Tag is not string url || url.Length == 0) return;
        try { Clipboard.SetText(url); } catch { }
    }

    private void ToggleService()
    {
        if (_web.Running)
        {
            _web.Stop();
        }
        else
        {
            // 端口不落配置：每次启动都由服务随机抽一个可用的
            var (ok, msg) = _web.Start();
            if (!ok)
            {
                MessageBox.Show(this, msg, "网页模式", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        UpdateStatus();
        RefreshGuests();
    }

    /// <summary>
    /// 换一个随机端口并重启服务。端口不再让 Host 手输：随机本来就是用来避开占用的，
    /// 换一次比让用户自己猜一个没被占的端口省事。重启会断开现有访客，所以先确认。
    /// </summary>
    private void RollPort()
    {
        if (!_web.Running)
        {
            MessageBox.Show(this, "服务没在运行，启动时会自动随机挑一个端口。", "网页模式",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var guests = _web.SnapshotGuests().Count;
        if (guests > 0 && MessageBox.Show(this,
                $"换端口会重启服务，当前 {guests} 位访客需要重新扫码或打开新地址才能继续。\n\n继续换端口？",
                "换端口", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _web.Stop();
        var (ok, msg) = _web.Start();
        if (!ok) MessageBox.Show(this, msg, "网页模式", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        UpdateStatus();
        RefreshGuests();
    }

    // ---------------- 访客列表 ----------------

    private WebGuest? SelectedGuest() =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as WebGuest : null;

    private void Menu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var guest = SelectedGuest();
        _menuSend.Enabled = guest != null;
        _menuCopyIp.Enabled = guest != null;
        _menuKick.Enabled = guest != null;
        _menuSend.Text = guest != null ? $"发送文件到 {guest.Nickname}…" : "发送文件（先选中访客）";
        _menuKick.Text = guest != null ? $"断开 {guest.Nickname}" : "断开此访客";
    }

    private void RefreshGuests()
    {
        var guests = _web.SnapshotGuests();
        var selectedId = SelectedGuest()?.Id;

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var g in guests)
            {
                var item = new ListViewItem(new[] { g.Nickname, g.RemoteIp, g.Status })
                {
                    Tag = g,
                    ToolTipText = $"{g.Nickname}\n{g.RemoteIp}\n加入于 {g.JoinedAt:HH:mm:ss}"
                };
                item.ForeColor = g.Status == "空闲"
                    ? Color.FromArgb(40, 46, 54)
                    : Color.FromArgb(30, 100, 180);
                _list.Items.Add(item);
                if (g.Id == selectedId) item.Selected = true;
            }
        }
        finally { _list.EndUpdate(); }

        _guestsTitle.Text = guests.Count > 0 ? $"在线访客 {guests.Count}" : "在线访客";
        _empty.Visible = _list.Items.Count == 0;
        _list.Visible = !_empty.Visible;
        // 没有人时连列头一起收起来：留一行孤零零的「昵称 / IP / 状态」下面是空状态，很割裂
        _headerRow.Visible = !_empty.Visible;
        _listDivider.Visible = !_empty.Visible;
        _group.Enabled = _web.Running && guests.Count > 0;
    }

    private async Task SendToSelectedAsync()
    {
        var guest = SelectedGuest();
        if (guest == null) return;
        if (!_web.Running)
        {
            MessageBox.Show(this, "服务已停止，先启动服务再发送", "网页模式", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = $"发送文件到 {guest.Nickname}",
            Filter = "所有文件|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await _web.SendFilesAsync(new[] { guest.Id }, dlg.FileNames);
    }

    private async Task GroupSendAsync()
    {
        var guests = _web.SnapshotGuests();
        if (guests.Count == 0)
        {
            MessageBox.Show(this, "还没有访客加入", "网页模式", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!_web.Running)
        {
            MessageBox.Show(this, "服务已停止，先启动服务再发送", "网页模式", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var pick = new GroupSendDialog(guests) { Owner = this };
        if (pick.ShowDialog(this) != DialogResult.OK || pick.SelectedIds.Count == 0) return;

        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = $"群发给 {pick.SelectedIds.Count} 位访客",
            Filter = "所有文件|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await _web.SendFilesAsync(pick.SelectedIds, dlg.FileNames);
    }
}

/// <summary>群发：多选访客（默认全不选，防误发），确认后再选文件。</summary>
internal sealed class GroupSendDialog : Form
{
    private readonly CheckedListBox _box = new();
    public List<string> SelectedIds { get; } = new();

    public GroupSendDialog(IReadOnlyList<WebGuest> guests)
    {
        Text = "群发文件";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        Font = new Font("微软雅黑", 9.5F);
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(360, 360);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(18), BackColor = Color.White
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));

        root.Controls.Add(new Label
        {
            Text = "选择要发送的访客（可多选）",
            AutoSize = true,
            ForeColor = Color.FromArgb(110, 118, 128),
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);

        _box.Dock = DockStyle.Fill;
        _box.BorderStyle = BorderStyle.FixedSingle;
        _box.CheckOnClick = true;
        _box.Font = new Font("微软雅黑", 10F);
        foreach (var g in guests) _box.Items.Add(g, false);
        root.Controls.Add(_box, 0, 1);

        var btns = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false
        };
        var ok = new Button
        {
            Text = "下一步：选文件",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 111, 217),
            ForeColor = Color.White,
            Padding = new Padding(14, 6, 14, 6),
            Margin = new Padding(8, 8, 0, 0)
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += (_, _) =>
        {
            SelectedIds.Clear();
            foreach (var item in _box.CheckedItems)
                if (item is WebGuest g) SelectedIds.Add(g.Id);
            if (SelectedIds.Count == 0)
            {
                MessageBox.Show(this, "请至少选择一位访客", "群发", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };

        var cancel = new Button
        {
            Text = "取消",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(245, 246, 248),
            ForeColor = Color.FromArgb(70, 78, 88),
            Padding = new Padding(14, 6, 14, 6),
            Margin = new Padding(8, 8, 0, 0)
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(214, 220, 228);

        btns.Controls.Add(ok);
        btns.Controls.Add(cancel);
        root.Controls.Add(btns, 0, 2);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
