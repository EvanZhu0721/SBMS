$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot = Join-Path $PSScriptRoot 'assets'
[IO.Directory]::CreateDirectory($assetRoot) | Out-Null
# Geometry matches ui/launch-intro.slint. Transparent PNGs remain crisp at 200% DPI.
$cuts = @(
    @(26,0,39,0,32,48),
    @(22,72,32,121,32,136,22,136),
    @(42,72,42,136,32,136,32,121),
    @(102,24,109,24,109,48,136,48,136,57,102,57),
    @(72,80,108,80,108,128,101,128,101,89,72,89)
)
# Sample the original 40 ms timeline at 120 FPS. Motion ends at 760 ms;
# the installer then holds the last frame for 1.68 s (twice the old 0.84 s).
for ($frame = 0; $frame -lt 93; $frame++) {
    $timeline = $frame * 1000.0 / 120.0 / 40.0
    $bitmap = [Drawing.Bitmap]::new(960,400)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.ScaleTransform(2,2)
    $progress = [Math]::Min(1.0, $timeline / 19.0)
    $ease = 1 - [Math]::Pow(1-$progress,3)
    $x = [single](172 + 38*$ease)
    $y = 32
    foreach ($tile in @(@(0,0,'#f25022'),@(72,0,'#7fba00'),@(0,72,'#00a4ef'),@(72,72,'#ffb900'))) {
        $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($tile[2]))
        $g.FillRectangle($brush,($x+$tile[0]),($y+$tile[1]),64,64)
        $brush.Dispose()
    }
    $black = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb([int](255*$ease),5,5,5))
    $g.FillRectangle($black,($x+64),$y,8,136)
    $g.FillRectangle($black,$x,($y+64),136,8)
    foreach ($cut in $cuts) {
        $points = for ($i=0;$i -lt $cut.Length;$i+=2) { [Drawing.PointF]::new(($x+$cut[$i]),($y+$cut[$i+1])) }
        $g.FillPolygon($black,[Drawing.PointF[]]$points)
    }
    $black.Dispose()
    $font = [Drawing.Font]::new('Segoe UI',86,[Drawing.FontStyle]::Bold,[Drawing.GraphicsUnit]::Pixel)
    foreach ($letter in @(@('B',128,5),@('S',70,9))) {
        $p=[Math]::Min(1.0,[Math]::Max(0.0,($timeline-$letter[2])/9.0))
        $e=1-[Math]::Pow(1-$p,3)
        $red=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb([int](255*$p),240,20,20))
        $g.DrawString($letter[0],$font,$red,[single]($letter[1]-55*(1-$e)),[single]48)
        $red.Dispose()
    }
    $font.Dispose()
    $bitmap.Save((Join-Path $assetRoot ('intro-{0:D2}.png' -f $frame)),[Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bitmap.Dispose()
}
