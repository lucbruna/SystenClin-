Add-Type -AssemblyName System.Windows.Forms,System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L,T,Rt,B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
}
"@
$procs = Get-Process Systenclin -ErrorAction SilentlyContinue
foreach($p in $procs){
  if($p.MainWindowHandle -ne 0){
    [W]::ShowWindow($p.MainWindowHandle, 9) | Out-Null   # SW_RESTORE
    [W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 600
    $r = New-Object W+R
    [W]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
    $w = $r.Rt - $r.L; $h = $r.B - $r.T
    "rect: $($r.L),$($r.T) size ${w}x${h}"
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $bmp.Save("janela.png", [System.Drawing.Imaging.ImageFormat]::Png)
    "salvo"
    break
  }
}
