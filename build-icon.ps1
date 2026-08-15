# ============================================================================
# build-icon.ps1
# 把 DeepSeek 鲸鱼 SVG path 渲染成多尺寸 DeepSeekWhale.ico（托盘 / 桌面图标用）。
#
# 依赖: Windows PowerShell 5.1 + .NET Framework WPF (Built-in)。
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File build-icon.ps1
# 输出: ./assets/DeepSeekWhale.ico
# ============================================================================
[CmdletBinding()] param()

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$pathTxt = Join-Path $root 'dsh-whale-path.txt'
$outDir  = Join-Path $root 'assets'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$icoPath = Join-Path $outDir 'DeepSeekWhale.ico'

if (-not (Test-Path -LiteralPath $pathTxt)) { throw "missing: $pathTxt" }
$d = Get-Content -LiteralPath $pathTxt -Raw

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

# --- 解析鲸鱼几何 -----------------------------------------------------------
$geo = [System.Windows.Media.Geometry]::Parse($d)
$b   = $geo.Bounds

# --- 渲染单个尺寸到位图 ------------------------------------------------------
function New-WhaleBitmap([int]$size) {
    # 超采样: 渲染 4x 再缩小, 得到抗锯齿边缘
    $super = 4
    $px = $size * $super

    $scale = ($px / [Math]::Max($b.Width, $b.Height)) * 0.86   # 四周留边

    # 变换矩阵: SVG 画布 -> 超采样画布像素坐标(含内边距居中 + 垂直翻转); 之后缩到 $size 抗锯齿
    $matrix = New-Object System.Windows.Media.Matrix
    $matrix.Scale($scale, -$scale)
    $matrix.Translate((($px - $b.Width  * $scale) / 2.0), (($px + $b.Height * $scale) / 2.0))
    $transform = New-Object System.Windows.Media.MatrixTransform -ArgumentList $matrix

    $geo2 = $geo.Clone()
    $geo2.Transform = $transform

    $dbrush  = New-Object System.Windows.Media.SolidColorBrush -ArgumentList ([System.Windows.Media.Colors]::Black)
    $drawing = New-Object System.Windows.Media.DrawingGroup
    $geomDraw = New-Object System.Windows.Media.GeometryDrawing -ArgumentList $dbrush, $null, $geo2
    $drawing.Children.Add($geomDraw)
    $drawing.Freeze()

    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap -ArgumentList $px, $px, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.DrawDrawing($drawing)
    $dc.Close()
    $rtb.Render($visual)

    # RTB -> BitmapSource -> System.Drawing.Bitmap
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $null = $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object System.IO.MemoryStream
    $encoder.Save($ms)
    $ms.Position = 0
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $ms
    $ms.Dispose()

    # 缩小到目标尺寸
    $final = New-Object System.Drawing.Bitmap -ArgumentList $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($final)
    $g.CompositingMode    = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($bmp, 0, 0, $size, $size)
    $g.Dispose()
    $bmp.Dispose()
    return $final
}

# --- 打包 .ico (多尺寸) ------------------------------------------------------
$WritePl = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
public static class IcoWriter {
    // size must be 256 for PNG-compressed, else BMP-compressed DIB
    public static void Write(string path, Image[] imgs, int[] sizes) {
        if (imgs.Length != sizes.Length) throw new Exception("count mismatch");
        using (var fs = new FileStream(path, FileMode.Create)) {
            using (var bw = new BinaryWriter(fs)) {
                bw.Write((Int16)0);
                bw.Write((Int16)1);
                bw.Write((Int16)sizes.Length);
                var offset = 6 + 16 * sizes.Length;
                var data = new byte[sizes.Length][];
                var dlen = new int[sizes.Length];
                for (int i = 0; i < sizes.Length; i++) {
                    // 256 用 PNG, 其它用 32bpp DIB
                    if (sizes[i] == 256) {
                        var png = PngBytes(imgs[i]);
                        data[i] = png; dlen[i] = png.Length;
                    } else {
                        var dib = Dib32(imgs[i]);
                        data[i] = dib; dlen[i] = dib.Length;
                    }
                }
                for (int i = 0; i < sizes.Length; i++) {
                    int s = sizes[i];
                    bw.Write((byte)(s == 256 ? 0 : s));
                    bw.Write((byte)(s == 256 ? 0 : s));
                    bw.Write((byte)0); // palette
                    bw.Write((byte)0); // reserved
                    bw.Write((Int16)1); // planes
                    bw.Write((Int16)32); // bpp
                    bw.Write((int)dlen[i]); // bytes in resource
                    bw.Write((int)offset); // offset
                    offset += dlen[i];
                }
                for (int i = 0; i < sizes.Length; i++) bw.Write(data[i]);
            }
        }
    }
    static byte[] PngBytes(Image img) {
        using (var ms = new MemoryStream()) {
            img.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }
    static byte[] Dib32(Image img) {
        int w = img.Width, h = img.Height;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(bmp)) g.DrawImage(img, 0, 0, w, h);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            int rowBytes = w * 4;
            var px = new byte[Math.Abs(stride) * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
            bmp.UnlockBits(data);
            // XOR (BGRA bottom-up) + AND mask
            var andStride = ((w + 31) / 32) * 4;
            var dib = new byte[40 + stride * h + andStride * h];
            using (var ms = new MemoryStream(dib)) {
                using (var bw = new BinaryWriter(ms)) {
                    bw.Write((int)40);
                    bw.Write((int)w);
                    bw.Write((int)(h * 2));
                    bw.Write((Int16)1);
                    bw.Write((Int16)32);
                    bw.Write((int)0); // BI_RGB
                    bw.Write((int)(stride * h));
                    bw.Write((int)0);
                    bw.Write((int)0);
                    bw.Write((int)0);
                    bw.Write((int)0);
                    // XOR pixels, bottom-up (Windows bitmaps are bottom-up)
                    for (int y = h - 1; y >= 0; y--) {
                        bw.Write(px, y * stride, rowBytes);
                    }
                    // AND mask (all 0 => fully opaque)
                    var zero = new byte[andStride];
                    for (int y = 0; y < h; y++) bw.Write(zero);
                }
            }
            return dib;
        }
    }
}
'@
Add-Type -TypeDefinition $WritePl -ReferencedAssemblies (
    'System.Drawing.dll', 'System.Runtime.InteropServices.dll')
$icoWriterType = [IcoWriter]

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$imgs  = foreach ($s in $sizes) { New-WhaleBitmap $s }
[IcoWriter]::Write($icoPath, [System.Drawing.Image[]]@($imgs), [int[]]$sizes)
foreach ($im in $imgs) { $im.Dispose() }

Write-Host "[build-icon] 已生成: $icoPath"
Get-Item -LiteralPath $icoPath | Select-Object Name, Length
