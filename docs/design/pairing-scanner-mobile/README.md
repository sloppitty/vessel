# Pairing scanner on a phone

Captured October 29, 2026 with headless Chromium at 390 × 844 (2× pixels), mobile
and touch emulation, from the production build with the API mocked to a
signed-out sign-in screen.

| File | State |
| --- | --- |
| `1-scanner-start.png` | Scan the code, before the camera starts. |
| `2-camera-denied-by-browser.png` | The browser refused the camera (`NotAllowedError`, simulated: headless Chromium cannot show a real refusal). "Try the camera again" now has its full width and the other actions wrap under it. |
| `3-camera-blocked-by-site.png` | The page's `Permissions-Policy` is `camera=()`, the header the homelab Traefik proxy sends. The scanner explains it and offers no retry. |
| `4-camera-scanning.png` | Camera live, using Chromium's fake camera. |
