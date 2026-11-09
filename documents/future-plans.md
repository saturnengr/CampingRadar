# LeadTime — Future Plans

Nov 8, 2026 · @Jeff

## How to read this

Notes from a planning conversation on 8 Nov 2026, kept so the ideas are not lost. Nothing here is committed; the shipping roadmap stays in the product plan. The hardware Sentinel (tablet-in-a-case bundle) is assumed to be the next product, and everything below comes after it.

LeadTime is three assets wearing one app, and each one spins off products on its own:

- **The nowcast engine** — storm tracking, arrival times, the virga/edge rules, and the verification ledger that scores them against airport gauges.
- **The Sentinel display** — an always-on screen that watches one spot, with the ten-tone sound set and push alerts.
- **The Family / group layer** — shared positions, messages, places, and alerts routed to whoever is at each spot.

## Future products after the hardware Sentinel

### Closest to what exists (12–18 months)

- **LeadTime for Sites** — construction, roofing, crane and concrete crews, outdoor events. The pitch is “you have 40 minutes to get the pour covered” plus a timestamped log afterwards. OSHA lightning rules, weather-delay contract claims and insurance all want evidence of when a warning was issued; the verification ledger becomes a product feature.
- **Lightning horn unit** — pools, golf courses, youth sports fields, marinas, beaches. Today that market pays $1,500–4,000 for Thor Guard or Earth Networks horn systems. A Sentinel box with a weatherproof horn and strobe, driven by GLM and the 30-mile strike counter, undercuts them. Schools and parks departments buy on purchase order, which suits a hardware margin.
- **Fleet / dispatch view** — one account watching twenty leases, job sites or a scout camp’s troop locations on a single map, alerts routed to whoever is at each one. A small backend change (Family with more spots and roles) sold to a different buyer with a budget.

### The engine as a product

- **API / white label** — once the ledger has months of scored cases, “arrival time verified against gauges, X% within 10 minutes” is something a drone-services company, irrigation controller, smart-home hub or race-timing company would pay per call for. Only worth selling once the verification numbers exist to back it.
- **Ag edition** — spraying windows (wind, rain-free hours after application), hay-cutting dry windows, frost nights. The differentiator is a radar nowcast tied to “will my application wash off.”

### Hardware follow-ons

- **Ruggedized cellular Sentinel** — the LTE tablet in an IP65 case with solar panel and mast, for deer camps, remote cabins, ranch gates. The trailer build is where the power and antenna lessons come from.
- **Weather tag** — a \~$40 e-ink or LED puck that pairs to the main display and shows green / yellow / red and the ETA, for the kids’ tent or the dock. Cheap, high attach rate per family.
- **Vehicle integration** — Android Auto card for RVers in motion; arrival math changes when the observer is moving.

### Adjacent, same customer

- **LeadTime Trip** — a planning layer, not a nowcast: “which of the next 10 days is driest at this campsite,” from the same NWS / Open-Meteo feeds, sold as a Pro upsell.
- **Campground operator edition** — the park office runs Sentinel on a big screen and the app pushes to every guest who scanned a QR code at check-in. The operator gets a liability tool; LeadTime gets a distribution channel that recruits users at every campground that signs up.

## Complementary hardware and software by activity

### Camping

- *Hardware:* a small weather station on the trailer or picnic table — Bluetooth/Wi-Fi temperature, humidity, wind, pressure, tipping-bucket rain (\~$60 BOM). Feeds the banner with on-site readings instead of an airport 20 miles away, gives the fizzle / dry-air logic the campsite dew point, and makes the pressure trend local — the best “something is coming” signal when radar is quiet.
- *Software:* pack list driven by the forecast (the “What to bring” pill extended to packing day); a quiet-hours sound profile; a shared camp board for the group (who is where, who is fetching water) with the weather context attached.

### RV

- *Hardware:* the ruggedized cellular Sentinel in a roof or window mount on coach 12 V. An **awning wind sensor** is the one RVers pay for immediately: awnings are destroyed by gusts, and a tilt/anemometer that says “retract the awning” before the gust front is a concrete, insurable save.
- *Software:* route-aware forecast (“the weather along I-40 for the next six hours at your speed”); crosswind warnings for high-profile vehicles; overnight-low alerts for water lines; campground arrival forecast. Android Auto is the natural screen.

### Hunting

- *Hardware:* a cellular or LoRa blind/stand sensor — temperature, wind direction at the stand, pressure — because wind at the stand is what matters for scent. LoRa gateway in the truck or camp, \~$30 nodes at each stand.
- *Software:* scent-cone overlay from the local wind; legal shooting light by position; pressure-trend feeding-activity notes; a silent / vibrate profile so the phone never chimes in the stand; stand-to-stand messaging over the same LoRa where there is no cell service.

