param([string]$Classic = (Join-Path $PSScriptRoot "..\classic"), [string[]]$Only = @(), [switch]$Force)
# The HD look's other races: the Meshy remakes are painted red (the red race's), so for blue, green and yellow each
# colour map's red paint turns to the race's colour, and classic/meshy/models/<race>/ gets every red export with its
# maps pointing there (tools/race_models.py).
#
# Red paint is a pixel whose hue lies near red and that is clearly coloured; it fades out over a band of hue and of
# saturation, so worn edges blend. Orange flames, yellow stripes, black, metal and gold are left as they are. The hue
# turns to the race's (the colours the original tinted its machines with: red 1,0,0, blue 0,0.3,1, green
# 0.82,1,0, yellow 1,0.83,0); saturation and brightness stay, as the original's tint left the grey picture's shading.
#
#   powershell -File tools/race-recolour.ps1 [-Only grunt-1,knight-3] [-Force]
$Only = @($Only | ForEach-Object { $_ -split "," } | Where-Object { $_ })  # "-File" hands a list over as one string
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class RacePaint {
    // Hue in degrees from red within which paint counts fully, and where it has faded out.
    const double HueFull = 12, HueNone = 24;
    // Saturation below which a pixel is grey (metal, black, white), and above which it is paint.
    const double SatNone = 0.25, SatFull = 0.45;
    const double ValueNone = 0.06;

    static double Smooth(double edge0, double edge1, double x) {
        double t = Math.Max(0, Math.Min(1, (x - edge0) / (edge1 - edge0)));
        return t * t * (3 - 2 * t);
    }

    public static void Recolour(Bitmap b, double targetHue) {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        var px = new byte[d.Stride * b.Height];
        Marshal.Copy(d.Scan0, px, 0, px.Length);
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++) {
                int i = y * d.Stride + x * 3;
                double bl = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                double max = Math.Max(r, Math.Max(g, bl)), min = Math.Min(r, Math.Min(g, bl)), c = max - min;
                if (max < ValueNone || c <= 0) continue;
                double s = c / max;
                double h = max == r ? 60 * (((g - bl) / c) % 6) : max == g ? 60 * ((bl - r) / c + 2) : 60 * ((r - g) / c + 4);
                if (h < 0) h += 360;
                double fromRed = Math.Min(h, 360 - h);
                double w = (1 - Smooth(HueFull, HueNone, fromRed)) * Smooth(SatNone, SatFull, s);
                if (w <= 0) continue;
                // The same saturation and brightness at the race's hue.
                double hh = targetHue / 60, xx = c * (1 - Math.Abs(hh % 2 - 1)), m = max - c;
                double nr, ng, nb;
                if (hh < 1) { nr = c; ng = xx; nb = 0; }
                else if (hh < 2) { nr = xx; ng = c; nb = 0; }
                else if (hh < 3) { nr = 0; ng = c; nb = xx; }
                else if (hh < 4) { nr = 0; ng = xx; nb = c; }
                else if (hh < 5) { nr = xx; ng = 0; nb = c; }
                else { nr = c; ng = 0; nb = xx; }
                px[i + 2] = (byte)Math.Round(255 * (r + w * (nr + m - r)));
                px[i + 1] = (byte)Math.Round(255 * (g + w * (ng + m - g)));
                px[i] = (byte)Math.Round(255 * (bl + w * (nb + m - bl)));
            }
        Marshal.Copy(px, 0, d.Scan0, px.Length);
        b.UnlockBits(d);
    }
}
"@
# The races' hues: blue 0,0.3,1 is 222 degrees, green 0.82,1,0 is 71, yellow 1,0.83,0 is 50.
$races = [ordered]@{ blue = 222; green = 71; yellow = 50 }
$jpeg = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq "image/jpeg" }
$quality = New-Object System.Drawing.Imaging.EncoderParameters 1
$quality.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 92L
$made = 0
foreach ($folder in Get-ChildItem (Join-Path $Classic "meshy\textures") -Directory) {
    if ($Only.Count -and $folder.Name -notin $Only) { continue }
    $albedo = Get-ChildItem $folder.FullName -Filter "albedo.*" | Where-Object { $_.Extension -in ".jpg", ".png" } | Select-Object -First 1
    if (-not $albedo) { continue }
    foreach ($race in $races.Keys) {
        $target = Join-Path $folder.FullName "albedo-$race.jpg"
        if ((Test-Path $target) -and -not $Force -and (Get-Item $target).LastWriteTime -gt $albedo.LastWriteTime) { continue }
        $loaded = [System.Drawing.Image]::FromFile($albedo.FullName)
        $bmp = New-Object System.Drawing.Bitmap $loaded.Width, $loaded.Height, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp); $g.DrawImage($loaded, 0, 0, $loaded.Width, $loaded.Height); $g.Dispose(); $loaded.Dispose()
        [RacePaint]::Recolour($bmp, $races[$race])
        $bmp.Save($target, $jpeg, $quality)
        $bmp.Dispose()
        $made++
    }
}
"recoloured maps: $made"
