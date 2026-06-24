<#
.SYNOPSIS
    Generate a thin "document" style .ico for the .md file association.
.DESCRIPTION
    Renders a light page with a folded top-right corner and a markdown
    "M v" glyph, at multiple resolutions, then packs them into a single
    PNG-compressed .ico (Vista+). Output: assets\md-document.ico
#>
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$outIco = Join-Path $PSScriptRoot 'md-document.ico'

function New-PageBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Page geometry: tall, narrow margins -> "thin document" look.
    $marginX = [math]::Round($s * 0.20)
    $marginY = [math]::Round($s * 0.08)
    $pw = $s - 2 * $marginX
    $ph = $s - 2 * $marginY
    $fold = [math]::Round($pw * 0.34)   # folded corner size

    # Page body path (top-right corner cut for the fold).
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $L = $marginX; $T = $marginY; $R = $marginX + $pw; $B = $marginY + $ph
    $path.AddLine($L, $T, ($R - $fold), $T)
    $path.AddLine(($R - $fold), $T, $R, ($T + $fold))
    $path.AddLine($R, ($T + $fold), $R, $B)
    $path.AddLine($R, $B, $L, $B)
    $path.CloseFigure()

    # Fill page + thin outline.
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 250, 250, 252))
    $g.FillPath($fill, $path)
    $penW = [math]::Max(1.0, $s / 64.0)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 120, 124, 132)), $penW
    $pen.LineJoin = 'Round'
    $g.DrawPath($pen, $path)

    # Folded corner triangle.
    $tri = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tri.AddLine(($R - $fold), $T, ($R - $fold), ($T + $fold))
    $tri.AddLine(($R - $fold), ($T + $fold), $R, ($T + $fold))
    $tri.CloseFigure()
    $foldFill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 218, 222, 228))
    $g.FillPath($foldFill, $tri)
    $g.DrawPath($pen, $tri)

    # Markdown glyph: "M" + down chevron, drawn with strokes (crisp at small sizes).
    if ($s -ge 24) {
        $accent = [System.Drawing.Color]::FromArgb(255, 60, 120, 215)
        $gpen = New-Object System.Drawing.Pen $accent, ([math]::Max(1.5, $s / 18.0))
        $gpen.StartCap = 'Round'; $gpen.EndCap = 'Round'; $gpen.LineJoin = 'Round'

        $cx  = $L + $pw * 0.5
        $gy  = $T + $ph * 0.52        # glyph top
        $gh  = $ph * 0.26             # glyph height
        $gb  = $gy + $gh             # glyph bottom
        $mw  = $pw * 0.30
        # "M": left stroke, V to middle, up to right peak, right stroke down.
        $x0  = $cx - $mw * 0.95
        $x1  = $cx - $mw * 0.30
        $xm  = ($x0 + $x1) / 2
        $g.DrawLine($gpen, $x0, $gb, $x0, $gy)        # left up
        $g.DrawLine($gpen, $x0, $gy, $xm, ($gy + $gh * 0.55))  # down to valley
        $g.DrawLine($gpen, $xm, ($gy + $gh * 0.55), $x1, $gy)  # up to peak
        $g.DrawLine($gpen, $x1, $gy, $x1, $gb)        # right down
        # down chevron (the markdown arrow)
        $ax  = $cx + $mw * 0.55
        $av  = $mw * 0.42
        $g.DrawLine($gpen, $ax, $gy, $ax, $gb)                 # arrow stem
        $g.DrawLine($gpen, ($ax - $av), ($gb - $av), $ax, $gb) # left wing
        $g.DrawLine($gpen, $ax, $gb, ($ax + $av), ($gb - $av)) # right wing
        $gpen.Dispose()
    } else {
        # Too small for glyph: draw 3 text lines to read as a document.
        $lpen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 150, 154, 162)), 1.0
        $ly = $T + $ph * 0.45
        for ($i = 0; $i -lt 3; $i++) {
            $yy = $ly + $i * ($ph * 0.16)
            $g.DrawLine($lpen, ($L + $pw * 0.22), $yy, ($R - $pw * 0.22), $yy)
        }
        $lpen.Dispose()
    }

    $g.Dispose()
    return $bmp
}

# Render each size to PNG bytes.
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-PageBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
    $bmp.Dispose(); $ms.Dispose()
}

# Pack into ICO container (PNG-compressed entries).
$fs = New-Object System.IO.FileStream($outIco, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)                 # reserved
$bw.Write([uint16]1)                 # type = icon
$bw.Write([uint16]$frames.Count)     # count

$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([byte]$dim)            # width  (0 => 256)
    $bw.Write([byte]$dim)            # height (0 => 256)
    $bw.Write([byte]0)               # palette
    $bw.Write([byte]0)               # reserved
    $bw.Write([uint16]1)             # color planes
    $bw.Write([uint16]32)            # bpp
    $bw.Write([uint32]$f.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes) }
$bw.Flush(); $bw.Close(); $fs.Close()

Write-Host "Wrote $outIco ($((Get-Item $outIco).Length) bytes, $($frames.Count) frames)"
