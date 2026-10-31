param([string]$ClassName = "MiniLaunchWnd")
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class T {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
}
"@
$found = [IntPtr]::Zero
$cb = [T+EnumProc]{
  param($h, $l)
  $sb = New-Object System.Text.StringBuilder 128
  [void][T]::GetClassNameW($h, $sb, 128)
  if ($sb.ToString() -eq $ClassName) { $script:found = $h; return $false }
  return $true
}
[void][T]::EnumWindows($cb, [IntPtr]::Zero)
$h = $script:found
if ($h -eq [IntPtr]::Zero) { Write-Output "NOT_FOUND"; exit 1 }
$v0 = [T]::IsWindowVisible($h)
[void][T]::SendMessageW($h, 0x0312, [IntPtr]1, [IntPtr]::Zero)
Start-Sleep -Milliseconds 250
$v1 = [T]::IsWindowVisible($h)
[void][T]::SendMessageW($h, 0x0312, [IntPtr]1, [IntPtr]::Zero)
Start-Sleep -Milliseconds 250
$v2 = [T]::IsWindowVisible($h)
Write-Output "WM_HOTKEY toggle: $v0 -> $v1 -> $v2"
