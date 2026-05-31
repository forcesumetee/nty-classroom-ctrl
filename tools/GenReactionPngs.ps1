# Phase 22.3-C — generate 5 color reaction emoji PNGs using GDI+ shape
# primitives.  Drawing emoji as shapes (rather than as a font glyph)
# sidesteps every WPF / DirectWrite font-resolution bug on the user's
# Win11 + Thai-locale environment.  Output: 48x48 PNG with transparency.
#
# This script is run ONCE at commit time; the generated PNGs are
# committed alongside the XAML that consumes them.

Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot '..\src\ClassroomCtrl.Shared.Wpf\Resources\Reactions'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force $outDir | Out-Null }

# Shared palette
$yellow   = [System.Drawing.Color]::FromArgb(255, 251, 188, 4)   # Conf accent yellow #FBBC04
$yellowD  = [System.Drawing.Color]::FromArgb(255, 230, 168, 0)   # Slightly darker for outline
$red      = [System.Drawing.Color]::FromArgb(255, 234, 67, 53)   # Conf accent red #EA4335
$redD     = [System.Drawing.Color]::FromArgb(255, 200, 50, 40)
$ink      = [System.Drawing.Color]::FromArgb(255, 32, 32, 32)    # Near-black for facial features
$white    = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
$transparent = [System.Drawing.Color]::Transparent

function New-Bitmap48 {
    $bmp = New-Object System.Drawing.Bitmap 48, 48
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode  = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear($transparent)
    return @{ Bmp = $bmp; G = $g }
}

function Save-Png($pack, $name) {
    $path = Join-Path $outDir $name
    $pack.Bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $pack.G.Dispose()
    $pack.Bmp.Dispose()
    Write-Output "wrote $path ($((Get-Item $path).Length) bytes)"
}

# ──────────────── 1. Thumbs up (thumbs_up.png) ────────────────
# Yellow circle background + simplified white thumb-up icon.
$p = New-Bitmap48
$g = $p.G
$brushY = New-Object System.Drawing.SolidBrush $yellow
$brushW = New-Object System.Drawing.SolidBrush $white
$brushI = New-Object System.Drawing.SolidBrush $ink
$penYD = New-Object System.Drawing.Pen ($yellowD, 1.5)

# Yellow disc
$g.FillEllipse($brushY, 1, 1, 46, 46)
$g.DrawEllipse($penYD, 1, 1, 46, 46)

# Thumb-up shape (simplified) — white fill on yellow
# Fist body: rectangle with rounded corners
$fist = New-Object System.Drawing.RectangleF (14, 22, 14, 16)
$g.FillRectangle($brushW, $fist)
# Thumb extending up
$thumbPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$thumbPath.AddPolygon(@(
    (New-Object System.Drawing.PointF (16, 22)),
    (New-Object System.Drawing.PointF (18, 14)),
    (New-Object System.Drawing.PointF (22, 12)),
    (New-Object System.Drawing.PointF (25, 14)),
    (New-Object System.Drawing.PointF (25, 22))
))
$g.FillPath($brushW, $thumbPath)
$thumbPath.Dispose()

# Wrist band at bottom
$wrist = New-Object System.Drawing.RectangleF (12, 32, 18, 5)
$g.FillRectangle($brushI, $wrist)

Save-Png $p 'thumbs_up.png'
$brushY.Dispose(); $brushW.Dispose(); $brushI.Dispose(); $penYD.Dispose()

# ──────────────── 2. Heart (heart.png) ────────────────
# Solid red heart with darker outline.
$p = New-Bitmap48
$g = $p.G
$brushR = New-Object System.Drawing.SolidBrush $red
$penRD = New-Object System.Drawing.Pen ($redD, 1.5)

$heartPath = New-Object System.Drawing.Drawing2D.GraphicsPath
# Left lobe (Bezier-approximated by two ellipses)
$g.FillEllipse($brushR, 7, 8, 18, 18)
$g.FillEllipse($brushR, 23, 8, 18, 18)
# Triangle for the bottom point
$heartPath.AddPolygon(@(
    (New-Object System.Drawing.PointF (8, 19)),
    (New-Object System.Drawing.PointF (40, 19)),
    (New-Object System.Drawing.PointF (24, 42))
))
$g.FillPath($brushR, $heartPath)
$heartPath.Dispose()

