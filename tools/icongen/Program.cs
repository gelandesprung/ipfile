using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

// 从「飞」字字体轮廓生成两端统一的应用图标：
//   1) PC 端多尺寸 app.ico（蓝圆 + 白「飞」）
//   2) Android vector pathData（同一份轮廓，保证字形一致）
// 用法：icongen <out.ico> <out-pathdata.txt>

internal static class IconGen
{
    internal static readonly Color Blue = Color.FromArgb(30, 111, 217);

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法：icongen <out.ico> <out-pathdata.txt>");
            return 2;
        }

        if (args[0] == "--extract")
        {
            using var ico = Icon.ExtractAssociatedIcon(args[1]) ?? throw new InvalidOperationException("exe 里没有图标");
            using var bmp = ico.ToBitmap();
            bmp.Save(args[2], ImageFormat.Png);
            Console.WriteLine($"exe icon = {bmp.Width}x{bmp.Height}");
            return 0;
        }

        var contours = GlyphContours();
        var box = Union(contours);
        var norm = Normalize(contours, box);   // 归一化到 0..1，保持长宽比

        File.WriteAllText(args[1], PathData(norm, 108f, 0.50f), new UTF8Encoding(false));
        WriteIco(args[0], new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 }, norm);

        if (args.Length >= 3)
        {
            using var big = Render(256, norm);
            big.Save(args[2], ImageFormat.Png);
        }

        Console.WriteLine($"contours={contours.Length}, sizes={string.Join("/", contours.Select(c => c.Length))}");
        Console.WriteLine($"glyph bounds = {box.Width:F1} x {box.Height:F1}, ico = {args[0]} ({new FileInfo(args[0]).Length} bytes)");
        return 0;
    }

    // ---------- 字形轮廓（Win32 GetGlyphOutline，y 向上） ----------

    private static PointF[][] GlyphContours()
    {
        using var font = new Font("Microsoft YaHei", 1000f, FontStyle.Bold, GraphicsUnit.Pixel);
        var hFont = font.ToHfont();
        var hdc = GetDC(IntPtr.Zero);
        var old = SelectObject(hdc, hFont);
        try
        {
            var mat = new MAT2
            {
                eM11 = Fix(1f), eM12 = Fix(0f), eM21 = Fix(0f), eM22 = Fix(1f)
            };
            var gm = default(GLYPHMETRICS);
            uint size = GetGlyphOutlineW(hdc, '飞', GGO_NATIVE, ref gm, 0, IntPtr.Zero, ref mat);
            if (size == 0xFFFFFFFF) throw new InvalidOperationException("GetGlyphOutlineW 失败：" + Marshal.GetLastWin32Error());

            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                GetGlyphOutlineW(hdc, '飞', GGO_NATIVE, ref gm, size, buf, ref mat);
                Console.WriteLine($"outline bytes={size}, box={gm.gmBlackBoxX}x{gm.gmBlackBoxY}");
                return Parse(buf, (int)size);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally
        {
            SelectObject(hdc, old);
            DeleteObject(hFont);
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private static PointF[][] Parse(IntPtr buf, int size)
    {
        var result = new List<PointF[]>();
        int off = 0;
        while (off + 16 <= size)
        {
            uint cb = (uint)Marshal.ReadInt32(buf, off);      // 整个多边形（含 header）字节数
            Console.WriteLine($"  poly off={off} cb={cb} type={Marshal.ReadInt32(buf, off + 4)}");
            if (cb < 16) break;
            int polyEnd = Math.Min(size, off + (int)cb);

            var pts = new List<PointF> { ReadPoint(buf, off + 8) };
            int p = off + 16;
            while (p + 4 <= polyEnd)
            {
                ushort wType = (ushort)Marshal.ReadInt16(buf, p);
                ushort cpfx = (ushort)Marshal.ReadInt16(buf, p + 2);
                int q = p + 4;
                if (q + cpfx * 8 > polyEnd) break;

                var ctrl = new PointF[cpfx];
                for (int i = 0; i < cpfx; i++) ctrl[i] = ReadPoint(buf, q + i * 8);

                if (wType == TT_PRIM_LINE)
                {
                    pts.AddRange(ctrl);
                }
                else if (wType == TT_PRIM_QSPLINE)
                {
                    // 二次样条：前 cpfx-1 个是控制点，最后一个是终点；
                    // 相邻控制点的中点是该段曲线的落点
                    var cur = pts[^1];
                    for (int i = 0; i < cpfx - 1; i++)
                    {
                        var end = i == cpfx - 2 ? ctrl[^1] : Mid(ctrl[i], ctrl[i + 1]);
                        Quad(pts, cur, ctrl[i], end);
                        cur = end;
                    }
                }
                p = q + cpfx * 8;
            }
            if (pts.Count > 2) result.Add(pts.ToArray());
            off = polyEnd;
        }
        return result.ToArray();
    }

    private static void Quad(List<PointF> outPts, PointF p0, PointF c, PointF p1, int seg = 10)
    {
        for (int i = 1; i <= seg; i++)
        {
            float t = i / (float)seg, mt = 1 - t;
            outPts.Add(new PointF(
                mt * mt * p0.X + 2 * mt * t * c.X + t * t * p1.X,
                mt * mt * p0.Y + 2 * mt * t * c.Y + t * t * p1.Y));
        }
    }

    private static PointF ReadPoint(IntPtr buf, int offset)
    {
        float x = ToFloat(Marshal.ReadInt16(buf, offset + 2), (ushort)Marshal.ReadInt16(buf, offset));
        float y = ToFloat(Marshal.ReadInt16(buf, offset + 6), (ushort)Marshal.ReadInt16(buf, offset + 4));
        return new PointF(x, -y);   // 字体坐标 y 向上，翻成屏幕坐标
    }

    private static float ToFloat(short value, ushort fract) => value + fract / 65536f;

    private static PointF Mid(PointF a, PointF b) => new((a.X + b.X) / 2f, (a.Y + b.Y) / 2f);

    private static FIXED Fix(float v)
    {
        int raw = (int)Math.Round(v * 65536f);
        return new FIXED { value = (short)(raw >> 16), fract = (ushort)(raw & 0xFFFF) };
    }

    // ---------- 归一化 ----------

    private static RectangleF Union(PointF[][] contours)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var c in contours)
            foreach (var p in c)
            {
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
        return RectangleF.FromLTRB(minX, minY, maxX, maxY);
    }

    private static PointF[][] Normalize(PointF[][] contours, RectangleF box)
    {
        float s = Math.Max(box.Width, box.Height);
        float dx = (s - box.Width) / 2f, dy = (s - box.Height) / 2f;
        return contours.Select(c => c.Select(p => new PointF(
            (p.X - box.X + dx) / s,
            (p.Y - box.Y + dy) / s)).ToArray()).ToArray();
    }

    // ---------- Android pathData ----------

    private static string PathData(PointF[][] norm, float viewport, float fillRatio)
    {
        float side = viewport * fillRatio;
        float off = (viewport - side) / 2f;
        var sb = new StringBuilder();
        foreach (var c in norm)
        {
            for (int i = 0; i < c.Length; i++)
            {
                sb.Append(i == 0 ? 'M' : 'L')
                  .Append(F(off + c[i].X * side)).Append(',')
                  .Append(F(off + c[i].Y * side)).Append(' ');
            }
            sb.Append('Z').Append('\n');
        }
        return sb.ToString();
    }

    private static string F(float v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    // ---------- 绘制 ----------

    private static Bitmap Render(int size, PointF[][] norm)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);

        float d = size - 1f;
        using (var bg = new SolidBrush(Blue)) g.FillEllipse(bg, 0f, 0f, d, d);

        // 小尺寸把字放大一点，否则笔画糊成一团
        float ratio = size <= 20 ? 0.66f : size <= 32 ? 0.60f : 0.54f;
        float side = size * ratio;
        float off = (size - side) / 2f;

        using var gp = new GraphicsPath { FillMode = FillMode.Winding };
        foreach (var c in norm)
            gp.AddPolygon(c.Select(p => new PointF(off + p.X * side, off + p.Y * side)).ToArray());

        using var fg = new SolidBrush(Color.White);
        g.FillPath(fg, gp);
        return bmp;
    }

    // ---------- ICO ----------

    private static void WriteIco(string file, int[] sizes, PointF[][] norm)
    {
        var frames = sizes.Select(s =>
        {
            using var bmp = Render(s, norm);
            // 128 及以上用 PNG 压缩（Vista 起支持），否则裸 DIB 太占体积
            return (size: s, data: s >= 128 ? Png(bmp) : Dib(bmp));
        }).ToArray();

        using var fs = File.Create(file);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)frames.Length);

        int offset = 6 + 16 * frames.Length;
        foreach (var (size, data) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);    // palette
            w.Write((byte)0);    // reserved
            w.Write((short)1);   // planes
            w.Write((short)32);  // bpp
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in frames) w.Write(data);
    }

    private static byte[] Png(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>32 位 DIB 帧：BITMAPINFOHEADER + 自下而上的 BGRA + 全零 AND 掩码。</summary>
    private static byte[] Dib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(40);            // biSize
        bw.Write(w);             // biWidth
        bw.Write(h * 2);         // biHeight（XOR + AND）
        bw.Write((short)1);      // biPlanes
        bw.Write((short)32);     // biBitCount
        bw.Write(0);             // biCompression = BI_RGB
        bw.Write(w * h * 4);     // biSizeImage
        bw.Write(2835);          // biXPelsPerMeter
        bw.Write(2835);          // biYPelsPerMeter
        bw.Write(0);             // biClrUsed
        bw.Write(0);             // biClrImportant

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var line = new byte[w * 4];
            for (int y = h - 1; y >= 0; y--)   // 自下而上
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, line, 0, line.Length);
                bw.Write(line);
            }
        }
        finally { bmp.UnlockBits(data); }

        int maskRow = ((w + 31) / 32) * 4;
        bw.Write(new byte[maskRow * h]);
        return ms.ToArray();
    }

    // ---------- P/Invoke ----------

    private const uint GGO_NATIVE = 2;
    private const ushort TT_PRIM_LINE = 1;
    private const ushort TT_PRIM_QSPLINE = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct FIXED { public ushort fract; public short value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MAT2 { public FIXED eM11, eM12, eM21, eM22; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GLYPHMETRICS
    {
        public uint gmBlackBoxX, gmBlackBoxY;
        public int gmptGlyphOriginX, gmptGlyphOriginY;
        public short gmCellIncX, gmCellIncY;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetGlyphOutlineW(IntPtr hdc, uint uChar, uint uFormat,
        ref GLYPHMETRICS lpgm, uint cbBuffer, IntPtr lpvBuffer, ref MAT2 lpmat2);
}
