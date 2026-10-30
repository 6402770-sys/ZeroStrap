Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "IconZeroStrap.ico"
$sizes = 16,24,32,48,64,128,256
$pngs = @()

function RoundRect($x,$y,$w,$h,$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r*2
    $p.AddArc($x,$y,$d,$d,180,90); $p.AddArc($x+$w-$d,$y,$d,$d,270,90)
    $p.AddArc($x+$w-$d,$y+$h-$d,$d,$d,0,90); $p.AddArc($x,$y+$h-$d,$d,$d,90,90)
    $p.CloseFigure(); $p
}

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s,$s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s/256.0

    # Background: near-black rounded square with a faint grey border
    $bg = RoundRect (4*$k) (4*$k) (248*$k) (248*$k) (56*$k)
    $rect = New-Object System.Drawing.RectangleF 0,0,$s,$s
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(255,24,24,24)),
        ([System.Drawing.Color]::FromArgb(255,0,0,0)), 90.0
    $g.FillPath($brush,$bg)
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70,255,255,255)), ([Math]::Max(1,3*$k))), $bg)

    # Neon tube: the "zero" ring and a stroked "Z" inside it
    $neon = New-Object System.Drawing.Drawing2D.GraphicsPath
    $neon.AddEllipse(48*$k, 48*$k, 160*$k, 160*$k)
    $neon.StartFigure()
    $neon.AddLines([System.Drawing.PointF[]](
        @(92,98),@(164,98),@(92,158),@(164,158) |
        ForEach-Object { New-Object System.Drawing.PointF ($_[0]*$k),($_[1]*$k) }))

    # Glow: wide translucent strokes, narrowing towards a bright white core
    $core = [Math]::Max(1.5, 12*$k)
    if ($s -ge 32) {
        foreach ($step in @(@(44,18),@(32,30),@(22,55),@(16,90))) {
            $p = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb($step[1],255,255,255)), ($step[0]*$k)
            $p.LineJoin = 'Round'; $p.StartCap = 'Round'; $p.EndCap = 'Round'
            $g.DrawPath($p, $neon)
        }
    }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $core
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawPath($pen, $neon)

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path $PSScriptRoot "IconZeroStrap.png")) }
    $pngs += ,$ms.ToArray()
    $g.Dispose(); $bmp.Dispose()
}

# Write ICO container with PNG-compressed entries
$fs = [System.IO.File]::Create($out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16*$sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) {0} else {$s}
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
