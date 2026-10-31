param([string]$Out = "C:\temp\ml-shot.png", [string]$ClassName = "MiniLaunchWnd")

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, IntPtr r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
"@

$found = [IntPtr]::Zero
$cb = [W+EnumProc]{
  param($h, $l)
  $sb = New-Object System.Text.StringBuilder 128
  [void][W]::GetClassNameW($h, $sb, 128)
  if ($sb.ToString() -eq $ClassName) { $script:found = $h; return $false }
  return $true
}
[void][W]::EnumWindows($cb, [IntPtr]::Zero)
$h = $script:found
if ($h -eq [IntPtr]::Zero) { Write-Output "WINDOW_NOT_FOUND"; exit 1 }

$buf = [System.Runtime.InteropServices.Marshal]::AllocHGlobal(16)
[void][W]::GetWindowRect($h, $buf)
$l = [System.Runtime.InteropServices.Marshal]::ReadInt32($buf, 0)
$t = [System.Runtime.InteropServices.Marshal]::ReadInt32($buf, 4)
$r = [System.Runtime.InteropServices.Marshal]::ReadInt32($buf, 8)
$b = [System.Runtime.InteropServices.Marshal]::ReadInt32($buf, 12)
[System.Runtime.InteropServices.Marshal]::FreeHGlobal($buf)
$w = $r - $l; $hgt = $b - $t
Write-Output "hwnd=$h rect=$l,$t size=${w}x${hgt} visible=$([W]::IsWindowVisible($h))"
if ($w -le 0 -or $hgt -le 0) { Write-Output "BAD_RECT"; exit 1 }

$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[void][W]::PrintWindow($h, $hdc, 0)
$g.ReleaseHdc($hdc)
$dir = Split-Path $Out -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "saved=$Out"
