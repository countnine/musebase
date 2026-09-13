using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Musebase.Windows.Services;

/// <summary>
/// 점프 목록 항목 앞에 붙일 아이콘을 <b>직접 만들어</b> 파일로 둔다.
///
/// 시스템 DLL의 아이콘을 번호로 가리키는 흔한 방법(<c>shell32.dll,13</c>)은 쓰지 않는다 —
/// 번호가 Windows 버전마다 달라 엉뚱한 그림이 붙는다. 글리프 하나를 그려 ICO로 저장하면
/// 어느 버전에서도 같은 그림이 나온다.
///
/// ICO는 Vista 이후 <b>PNG를 그대로 담을 수 있다</b>. 그래서 32×32 PNG 앞에 22바이트 머리말만
/// 붙이면 된다(PNG를 BMP로 변환하는 번거로움이 없다).
/// </summary>
public static class JumpIcons
{
    /// <summary>만들어 둔 아이콘 경로(없으면 만든다). 실패하면 null — 호출자가 앱 아이콘으로 돌아간다.</summary>
    public static string? Make(string name, string glyph)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Musebase", "jumpicons");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, name + ".ico");
            if (File.Exists(path)) return path;

            using var png = new MemoryStream();
            RenderGlyph(glyph).Save(png, ImageFormat.Png);
            WriteIco(path, png.ToArray());
            return path;
        }
        catch (Exception e)
        {
            Log.Write($"[taskbar] 아이콘 만들기 실패({name}): {e.Message}");
            return null;
        }
    }

    /// <summary>글리프 하나를 32×32 투명 배경에 흰색으로 그린다(작업표시줄 메뉴는 어두운 배경이다).</summary>
    private static Bitmap RenderGlyph(string glyph)
    {
        const int size = 32;
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        using var font = new Font("Segoe UI Symbol", 17, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.White);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
        return bitmap;
    }

    /// <summary>PNG 한 장을 담은 ICO 파일을 쓴다(ICONDIR 6바이트 + ICONDIRENTRY 16바이트 + PNG).</summary>
    private static void WriteIco(string path, byte[] png)
    {
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);

        w.Write((short)0);      // reserved
        w.Write((short)1);      // type: 1 = icon
        w.Write((short)1);      // 이미지 개수

        w.Write((byte)32);      // 너비
        w.Write((byte)32);      // 높이
        w.Write((byte)0);       // 팔레트 색 수(트루컬러 = 0)
        w.Write((byte)0);       // reserved
        w.Write((short)1);      // 색 평면
        w.Write((short)32);     // 비트/픽셀
        w.Write(png.Length);    // 이미지 바이트 수
        w.Write(22);            // 이미지 시작 위치(머리말 6 + 항목 16)

        w.Write(png);
    }
}
