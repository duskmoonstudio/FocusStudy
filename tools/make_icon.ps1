param([string]$OutIco, [string]$OutPng)
Add-Type -AssemblyName System.Drawing

function Draw([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)

  # rounded square background
  $pad = [Math]::Max(0.5, $s * 0.03); $w = $s - 2 * $pad; $r = $w * 0.24
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc($pad, $pad, 2*$r, 2*$r, 180, 90)
  $path.AddArc($pad + $w - 2*$r, $pad, 2*$r, 2*$r, 270, 90)
  $path.AddArc($pad + $w - 2*$r, $pad + $w - 2*$r, 2*$r, 2*$r, 0, 90)
  $path.AddArc($pad, $pad + $w - 2*$r, 2*$r, 2*$r, 90, 90)
  $path.CloseFigure()
  $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), ([System.Drawing.Color]::FromArgb(255, 0x26, 0x2D, 0x40)), ([System.Drawing.Color]::FromArgb(255, 0x10, 0x13, 0x1A))
  $g.FillPath($bg, $path)

  $c = $s / 2.0
  # focus ring (accent blue)
  $ringR = $s * 0.285; $ringW = [Math]::Max(1.6, $s * 0.085)
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x6C, 0x8C, 0xFF)), $ringW
  $g.DrawEllipse($pen, [float]($c - $ringR), [float]($c - $ringR), [float](2*$ringR), [float](2*$ringR))
  # centre dot (focused green)
  $dotR = $s * 0.125
  $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x3E, 0xCF, 0x8E))
  $g.FillEllipse($dot, [float]($c - $dotR), [float]($c - $dotR), [float](2*$dotR), [float](2*$dotR))
  $g.Dispose()
  return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
  $b = Draw $s; $ms = New-Object System.IO.MemoryStream
  if ($s -eq 256) {
    # 256 px as PNG (standard for Vista+)
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($OutPng) { $b.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png) }
  } else {
    # smaller sizes as classic 32-bit DIB: BITMAPINFOHEADER + bottom-up BGRA + AND mask
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32](2 * $s)); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]0); $w.Write([UInt32]0); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
      for ($x = 0; $x -lt $s; $x++) {
        $p = $b.GetPixel($x, $y)
        $w.Write([byte]$p.B); $w.Write([byte]$p.G); $w.Write([byte]$p.R); $w.Write([byte]$p.A)
      }
    }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskRow * $s)))
    $w.Flush()
  }
  $b.Dispose(); ,$ms.ToArray()
}

$fs = [System.IO.File]::Create($OutIco); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $d = $pngs[$i]
  $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$d.Length); $bw.Write([UInt32]$offset)
  $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Close()
"ico: $((Get-Item $OutIco).Length) bytes"