### Fishing & boating

- *Hardware:* a boat unit — IP67 Sentinel display with GPS, tied to NMEA 2000 so the chartplotter shows the rain ETA. A lightning horn on the dock for marinas.
- *Software:* storms relative to the ramp (“you are 22 min from the ramp; rain in 35”); lean into the tide and water-temperature data already in the app; wind-against-current chop warnings; river-rise alerts from the USGS gauges for kayakers; a float plan shared with the Family group that auto-pings “we’re late.”

### Hiking / running / cycling

- *Hardware:* mostly none — this crowd carries a watch. A **Wear OS complication** and a **Garmin Connect IQ data field** showing rain ETA and lightning distance; Garmin’s SDK is free and the arrival number on a bike computer is a real differentiator.
- *Software:* trailhead- and summit-aware alerts (exposure above treeline when lightning is 30 mi out, not 10); route-ahead forecast on foot; turnaround-time calculation; offline map caching for the no-signal stretch with the last nowcast baked in.

### Beach

- *Hardware:* a lifeguard / concession-stand Sentinel with horn and strobe — beach operators are exactly the Thor Guard customer. A UV sensor is a cheap add.
- *Software:* rip current, UV and tide are already there; add a sunburn timer by skin type and SPF, a “pack up now” countdown that accounts for walking off the sand, and a kid-finder use of the Family layer (geofence around the umbrella).

### Skiing

- *Hardware:* a cold-rated battery case for a cabin Sentinel; a snow-depth stake with an ultrasonic sensor at the base of the driveway so the app knows what fell overnight before the resort reports.
- *Software:* overnight snowfall and wind-hold predictions by resort; whiteout / visibility alerts (already in the hazard sound set); avalanche bulletins from avalanche.org; pass road-closure feeds; layering advice extended from the Bring pill.

### Sentinel (fixed display)

- *Hardware:* this is the hardware product itself — the tablet-in-a-case bundle, then the cellular/solar outdoor version, the horn/strobe relay, and a dry-contact output so it can trigger existing systems (pool alarm, gate, PA). The e-ink weather tag as a repeater for other rooms.
- *Software:* kiosk mode with remote configuration from the admin page; multi-site fleet view; event logging for the Sites edition; a wall-display API so a campground or school can put the status bar on their own screens.

## Cross-cutting builds worth doing once

- **Local weather station** — feeds every activity and makes the tracker measurably better: a gauge at the customer’s site becomes a ledger row, which is what the verification effort is for.
- **LoRa mesh (Meshtastic-compatible)** — covers hunting, hiking and remote camping at once: off-grid messaging, plus a path for the nowcast to reach a phone with no signal through one device that has it.
- **Watch surfaces (Wear OS, Garmin Connect IQ)** — cover trail, skiing, boating and beach with no new hardware.
- **Horn / strobe relay** — the same board serves beach, marina, pool and job site.
- **Native Android shell** — needed anyway for screen-off GPS; it is also the prerequisite for Android Auto, Wear OS and Play Store distribution.

## Platforms, sign-in and costs

### Native Android shell and Android Auto

- The car screen never runs our UI. A native app ships a `CarAppService` and the head unit renders Google’s fixed templates from data the service supplies. Google has an official **Weather** category (`androidx.car.app.category.WEATHER`), which unlocks `MapWithContentTemplate`: the app draws the map surface itself, with a template list or pane beside it — radar with the dot on the left, “Rain 22 mi SW · arrives ≈ 1:40” and the next hours on the right, chime through the car speakers. Declare `androidx.car.app.MAP_TEMPLATES`, not the navigation permission; declaring both is rejected in review.
- Work: a Kotlin shell hosting the PWA in a WebView plus a foreground GPS service (about two weeks), then the car service on top (one to two more). The nowcast core already exports as plain JS, so the car service can run it in a hidden WebView rather than porting the math. Moving-observer arrival math is a projected position along the heading, not a rewrite.
- Testing with Google’s Desktop Head Unit emulator in Android Studio; a real head unit (\~$100–150 used) is optional.
- **CarPlay** is effectively closed: Apple grants it by entitlement per category (audio, messaging, navigation, EV charging, parking, fueling, food ordering, driving task) and there is no weather category. iOS gets the PWA plus push alerts; revisit only if Apple’s categories change.

### What has to be bought

