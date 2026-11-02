# Значок программы (src\PulseBar\PulseBar.ico): тот же рисунок, что значок в трее (Overlay.MakeTrayIcon) —
# тёмный скруглённый квадрат и зелёная линия пульса, — во всех размерах, которые берут «Пуск», Проводник и панель задач.
# Запуск: powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\PulseBar\PulseBar.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

function Draw-Frame([int]$n) {
	$k = $n / 16.0
	$bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
	$g = [System.Drawing.Graphics]::FromImage($bmp)
	$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
	$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
	$g.Clear([System.Drawing.Color]::Transparent)
	$r = 3.5 * $k
	$e = $n - 1
	$path = New-Object System.Drawing.Drawing2D.GraphicsPath
	$path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
	$path.AddArc($e - $r * 2, 0, $r * 2, $r * 2, 270, 90)
	$path.AddArc($e - $r * 2, $e - $r * 2, $r * 2, $r * 2, 0, 90)
	$path.AddArc(0, $e - $r * 2, $r * 2, $r * 2, 90, 90)
	$path.CloseFigure()
	$fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(28, 32, 38))
	$g.FillPath($fill, $path)
	# Тонкая светлая рамка: в тёмном меню «Пуск» тёмный квадрат без неё сливается с фоном.
	$edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(78, 88, 102)), ([Math]::Max(1.0, 0.55 * $k))
	$g.DrawPath($edge, $path)
	$edge.Dispose()
	$pts = @(
		(New-Object System.Drawing.PointF (1.8 * $k), (8.5 * $k)),
		(New-Object System.Drawing.PointF (5.0 * $k), (8.5 * $k)),
		(New-Object System.Drawing.PointF (6.6 * $k), (3.6 * $k)),
		(New-Object System.Drawing.PointF (9.2 * $k), (12.6 * $k)),
		(New-Object System.Drawing.PointF (10.8 * $k), (6.6 * $k)),
		(New-Object System.Drawing.PointF (11.8 * $k), (8.5 * $k)),
		(New-Object System.Drawing.PointF (14.2 * $k), (8.5 * $k))
	)
	$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(61, 220, 132)), (1.7 * $k)
	$pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
	$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
	$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
	$g.DrawLines($pen, [System.Drawing.PointF[]]$pts)
	$pen.Dispose(); $fill.Dispose(); $path.Dispose(); $g.Dispose()
	$ms = New-Object System.IO.MemoryStream
	if ($n -ge 256) {
		$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
	} else {
		# Малые размеры — кадр BMP (BGRA снизу вверх и маска AND): его читают все, включая старый GDI+.
		$bw = New-Object System.IO.BinaryWriter $ms
		$mask = [int]([Math]::Ceiling($n / 32.0) * 4)
		$bw.Write([uint32]40); $bw.Write([int32]$n); $bw.Write([int32]($n * 2))
		$bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
		$bw.Write([uint32]($n * $n * 4 + $mask * $n)); $bw.Write([int32]0); $bw.Write([int32]0)
		$bw.Write([uint32]0); $bw.Write([uint32]0)
		for ($y = $n - 1; $y -ge 0; $y--) {
			for ($x = 0; $x -lt $n; $x++) {
				$c = $bmp.GetPixel($x, $y)
				$bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
			}
		}
		$bw.Write((New-Object byte[] ($mask * $n)))
		$bw.Flush()
	}
	$bmp.Dispose()
	return ,$ms.ToArray()
}

# ICO: заголовок, по записи на размер, затем кадры (BMP, 256 px — PNG).
$frames = foreach ($n in $sizes) { ,(Draw-Frame $n) }
$f = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $f
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
	$n = $sizes[$i]
	$b = if ($n -ge 256) { 0 } else { $n }
	$w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
	$w.Write([uint16]1); $w.Write([uint16]32)
	$w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
	$offset += $frames[$i].Length
}
foreach ($fr in $frames) { $w.Write($fr) }
$w.Flush()
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($out), $f.ToArray())
Write-Output "$out ($($sizes -join ', ') px)"
