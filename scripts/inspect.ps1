[CmdletBinding()]
param([Parameter(Mandatory=$true)][int]$AppProcessId, [string]$Screenshot = '')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MusicInspection {
  [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out Rect rect);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref Point point);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
}
'@
[MusicInspection]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$process = Get-Process -Id $AppProcessId
if ($process.ProcessName -ne 'ytmusicwinui') { throw 'Only ytmusicwinui can be inspected by this script.' }
$handle = $process.MainWindowHandle
if (!$handle) { throw 'The application has no visible window.' }
[MusicInspection]::ShowWindow($handle,9) | Out-Null
[MusicInspection]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 600
$window = [Windows.Automation.AutomationElement]::FromHandle($handle)
$controls = $window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
# Never enumerate password values or the clipboard.
$controls | ForEach-Object { [pscustomobject]@{Type=$_.Current.ControlType.ProgrammaticName;Name=$_.Current.Name;Id=$_.Current.AutomationId;Enabled=$_.Current.IsEnabled;Offscreen=$_.Current.IsOffscreen} } | ConvertTo-Json -Depth 3
if ($Screenshot) {
  $rectangle = New-Object MusicInspection+Rect
  [MusicInspection]::GetClientRect($handle,[ref]$rectangle) | Out-Null
  $origin = New-Object MusicInspection+Point
  [MusicInspection]::ClientToScreen($handle,[ref]$origin) | Out-Null
  $bitmap = New-Object Drawing.Bitmap ($rectangle.Right-$rectangle.Left),($rectangle.Bottom-$rectangle.Top)
  $graphics = [Drawing.Graphics]::FromImage($bitmap)
  try { $graphics.CopyFromScreen($origin.X,$origin.Y,0,0,$bitmap.Size); $bitmap.Save($Screenshot,[Drawing.Imaging.ImageFormat]::Png) }
  finally { $graphics.Dispose(); $bitmap.Dispose(); }
}
