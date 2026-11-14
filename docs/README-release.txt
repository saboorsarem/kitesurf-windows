Kitesurf Browser for Windows 1.2.1
=================================

A native Windows client for the Kitesurf browser engine (github.com/cloudflare/kitesurf).
Kitesurf parses HTML, applies CSS, runs page JavaScript and paints frames on Cloudflare Workers.
This window is the desktop shell for that engine: C#, WPF, no Chromium underneath.

START
-----
Double-click KitesurfBrowser.exe. Nothing else needs to be installed.

Connect the client to an engine:

  * Local development engine
      git clone https://github.com/cloudflare/kitesurf
      cd kitesurf
      pnpm install --frozen-lockfile
      pnpm run dev
    Then enter 127.0.0.1:8787 in the welcome screen and press Connect.

  * Cloudflare-deployed engine
    Follow the engine README to deploy the Worker, put Cloudflare Access in front of it, then paste
    the Worker URL. A Workers Paid plan is required: every page runs in its own Dynamic Worker.

  * Engine behind Cloudflare Access
    Open "Cloudflare Access service token (optional)" under the address field and paste the
    CF-Access-Client-Id and CF-Access-Client-Secret of a service token whose Access policy uses the
    Service Auth action. Create the token under Zero Trust -> Access -> Service Auth.
    The client sends both headers on /json/version and on the CDP WebSocket upgrade. The secret is
    encrypted with Windows DPAPI before it is written to settings.json and is never logged.

No engine at hand? Run with the built-in demo engine:

      KitesurfBrowser.exe --demo

The demo engine is a synthetic page painted on this machine. It exercises the same socket, frame and
input paths a real engine uses. It is not a browser.

COMMAND LINE
------------
  KitesurfBrowser.exe               normal start, reconnects to the last engine
  KitesurfBrowser.exe --demo        start the demo engine and open it
  KitesurfBrowser.exe --selftest    protocol check; writes %TEMP%\kitesurf-selftest.json and exits

KEYBOARD
--------
  Ctrl+T / Ctrl+W     new tab / close tab
  Ctrl+L              focus the address bar
  Ctrl+R, F5          reload
  Ctrl+`              console drawer
  Ctrl+Tab            next tab
  Alt+Left/Right      back / forward
  F12, Ctrl+Shift+I   engine DevTools in your browser

TROUBLESHOOTING
---------------
  "Nothing is listening at ..."   Start the engine, or check the port.
  "Blocked by Cloudflare Access"  Add a service token, or relax the Access policy.
  "did not answer in time"        Host firewall, Cloudflare Access, or a proxy in the way.
  Pages stay black                Check %APPDATA%\KitesurfBrowser\client.log for the frame trace.

  Settings, saved engines and the log live in %APPDATA%\KitesurfBrowser.
  Delete settings.json to start from a clean slate.

LIMITS
------
Kitesurf is an experimental engine implementing a subset of the Chrome DevTools Protocol. Rendering,
form behaviour and media support are whatever the engine produces; this client does not emulate or
patch pages, and it never falls back to WebView2.

LICENSE
-------
Apache 2.0. See LICENSE.txt in this folder, or the repository's LICENSE file.

The application icon is the upstream Kitesurf logo. The Kitesurf name and logo are trademarks of
Cloudflare, Inc., used to identify the engine this client connects to; see NOTICE.txt.
