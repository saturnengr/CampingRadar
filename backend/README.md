# Camp Radar backend

ASP.NET Core 8 Web API that does the shared work for every Camp Radar device:

- **Radar cache** — fetches NOAA MRMS quality-controlled reflectivity once per scan and serves Web-Mercator tiles (`/radar/tile/{time}/{z}/{x}/{y}.png`). NOAA data, public domain.
- **Lightning** — ingests GOES-19 GLM flash files from AWS Open Data every minute (no credentials), keeps the last hour in memory, answers "strikes within 10/20/30 miles of here".
- **Rain arrival** — the same storm-tracking engine as the page, run on the server (`/status`).
- **Push alerts** — Web Push (VAPID) to phones and tablets that opted in, even with the screen off: rain arriving, lightning within 10 mi, and life-safety NWS warnings — tornado, severe thunderstorm, flash flood / flood, hurricane / tropical storm / storm surge, extreme wind, tsunami, blizzard / ice storm / winter storm, extreme cold, avalanche — which are never snoozed.

GitHub Actions builds and tests it on every push and attaches `campradar-api.zip`, a ready-to-upload IIS publish.

## Endpoints

| Method | Path | What |
| --- | --- | --- |
| GET | `/health` | Radar scan count, lightning flashes in store, worker heartbeat, push status |
| GET | `/tick` | Runs one worker cycle; the host's Scheduled Task calls this every 1–2 min |
| GET | `/radar/times` | Scan times (epoch ms), the latest, and the six loop frames |
| GET | `/radar/tile/{t}/{z}/{x}/{y}.png` | 512-px radar tile for scan `t` |
| GET | `/lightning?lat&lon&minutes=30` | Counts within 10/20/30 mi, nearest strike, trend |
| GET | `/lightning/recent?minLat&minLon&maxLat&maxLon` | Strikes in a box, for the map |
| GET | `/status?lat&lon&hitMi=3&threshDbz=20` | Rain prediction + lightning + NWS alerts, with the same wording as the page |
| POST | `/auth/start` · `/auth/verify` | Email sign-in: send a 6-digit code, exchange it for a device token |
| POST | `/auth/redeem` | Activation code (admin or purchase) → device token |
| GET/PUT | `/me/settings` · DELETE `/me` | Synced settings; delete the account |
| GET | `/admin` | Admin dashboard (devices, Pro grants, health); needs the admin key |
| POST | `/brief` | `{facts, fallback}` → a spoken-English briefing written by a language model (needs `Anthropic:ApiKey`) |
| GET | `/push/vapid-public-key` | Key the browser needs to subscribe |
| POST | `/push/subscribe` | `{subscription, lat, lon, prefs}` → `{id}` |
| POST | `/push/{id}/location` · `/push/{id}/snooze` · `/push/{id}/test` | Update a device |
| DELETE | `/push/{id}` | Stop alerts for a device |
| GET | `/admin/vapid/new` | Generate VAPID keys once (needs header `X-Admin-Key`) |

## Deploy to SmarterASP.NET (W1050 plan)

**1. Create the site**
- Control panel → *Websites* → add a site (or a subdomain such as `radar.yourdomain.com`).
- Set the site's framework to **.NET Core** (the panel offers one ".NET Core" choice; the app's runtimeconfig selects .NET 8).
- A subdomain created in the panel (e.g. `radar.yourdomain.com` → folder `radar`) is served as its own site root, so the API answers at `https://radar.yourdomain.com/health` with no path prefix.
- Keep the shipped `web.config` as is: it is intentionally minimal because this host locks IIS's `requestFiltering` section; adding `<security>` or `<httpErrors>` blocks to it makes every request return a bare 500 with an empty `logs` folder.
- *SSL* → turn on the free Let's Encrypt certificate for that host name. The app page is HTTPS and push notifications require HTTPS, so this is not optional.

**2. Generate the push keys (once, ever)**
- Set an admin key: in `appsettings.Production.json` (step 3) put any long random string under `"AdminKey"`.
- After the first deploy, open `https://radar.yourdomain.com/admin/vapid/new` with the header `X-Admin-Key: <that string>` (for example with the REST client in VS Code, or `curl -H "X-Admin-Key: ..." https://.../admin/vapid/new`).
- Copy `publicKey` and `privateKey` into `appsettings.Production.json` under `Vapid`, set `Subject` to `mailto:` your address, and redeploy or restart the site.
- Keep these keys forever. Changing them silently disconnects every device that subscribed.

**3. Configuration file** — create `appsettings.Production.json` next to `appsettings.json` on the server (it is git-ignored, so secrets never land in the repo):

```json
{
  "AllowedOrigins": "https://saturnengr.github.io",
  "Vapid": { "Subject": "mailto:you@example.com", "PublicKey": "…", "PrivateKey": "…" },
  "AdminKey": "a-long-random-string",
  "Glm": { "Buckets": "noaa-goes19" },
  "Anthropic": { "ApiKey": "sk-ant-…", "Model": "claude-haiku-5-5" }
}
```
Add an `Smtp` block to turn on **email sign-in** (6-digit codes); any mailbox on the hosting account works, for example one created under the control panel's *Emails*:

```json
  "Smtp": { "Host": "mail.yourdomain.com", "Port": 587, "User": "no-reply@yourdomain.com", "Password": "…", "From": "LeadTime <no-reply@yourdomain.com>" }
```
Without it, people can still sign in with an activation code from the admin page (`/admin`), which is also where Pro is granted.

`Anthropic:ApiKey` is optional: with it, the app's Briefing is written by a language model; without it the device composes the text itself. A key comes from console.anthropic.com; a briefing costs well under a cent.
```
```
Add `noaa-goes18` to `Glm:Buckets` (comma-separated) for better lightning coverage west of the Rockies; it doubles the lightning download, which is still small.

**4. Upload the build**
- Download `campradar-api.zip` from the latest green run on the repository's *Actions* tab (or run `dotnet publish -c Release -r win-x64 --self-contained false` locally).
- Unzip into the site's `wwwroot` folder with the control panel's File Manager, FTP, or Web Deploy from Visual Studio. The zip already contains `web.config`.
- The app writes its radar cache and SQLite database to `wwwroot\App_Data`; the folder is created on first run and `web.config` blocks it from being served.

**5. Heartbeat**
- Control panel → *Scheduled Tasks* → add a task that requests `https://radar.yourdomain.com/tick` every **1 minute** (2 if the panel's minimum is higher).
- This keeps alerts flowing even when IIS has idled the app pool; the app also runs its own loop while awake. Both call the same code and never overlap.

**6. Check it**
- `https://radar.yourdomain.com/health` should show radar scans > 0 within a minute and lightning `lastIngest` within a few minutes.
- In the Camp Radar app: Settings → *Backend address* → paste the URL → *Save address*. The diagnostics line should read "Connected".
- Settings → *Turn on alerts for this device* → allow notifications → *Send a test alert*.

## Running locally

```
cd backend
dotnet run --project src/CampRadar.Api
# http://localhost:5000/health
dotnet test
```
Without VAPID keys the API runs with push disabled (everything else works).

## Notes

- NOAA asks clients to send a descriptive `User-Agent`; set `UserAgent` in configuration to include a contact address.
- Radar tiles for past scans are cached as immutable; the newest scan is cached for 60 s.
- Devices whose push endpoint is gone (uninstalled, permission revoked) are deleted automatically.
- Everything is US-only (NOAA coverage). The API refuses coordinates outside 15–72°N, 170–50°W.
