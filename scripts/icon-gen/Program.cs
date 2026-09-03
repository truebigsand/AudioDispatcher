using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// AudioDispatcher 应用图标生成器:绿色圆底 + 白色声波三弧(声源向多路扩散)。
// 输出多尺寸 PNG 压缩 .ico(16/24/32/48/64/128/256),与托盘运行时绘制图案一致。
// 用法: dotnet run -- <输出路径默认 ../../src/AudioDispatcher/Assets/app.ico>

var output = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
        "..", "src", "AudioDispatcher", "Assets", "app.ico"));

var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
var pngs = new Dictionary<int, byte[]>();
foreach (var size in sizes)
{
    pngs[size] = PngBytes(DrawIcon(size));
}

// ICO 文件头 + 目录项 + PNG 数据(Vista+ 支持 PNG 压缩条目)
var headerSize = 6 + sizes.Length * 16;
using var ms = new MemoryStream();
using (var w = new BinaryWriter(ms))
{
    w.Write((ushort)0);          // reserved
    w.Write((ushort)1);          // type: icon
    w.Write((ushort)sizes.Length);
    var offset = headerSize;
    foreach (var size in sizes)
    {
        var data = pngs[size];
        w.Write((byte)(size >= 256 ? 0 : size)); // 256 记 0
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);        // palette
        w.Write((byte)0);        // reserved
        w.Write((ushort)1);      // planes
        w.Write((ushort)32);     // bpp
        w.Write(data.Length);
        w.Write(offset);
        offset += data.Length;
    }
    foreach (var size in sizes)
    {
        w.Write(pngs[size]);
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllBytes(output, ms.ToArray());
Console.WriteLine($"已生成图标: {output} ({sizes.Length} 个尺寸)");

return;

static byte[] PngBytes(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static Bitmap DrawIcon(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.Clear(Color.Transparent);

    var s = size / 256f; // 按 256 基准坐标缩放
    var green = Color.FromArgb(46, 160, 67); // 与托盘"分发中"色一致

    using (var bg = new SolidBrush(green))
    {
        // 圆底(略小于画布)
        g.FillEllipse(bg, 10 * s, 10 * s, 236 * s, 236 * s);
    }

    using var pen = new Pen(Color.White, 22 * s)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
    };
    // 三条同心声波弧,开口朝右上(声源向多路扩散)
    for (var i = 0; i < 3; i++)
    {
        var inset = 42 + i * 32; // 42, 74, 106
        g.DrawArc(pen, inset * s, inset * s, (256 - inset * 2) * s, (256 - inset * 2) * s, -52, 104);
    }
    return bmp;
}
