# Prints a small JPEG of a screenshot as base64 into the CI log, so results can be inspected
# without downloading artifacts.
function Print-Screenshot([string]$path, [string]$label, [int]$width) {
  Add-Type -AssemblyName System.Drawing
  if (-not (Test-Path $path)) { Write-Host "NO SCREENSHOT: $path"; return }
  $src = [System.Drawing.Image]::FromFile($path)
  $h = [int]($src.Height * $width / $src.Width)
  $bmp = New-Object System.Drawing.Bitmap $width, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.DrawImage($src, 0, 0, $width, $h)
  $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
  $ep = New-Object System.Drawing.Imaging.EncoderParameters 1
  $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 55L
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, $codec, $ep)
  Write-Host ("SCREENSHOT_" + $label + "_BASE64:" + [Convert]::ToBase64String($ms.ToArray()))
  $g.Dispose(); $bmp.Dispose(); $src.Dispose()
}
