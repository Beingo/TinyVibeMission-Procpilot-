using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProcPilot.Services;

/// <summary>
/// 从 Windows 自带图标库（imageres.dll / shell32.dll）提取齿轮（设置）图标，
/// 作为应用窗口与系统托盘图标。
/// </summary>
public static class IconProvider
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>齿轮图标候选来源（按优先级：先命中即用）。</summary>
    private static readonly (string File, int Index)[] GearCandidates =
    {
        ("imageres.dll", 109),  // 已验证：齿轮（设置图标）
        ("imageres.dll", 33),   // 备选
        ("imageres.dll", 27),   // 备选
        ("shell32.dll", 35),    // 备选
    };

    public static Icon GetGearIcon()
    {
        foreach (var (file, index) in GearCandidates)
        {
            var path = Path.Combine(Environment.SystemDirectory, file);
            if (ExtractIconEx(path, index, out var large, out var small, 1) != 0 && large != IntPtr.Zero)
            {
                try
                {
                    using var tmp = Icon.FromHandle(large);
                    return (Icon)tmp.Clone();
                }
                finally
                {
                    if (large != IntPtr.Zero) DestroyIcon(large);
                    if (small != IntPtr.Zero) DestroyIcon(small);
                }
            }
        }
        return SystemIcons.Application; // 兜底
    }

    /// <summary>System.Drawing.Icon → WPF ImageSource（用于窗口/任务栏图标）。</summary>
    public static ImageSource ToImageSource(Icon icon)
    {
        using var bmp = icon.ToBitmap();
        var hbmp = bmp.GetHbitmap();
        try
        {
            return Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DeleteObject(hbmp);
        }
    }
}
