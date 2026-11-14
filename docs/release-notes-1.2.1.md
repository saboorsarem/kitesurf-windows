# Kitesurf Browser 1.2.1 for Windows

Icon reliability.

The application icon is the upstream Kitesurf logo, embedded in the executable at seven sizes
(16–256 px). This build additionally sets `Window.Icon` explicitly, so the title bar, taskbar and
Alt-Tab entry come from the logo asset rather than from whatever the shell has cached for the file
path.

If Explorer still shows the older blue mark for `KitesurfBrowser.exe`, the executable you are looking
at predates the logo change (anything before 1.1.1) or Windows is serving a cached thumbnail:

```powershell
ie4uinit.exe -show          # asks the shell to rebuild its icon cache
```

Replacing the file at a new path, or restarting Explorer, also clears it. Verifying the binary itself
is unambiguous:

```powershell
Add-Type -AssemblyName System.Drawing
$i = [System.Drawing.Icon]::ExtractAssociatedIcon("$PWD\KitesurfBrowser.exe")
$b = $i.ToBitmap(); $b.GetPixel($b.Width/2, $b.Height/2)   # orange (255, 96, 49), not blue
```

No behavioural changes: connection handling, reconnect, discovery, telemetry, the CDP inspector and
Cloudflare Access support are identical to 1.2.0. `--selftest` passes its 19 checks.