- Android: nothing. Android Studio, the SDK, emulator, Kotlin, Gradle, the Car App Library and the Desktop Head Unit are all free. No C++ or Visual Studio is involved (the logic stays JavaScript in a WebView; the backend builds with the free `dotnet` SDK / Visual Studio Community). The only money is the one-time **$25 Google Play Console** registration.
- iOS: a Mac plus the **$99/year** Apple Developer Program, and CarPlay stays closed regardless.

### Sign-in options

- Email + 6-digit code is live and stays as the fallback. A first-run welcome page (device name, activity, optional email) shipped 8 Nov 2026 (2026.11.10i).
- **Sign in with Google** is free (Google Identity Services / OAuth client ID in a Cloud project; no billing account). The backend would verify the ID token and map the email onto the existing account record — about a day. An app requesting the email scope starts in testing mode (100 named users) until a short, free verification that needs a homepage and privacy-policy URL on getleadtime.app. Identity Platform (free to 50k MAU, then \~$0.0055/MAU) is a different product we would not need.
- **Sign in with Apple** needs the $99 developer program, domain verification and a services ID; its “hide my email” relay complicates Family invites by email. Apple requires it in any native iOS app that offers Google sign-in.

### Website

- getleadtime.app is purchased; repo saturnengr/LeadTime (private), to be hosted on SmarterASP.NET. Pages needed: landing, pricing (the app already links to /pricing/), privacy policy, manuals, later the store. Google OAuth verification and the Play Store both want the landing and privacy pages.

## Suggested priority

Ranked by effort against likely revenue:

1. **LeadTime for Sites** — reuses everything built; sells to organizations that write checks.
2. **Lightning horn unit** — small hardware, an existing market paying far more.
3. **Campground operator edition** — a liability tool for the operator and a distribution channel for us.
4. **Weather station + horn relay** — two small boards that touch every activity and both lines of business; the station improves core accuracy.
5. **Native Android shell → Android Auto → Wear OS / Garmin** — the shell is needed anyway for screen-off GPS.
6. **API / white label** — high value, but only after the ledger has months of scored cases.

## Locking down the Sentinel tablet (kiosk mode)

Goal: the tablet boots straight into LeadTime, stays there, lets the owner change Wi-Fi, and cannot be turned back into an ordinary Android tablet by the customer.

- **Android’s built-in answer is Device Owner mode.** A tiny Device Policy Controller app (ours) is set as device owner during first setup — either by scanning a QR code at the “Hi there” screen of a fresh tablet (zero-touch style provisioning, no Google account needed) or over ADB (`dpm set-device-owner`) before the tablet ships. Device owner can only be set on a tablet with no accounts added, so it is done at the factory/bench step, and it survives reboots. The only way out is a factory reset, which device owner can **block** (`DISALLOW_FACTORY_RESET`); recovery-mode reset can additionally be stopped with Factory Reset Protection tied to our provisioning.
- **Lock task mode** (the real kiosk): the DPC whitelists the LeadTime app (the installed PWA shell or the native shell) and starts it in lock task. Home, Back, Recents and the status-bar shade are disabled or limited, notifications are suppressed, and the app relaunches on boot (`PERSISTENT_PREFERRED_ACTIVITY` as the home app). Lock task has feature flags so we can allow the system info bar and global actions (power menu) while keeping everything else off.
- **Wi-Fi without the rest of Settings:** two ways. Preferred: our own Wi-Fi screen inside the app (scan, pick a network, enter the password) using the `WifiNetworkSuggestion` / `WifiNetworkSpecifier` APIs, which a device-owner app can use without showing Settings at all. Simpler: lock task whitelists only the Wi-Fi settings activity (`Settings$WifiSettingsActivity`) and the app launches it from a button; user restrictions (`DISALLOW_CONFIG_*`) block everything else in Settings, and `DISALLOW_DEBUGGING_FEATURES` closes ADB.
- **Still manageable by us:** device owner can install and update the app silently (or we simply keep the PWA, which self-updates), set the screen to stay on while charged, hold brightness, disable the camera, and push a new config from the admin page. An “owner PIN” screen inside the app can expose the Wi-Fi screen, volume and the bottom-gap setting to the customer while keeping the rest closed.
- **Options that avoid writing a DPC:** Android Enterprise with a free-tier MDM (Google’s Android Management API is free; Scalefusion, Esper and Hexnode charge per device per month) gives the same lock task + Wi-Fi-only policy from a web console, and zero-touch enrollment from a reseller. Worth it if we ship hundreds; for the first batches our own 300-line DPC is cheaper and we own it.
- **Hardware notes:** choose a tablet with Android 10+ and Google Play services (Device Owner and Play-less AOSP tablets behave differently); the Oukitel RT3 qualifies. Keep a bench procedure: fresh reset → skip setup → provision device owner → install app → lock task → test Wi-Fi change → box it.
