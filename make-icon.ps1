# Generates src\icon.ico (red rounded square with a white download arrow) at all common sizes.
param([string]$Out = (Join-Path $PSScriptRoot 'src\icon.ico'))

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class IconMaker
{
    static float Snap(float v, int s) { return s <= 32 ? (float)Math.Round(v) : v; }

    static Bitmap Draw(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float m = s <= 24 ? 0f : s * 0.04f;
            var r = new RectangleF(m, m, s - 2 * m, s - 2 * m);
            float d = r.Width * 0.44f;
            using (var path = new GraphicsPath())
            {
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                using (var b = new LinearGradientBrush(r, Color.FromArgb(255, 255, 84, 72), Color.FromArgb(255, 210, 28, 22), 90f))
                    g.FillPath(b, path);
            }

            float u = s / 16f, cx = s / 2f;
            using (var w = new SolidBrush(Color.White))
            {
                float shaftW = Snap(2.4f * u, s), top = Snap(3.2f * u, s), headTop = Snap(7.2f * u, s);
                float tip = Snap(10.8f * u, s), half = 3.7f * u;
                g.FillRectangle(w, cx - shaftW / 2, top, shaftW, headTop - top + 0.5f);
                g.FillPolygon(w, new[] { new PointF(cx - half, headTop), new PointF(cx + half, headTop), new PointF(cx, tip) });
                float barTop = Snap(12f * u, s), barH = Math.Max(1.5f, Snap(1.6f * u, s)), barW = 8.4f * u;
                g.FillRectangle(w, cx - barW / 2, barTop, barW, barH);
            }
        }
        return bmp;
    }

    static byte[] Png(Bitmap b)
    {
        using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    static byte[] Dib(Bitmap b)
    {
        int s = b.Width, maskStride = ((s + 31) / 32) * 4;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            var data = b.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var row = new byte[s * 4];
            var alpha = new bool[s, s];
            for (int y = s - 1; y >= 0; y--)
            {
                Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < s; x++) alpha[x, y] = row[x * 4 + 3] == 0;
                w.Write(row);
            }
            b.UnlockBits(data);
            for (int y = s - 1; y >= 0; y--)
            {
                var mask = new byte[maskStride];
                for (int x = 0; x < s; x++) if (alpha[x, y]) mask[x / 8] |= (byte)(0x80 >> (x % 8));
                w.Write(mask);
            }
            w.Flush();
            return ms.ToArray();
        }
    }

    public static void Save(string path)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
            using (var b = Draw(sizes[i])) images[i] = sizes[i] >= 256 ? Png(b) : Dib(b);

        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32); w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
    }
}
'@

[IconMaker]::Save($Out)
Write-Host "Wrote $Out"
