# Peripheral Battery

Double-click **PeripheralBattery.exe** on Windows 10 or 11. The app runs in the notification area with two icons. The black-and-white mouse and keyboard icons show a charge bar below each symbol. Hover over either icon for the exact percentage. A dash inside the bar means no reading is available.

If you find the icons behind the taskbar arrow, open the overflow and drag both icons onto the visible taskbar. On Windows 11 you can also use **Settings → Personalization → Taskbar → Other system tray icons**. Windows controls their exact placement.

Hover for a summary or right-click either icon for individual device readings, manual refresh, taskbar settings, and exit. The app refreshes every minute. Each icon shows the lowest available percentage if several devices of that type are detected.

## Compatibility

The app reads the battery percentage Windows exposes for connected Bluetooth, USB and receiver devices. It also queries the documented battery status interface on supported AJAZZ/Attack Shark 2.4 GHz receivers with USB ID 3151:402D. The app shows a live reading when available. If the receiver returns a retained percentage while the mouse is idle, the tooltip and tray menu label it as last reported. The app also keeps the last valid reading for that receiver during the current session if a later query fails. Many other 2.4 GHz receivers use model-specific protocols, and battery-free wired devices have no charge level.

References for [Windows notification area behavior](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area), the [Windows battery property](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-devices-batterylife), and the [Rongyuan status report](https://github.com/qu1ncky/BatteryTray#how-it-works).

## Build and diagnose

From PowerShell in this folder, run:

```powershell
.\Build.ps1
.\Build.ps1 -Console
.\PeripheralBattery-Diagnostics.exe --list
```

The build uses the Windows .NET Framework C# compiler. The console build is optional.



