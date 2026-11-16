# Windows 11 Network Bandwidth Monitor

A lightweight, zero-dependency real-time network bandwidth and NetFlow monitor for Windows 10 / 11 built with C# (.NET Framework 4.x WinForms + Win32 IP Helper & ETW).

## Features
- **Real-time Download & Upload speeds** (1-second `Stopwatch`-normalized polling) with **Peak** and **Session Total** counters.
- **60-second dual-line area chart** with auto-scaling Y-axis.
- **Adapter selector** (`All Active Adapters` or any individual interface).
- **Per-Process / PID tracking** (select any running process or enter a PID; uses `Microsoft-Windows-Kernel-Network` ETW when running as Administrator).
- **Resizable NetFlow Sessions table** showing `Protocol`, `Process (PID)`, `Local Endpoint`, `Remote Peer`, `State`, `↓ Recv`, `↑ Sent`, and `↓/↑ Rate`.
- **Unit toggle** (`MB/s` ↔ `Mbps`), **Always on Top** (`Pin`), **Mini** compact widget mode (with **`Expand`** button / double-click / tray menu), **Pause/Resume**, **Reset**, and **System Tray** support.

## Quick Start
- **Run directly (no build needed):** Double-click `RunNetworkMonitor.bat`.
- **Build standalone `.exe`:** Double-click `build.bat` to compile `NetworkMonitor.exe` using the built-in .NET Framework 4.x `csc.exe` compiler.
- **Per-PID byte-rate tracking:** Click **`Admin (PID Speed)`** in the toolbar (or run as Administrator) to enable kernel ETW per-PID/per-flow counters.