Save-Png $p 'heart.png'
$brushR.Dispose(); $penRD.Dispose()

# ──────────────── 3. Laugh (laugh.png) ────────────────
# Yellow disc + closed eyes (curved arcs) + wide open smile.
$p = New-Bitmap48
$g = $p.G
$brushY = New-Object System.Drawing.SolidBrush $yellow
$brushI = New-Object System.Drawing.SolidBrush $ink
$penYD = New-Object System.Drawing.Pen ($yellowD, 1.5)
$penI3 = New-Object System.Drawing.Pen ($ink, 2.5)
$penI3.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$penI3.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

$g.FillEllipse($brushY, 1, 1, 46, 46)
$g.DrawEllipse($penYD, 1, 1, 46, 46)

# Closed/squinting eyes — two upward arcs
$g.DrawArc($penI3, 12, 16, 8, 6, 200, 140)
$g.DrawArc($penI3, 28, 16, 8, 6, 200, 140)

# Wide open smile — filled black with white interior accent
$smileOuter = New-Object System.Drawing.Drawing2D.GraphicsPath
$smileOuter.AddArc(13, 26, 22, 16, 0, 180)
$smileOuter.CloseFigure()
$g.FillPath($brushI, $smileOuter)
$smileOuter.Dispose()

Save-Png $p 'laugh.png'
$brushY.Dispose(); $brushI.Dispose(); $penYD.Dispose(); $penI3.Dispose()

# ──────────────── 4. Surprise (surprise.png) ────────────────
# Yellow disc + two dot eyes + small round open mouth (O shape).
$p = New-Bitmap48
$g = $p.G
$brushY = New-Object System.Drawing.SolidBrush $yellow
$brushI = New-Object System.Drawing.SolidBrush $ink
$penYD = New-Object System.Drawing.Pen ($yellowD, 1.5)

$g.FillEllipse($brushY, 1, 1, 46, 46)
$g.DrawEllipse($penYD, 1, 1, 46, 46)

# Eyes — round dots
$g.FillEllipse($brushI, 14, 17, 5, 5)
$g.FillEllipse($brushI, 29, 17, 5, 5)

# "O" mouth — solid filled circle for surprise expression
$g.FillEllipse($brushI, 20, 28, 8, 10)

Save-Png $p 'surprise.png'
$brushY.Dispose(); $brushI.Dispose(); $penYD.Dispose()

# ──────────────── 5. Sad (sad.png) ────────────────
# Yellow disc + dot eyes + downward arc frown + small blue tear.
$p = New-Bitmap48
$g = $p.G
$brushY = New-Object System.Drawing.SolidBrush $yellow
$brushI = New-Object System.Drawing.SolidBrush $ink
$brushBlue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 66, 133, 244))
$penYD = New-Object System.Drawing.Pen ($yellowD, 1.5)
$penI3 = New-Object System.Drawing.Pen ($ink, 2.5)
$penI3.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$penI3.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

$g.FillEllipse($brushY, 1, 1, 46, 46)
$g.DrawEllipse($penYD, 1, 1, 46, 46)

# Sad eyes — small dots
$g.FillEllipse($brushI, 14, 17, 5, 5)
$g.FillEllipse($brushI, 29, 17, 5, 5)

# Frown — downward arc
$g.DrawArc($penI3, 16, 31, 16, 10, 180, 180)

# Tear — small blue teardrop from left eye
$tearPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$tearPath.AddPolygon(@(
    (New-Object System.Drawing.PointF (15, 24)),
    (New-Object System.Drawing.PointF (12, 32)),
    (New-Object System.Drawing.PointF (18, 32))
))
$g.FillPath($brushBlue, $tearPath)
$g.FillEllipse($brushBlue, 12, 30, 6, 5)
$tearPath.Dispose()

Save-Png $p 'sad.png'
$brushY.Dispose(); $brushI.Dispose(); $brushBlue.Dispose(); $penYD.Dispose(); $penI3.Dispose()

Write-Output 'done'
