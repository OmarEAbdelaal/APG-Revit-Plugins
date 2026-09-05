<#
.SYNOPSIS
    Generates all APG Revit Plugins brand assets (ribbon icons, dialog wordmarks,
    installer icon) from code, so the repo needs no binary source files.

.DESCRIPTION
    Output:
      src/CodeCompliance/Resources/*.png   ribbon button icons (16 + 32 px) and
                                           dialog header wordmarks
      installer/apg.ico                    multi-size installer/uninstaller icon

    Re-run after changing the drawing code, then commit the regenerated files.
    Requires Windows PowerShell (uses System.Drawing / GDI+).
#>

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$resDir   = Join-Path $repoRoot "src\CodeCompliance\Resources"
$icoPath  = Join-Path $repoRoot "installer\apg.ico"
New-Item -ItemType Directory -Force $resDir | Out-Null

# APG brand colors
$ApgBlue  = [System.Drawing.Color]::FromArgb(255, 0x27, 0x26, 0xA9)
$ApgNavy  = [System.Drawing.Color]::FromArgb(255, 0x17, 0x16, 0x66)
$White    = [System.Drawing.Color]::White

function New-Canvas([int]$size, [int]$height = 0) {
    if ($height -eq 0) { $height = $size }
    $bmp = New-Object System.Drawing.Bitmap($size, $height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)
    @{ Bmp = $bmp; G = $g }
}

function Save-Png($bmp, [string]$name) {
    $path = Join-Path $resDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "  $name.png"
}

function Resize($bmp, [int]$size) {
    $small = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($small)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.DrawImage($bmp, 0, 0, $size, $size)
    $g.Dispose()
    $small
}

# The APG "A" mark: a right-leaning triangle with a triangular counter (hole).
function Draw-Mark($g, [System.Drawing.Color]$color, [double]$x, [double]$y, [double]$s) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath([System.Drawing.Drawing2D.FillMode]::Alternate)
    $outer = @(
        (New-Object System.Drawing.PointF(($x + 0.02 * $s), ($y + 0.92 * $s))),
        (New-Object System.Drawing.PointF(($x + 0.68 * $s), ($y + 0.08 * $s))),
        (New-Object System.Drawing.PointF(($x + 0.95 * $s), ($y + 0.92 * $s)))
    )
    $inner = @(
        (New-Object System.Drawing.PointF(($x + 0.34 * $s), ($y + 0.78 * $s))),
        (New-Object System.Drawing.PointF(($x + 0.64 * $s), ($y + 0.36 * $s))),
        (New-Object System.Drawing.PointF(($x + 0.76 * $s), ($y + 0.78 * $s)))
    )
    $path.AddPolygon($outer)
    $path.AddPolygon($inner)
    $brush = New-Object System.Drawing.SolidBrush($color)
    $g.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose()
}

# Rounded blue tile used as the background of every ribbon icon.
function Draw-Tile($g, [int]$s) {
    $r = [int]($s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($s - 2 * $r - 1, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($s - 2 * $r - 1, $s - 2 * $r - 1, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $s - 2 * $r - 1, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point(0, $s)), $ApgBlue, $ApgNavy)
    $g.FillPath($grad, $path)
    $grad.Dispose(); $path.Dispose()
}

function New-Pen([System.Drawing.Color]$color, [double]$w) {
    $pen = New-Object System.Drawing.Pen($color, [float]$w)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pen
}

function P([double]$x, [double]$y) { New-Object System.Drawing.PointF([float]$x, [float]$y) }

# Draws one 128 px master icon, then saves the 32 and 16 px ribbon versions.
function New-Icon([string]$name, [scriptblock]$glyph) {
    $c = New-Canvas 128
    Draw-Tile $c.G 128
    & $glyph $c.G 128
    $c.G.Dispose()
    Save-Png (Resize $c.Bmp 32) "$($name)32"
    Save-Png (Resize $c.Bmp 16) "$($name)16"
    $c.Bmp
}

Write-Host "Generating ribbon icons..." -ForegroundColor Cyan
$wb = New-Object System.Drawing.SolidBrush($White)

# APG logo tile (About button + installer icon)
$logoTile = New-Icon "Apg" {
    param($g, $s)
    Draw-Mark $g $White (0.14 * $s) (0.12 * $s) (0.76 * $s)
}

# Escape Stairs: white steps going up
$null = New-Icon "EscapeStairs" {
    param($g, $s)
    $pts = @(
        (P (0.16*$s) (0.84*$s)); (P (0.16*$s) (0.66*$s)); (P (0.38*$s) (0.66*$s));
        (P (0.38*$s) (0.48*$s)); (P (0.60*$s) (0.48*$s)); (P (0.60*$s) (0.30*$s));
        (P (0.84*$s) (0.30*$s)); (P (0.84*$s) (0.84*$s))
    )
    $g.FillPolygon($wb, $pts)
}

# Travel Paths: white route polyline with arrowhead
$null = New-Icon "TravelPaths" {
    param($g, $s)
    $pen = New-Pen $White (0.09 * $s)
    $g.DrawLines($pen, @((P (0.18*$s) (0.82*$s)); (P (0.18*$s) (0.45*$s)); (P (0.55*$s) (0.45*$s)); (P (0.55*$s) (0.22*$s)); (P (0.78*$s) (0.22*$s))))
    $pen.Dispose()
    $g.FillPolygon($wb, @((P (0.72*$s) (0.10*$s)); (P (0.90*$s) (0.22*$s)); (P (0.72*$s) (0.34*$s))))
    $g.FillEllipse($wb, [float](0.11*$s), [float](0.75*$s), [float](0.14*$s), [float](0.14*$s))
}

# Egress Report: white document with lines
$null = New-Icon "EgressReport" {
    param($g, $s)
    $g.FillRectangle($wb, [float](0.24*$s), [float](0.14*$s), [float](0.52*$s), [float](0.72*$s))
    $bp = New-Object System.Drawing.SolidBrush($ApgBlue)
    foreach ($i in 0..3) {
        $g.FillRectangle($bp, [float](0.31*$s), [float]((0.26 + 0.14 * $i)*$s), [float](0.38*$s), [float](0.05*$s))
    }
    $bp.Dispose()
}

# Model Check: white check mark
$null = New-Icon "ModelCheck" {
    param($g, $s)
    $pen = New-Pen $White (0.13 * $s)
    $g.DrawLines($pen, @((P (0.20*$s) (0.55*$s)); (P (0.42*$s) (0.76*$s)); (P (0.80*$s) (0.26*$s))))
    $pen.Dispose()
}

# DM Compliance: white shield with a blue check (Dubai Municipality compliance)
$null = New-Icon "DmCompliance" {
    param($g, $s)
    $pts = @(
        (P (0.50*$s) (0.09*$s)); (P (0.86*$s) (0.23*$s)); (P (0.86*$s) (0.50*$s));
        (P (0.80*$s) (0.66*$s)); (P (0.68*$s) (0.80*$s)); (P (0.50*$s) (0.93*$s));
        (P (0.32*$s) (0.80*$s)); (P (0.20*$s) (0.66*$s)); (P (0.14*$s) (0.50*$s));
        (P (0.14*$s) (0.23*$s))
    )
    $g.FillPolygon($wb, $pts)
    $pen = New-Pen $ApgBlue (0.095 * $s)
    $g.DrawLines($pen, @((P (0.33*$s) (0.50*$s)); (P (0.45*$s) (0.63*$s)); (P (0.69*$s) (0.36*$s))))
    $pen.Dispose()
}

# DM Report: white document with blue lines and a check
$null = New-Icon "DmReport" {
    param($g, $s)
    $g.FillPolygon($wb, @(
        (P (0.24*$s) (0.12*$s)); (P (0.68*$s) (0.12*$s)); (P (0.78*$s) (0.24*$s));
        (P (0.78*$s) (0.88*$s)); (P (0.24*$s) (0.88*$s))
    ))
    $pen = New-Pen $ApgBlue (0.05 * $s)
    foreach ($i in 0..2) {
        $y = (0.34 + 0.13 * $i) * $s
        $g.DrawLine($pen, [float](0.33*$s), [float]$y, [float](0.62*$s), [float]$y)
    }
    $pen.Dispose()
    $pen2 = New-Pen $ApgBlue (0.075 * $s)
    $g.DrawLines($pen2, @((P (0.33*$s) (0.73*$s)); (P (0.42*$s) (0.81*$s)); (P (0.66*$s) (0.58*$s))))
    $pen2.Dispose()
}

# Parking Ramp: white ramp wedge with a rising arrow
$null = New-Icon "ParkingRamp" {
    param($g, $s)
    $g.FillPolygon($wb, @((P (0.12*$s) (0.84*$s)); (P (0.88*$s) (0.32*$s)); (P (0.88*$s) (0.84*$s))))
    $pen = New-Pen $White (0.08 * $s)
    $g.DrawLine($pen, [float](0.18*$s), [float](0.60*$s), [float](0.52*$s), [float](0.32*$s))
    $pen.Dispose()
    $g.FillPolygon($wb, @((P (0.44*$s) (0.20*$s)); (P (0.62*$s) (0.24*$s)); (P (0.50*$s) (0.40*$s))))
}

# Magic Annotation: dimension line with end ticks + a sparkle
$null = New-Icon "MagicAnnotation" {
    param($g, $s)
    $pen = New-Pen $White (0.07 * $s)
    # dimension line with witness lines
    $g.DrawLine($pen, [float](0.14*$s), [float](0.72*$s), [float](0.86*$s), [float](0.72*$s))
    $g.DrawLine($pen, [float](0.14*$s), [float](0.60*$s), [float](0.14*$s), [float](0.84*$s))
    $g.DrawLine($pen, [float](0.86*$s), [float](0.60*$s), [float](0.86*$s), [float](0.84*$s))
    # oblique dimension ticks
    $g.DrawLine($pen, [float](0.08*$s), [float](0.78*$s), [float](0.20*$s), [float](0.66*$s))
    $g.DrawLine($pen, [float](0.80*$s), [float](0.78*$s), [float](0.92*$s), [float](0.66*$s))
    $pen.Dispose()
    # four-point sparkle above the line
    $cx = 0.50 * $s; $cy = 0.32 * $s; $r = 0.20 * $s; $w = 0.055 * $s
    $g.FillPolygon($wb, @(
        (P $cx ($cy - $r)); (P ($cx + $w) ($cy - $w)); (P ($cx + $r) $cy); (P ($cx + $w) ($cy + $w));
        (P $cx ($cy + $r)); (P ($cx - $w) ($cy + $w)); (P ($cx - $r) $cy); (P ($cx - $w) ($cy - $w))
    ))
}

# MCP Server: white plug (two prongs + body + cable) - the Claude <-> Revit connection
$null = New-Icon "McpServer" {
    param($g, $s)
    $pen = New-Pen $White (0.09 * $s)
    # prongs
    $g.DrawLine($pen, [float](0.38*$s), [float](0.12*$s), [float](0.38*$s), [float](0.34*$s))
    $g.DrawLine($pen, [float](0.62*$s), [float](0.12*$s), [float](0.62*$s), [float](0.34*$s))
    # cable
    $g.DrawLine($pen, [float](0.50*$s), [float](0.70*$s), [float](0.50*$s), [float](0.90*$s))
    $pen.Dispose()
    # body: rounded block
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x = 0.24*$s; $y = 0.32*$s; $w = 0.52*$s; $h = 0.40*$s; $r = 0.10*$s
    $path.AddArc([float]$x, [float]$y, [float](2*$r), [float](2*$r), 180, 90)
    $path.AddArc([float]($x+$w-2*$r), [float]$y, [float](2*$r), [float](2*$r), 270, 90)
    $path.AddArc([float]($x+$w-2*$r), [float]($y+$h-2*$r), [float](2*$r), [float](2*$r), 0, 90)
    $path.AddArc([float]$x, [float]($y+$h-2*$r), [float](2*$r), [float](2*$r), 90, 90)
    $path.CloseFigure()
    $g.FillPath($wb, $path); $path.Dispose()
}

# MCP Setup: white gear
$null = New-Icon "McpSetup" {
    param($g, $s)
    $cx = 0.50*$s; $cy = 0.50*$s; $ro = 0.36*$s; $ri = 0.27*$s
    $pts = @()
    for ($i = 0; $i -lt 16; $i++) {
        $a = ($i / 16.0) * 2 * [math]::PI
        $rr = if ($i % 2 -eq 0) { $ro } else { $ri }
        $pts += (P ($cx + $rr * [math]::Cos($a)) ($cy + $rr * [math]::Sin($a)))
        $a2 = (($i + 0.5) / 16.0) * 2 * [math]::PI
        $pts += (P ($cx + $rr * [math]::Cos($a2)) ($cy + $rr * [math]::Sin($a2)))
    }
    $g.FillPolygon($wb, $pts)
    $bp = New-Object System.Drawing.SolidBrush($ApgBlue)
    $g.FillEllipse($bp, [float]($cx - 0.12*$s), [float]($cy - 0.12*$s), [float](0.24*$s), [float](0.24*$s))
    $bp.Dispose()
}

# About: white info circle
$null = New-Icon "About" {
    param($g, $s)
    $pen = New-Pen $White (0.08 * $s)
    $g.DrawEllipse($pen, [float](0.20*$s), [float](0.20*$s), [float](0.60*$s), [float](0.60*$s))
    $pen.Dispose()
    $g.FillEllipse($wb, [float](0.46*$s), [float](0.32*$s), [float](0.09*$s), [float](0.09*$s))
    $g.FillRectangle($wb, [float](0.465*$s), [float](0.47*$s), [float](0.08*$s), [float](0.22*$s))
}

# Wordmarks for the WPF dialog headers: APG mark + "PG" letters.
function New-Wordmark([string]$name, [System.Drawing.Color]$color) {
    $c = New-Canvas 400 200
    Draw-Mark $c.G $color 8 16 176
    $font = New-Object System.Drawing.Font("Arial", 112, ([System.Drawing.FontStyle]::Bold -bor [System.Drawing.FontStyle]::Italic), [System.Drawing.GraphicsUnit]::Pixel)
    $brush = New-Object System.Drawing.SolidBrush($color)
    $c.G.DrawString("PG", $font, $brush, 178, 36)
    $font.Dispose(); $brush.Dispose(); $c.G.Dispose()
    Save-Png $c.Bmp $name
    $c.Bmp.Dispose()
}
New-Wordmark "ApgWordmark"      $ApgBlue
New-Wordmark "ApgWordmarkWhite" $White

# --- Installer icon (.ico): classic 32-bit BMP entries (Inno Setup's resource
# updater rejects PNG-compressed entries, so plain DIBs for every size) --------
Write-Host "Generating installer icon..." -ForegroundColor Cyan

function Get-IcoBmpEntry($bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER (height doubled: XOR + AND mask)
    $bw.Write([int32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2))
    $bw.Write([int16]1);  $bw.Write([int16]32); $bw.Write([int32]0)
    $bw.Write([int32]($w * $h * 4)); $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([int32]0);  $bw.Write([int32]0)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $locked = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = New-Object byte[] ($w * 4)
    for ($y = $h - 1; $y -ge 0; $y--) {
        $src = [IntPtr]::Add($locked.Scan0, $y * $locked.Stride)
        [System.Runtime.InteropServices.Marshal]::Copy($src, $row, 0, $row.Length)
        $bw.Write($row)
    }
    $bmp.UnlockBits($locked)
    $maskRow = [int][math]::Ceiling($w / 32.0) * 4
    $bw.Write((New-Object byte[] ($maskRow * $h)))
    $bw.Flush()
    # The comma keeps the byte[] intact instead of unrolling it into the pipeline.
    ,$ms.ToArray()
}

$entries = @()
foreach ($sz in @(16, 32, 48, 256)) {
    $entries += ,@{ Size = $sz; Data = [byte[]](Get-IcoBmpEntry (Resize $logoTile $sz)) }
}

$fs = [System.IO.File]::Create($icoPath)
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int32]([byte[]]$e.Data).Length); $w.Write([int32]$offset)
    $offset += ([byte[]]$e.Data).Length
}
foreach ($e in $entries) { $w.Write([byte[]]$e.Data) }
$w.Flush(); $fs.Close()
Write-Host "  installer\apg.ico"

$wb.Dispose(); $logoTile.Dispose()
Write-Host "Done." -ForegroundColor Green
