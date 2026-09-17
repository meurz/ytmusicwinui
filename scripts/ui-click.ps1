[CmdletBinding()]
param([Parameter(Mandatory=$true)][int]$AppProcessId, [string]$AutomationId = '', [string]$Name = '', [string]$Text = '', [switch]$Enter)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MusicInput {
  [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point p);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
}
'@
$p=Get-Process -Id $AppProcessId
if ($p.ProcessName -ne 'ytmusicwinui') { throw 'Only ytmusicwinui can be controlled by this script.' }
[MusicInput]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
$root=[Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
if ($AutomationId) { $condition=New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::AutomationIdProperty),$AutomationId }
elseif ($Name) { $condition=New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty),$Name }
else { throw 'An AutomationId or Name is required.' }
$element=$root.FindAll([Windows.Automation.TreeScope]::Descendants,$condition) | Where-Object { !$_.Current.IsOffscreen -and $_.Current.IsEnabled } | Select-Object -First 1
if (!$element) { throw 'An enabled visible matching control was not found.' }
$r=$element.Current.BoundingRectangle
$old=New-Object MusicInput+Point
[MusicInput]::GetCursorPos([ref]$old) | Out-Null
try {
 [MusicInput]::SetCursorPos([int]($r.Left+$r.Width/2),[int]($r.Top+$r.Height/2)) | Out-Null
 [MusicInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
 [MusicInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 if ($Text) { [Windows.Forms.SendKeys]::SendWait('^a'); [Windows.Forms.SendKeys]::SendWait($Text) }
 if ($Enter) { [Windows.Forms.SendKeys]::SendWait('{ENTER}') }
} finally { [MusicInput]::SetCursorPos($old.X,$old.Y) | Out-Null }
Write-Output 'Control activated.'
