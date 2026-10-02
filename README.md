# Where U At (WhereUAtNative)

Family Tracker — a privacy-first .NET MAUI app for Android and iOS. Firebase email/password authentication, **opt-in** location sharing, family groups, and a lightweight OpenStreetMap map. Location is **off by default** and only uploaded while Share my location is ON **and** you belong to a family.

## Open the project

1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download) with the MAUI workload.
2. Open `WhereUAtNative.csproj` in **Visual Studio 2022/2026** or **JetBrains Rider**.
3. Select an Android or iOS target and run.

```bash
dotnet restore
dotnet build -t:Run -f net10.0-android
```

## App identity

| Setting | Value |
|--------|--------|
| Application title | Where U At |
| Android / iOS package id | `com.familytracker.whereuat` |
| Display version | `0.2.17` (versionCode `20`) |
| Firebase project | `whereuat-firebase` |

Firebase config files already live under:

- `Platforms/Android/google-services.json`
- `Platforms/iOS/GoogleService-Info.plist`

## Testing login

Create (or use) a **Firebase Authentication** email/password user in the `whereuat-firebase` console. The app has login only — no in-app sign-up UI yet.

1. Launch on a device or emulator.
2. Sign in with the Firebase email/password user.
3. You should land straight on the **Map** (full-bleed OSM). Use the mid-right green pin FAB to share location, the **padlock** FAB under it to unlock map follow, and **Settings** for coordinates, family code, and sign-out.

## Location sharing (opt-in)

- Sharing defaults to **Off** (device preference `location_sharing_enabled` = false).
- The map **pin FAB** (mid-right) toggles Share my location — green when on, muted when off. It requests **when-in-use** permission, then reads GPS. **Your self pin only appears while sharing is ON** (local display + optional family upload).
- While map follow is locked on a marker, a **padlock FAB** appears under the pin; tap it to unlock (finger-pan freely). Leaflet +/- zoom controls are hidden — pinch to zoom.
- GPS reliability (v0.2.17): listener restart trusts the platform `IsListeningForeground` flag (not a stale in-memory bool); `IsEnabled=false` is a soft warning so OEM false-negatives still attempt a fix.
- GPS cold starts / session restore: when Share my location was left **On**, cold open re-inits the **foreground location listener** (Medium) with a brief listener wait before one-shots — same path as tapping the pin (avoids the restore race that hung GPS while the map WebView was still loading). Accepts last-known (up to ~30 min), then Medium→Low→Lowest one-shots. A bottom **ToggleHint** shows **“Sharing on — waiting for GPS…”** until a fix (no top status chip). If the WebView was not ready, the pin is queued and re-injected when the bridge is up.
- Self pin defaults to **light purple** with your **Alias Name** as a sleek label **above** the pin (not inside). Change alias and pin colour under **Settings → Map display** (local Preferences).
- **Coordinates**, **family code**, and **Sign out** live under **Settings** (map stays clean). Coords are rounded (~4 decimal places).
- While sharing is on **and** you are in a family, the app writes your live position to Firebase Realtime Database under `families/{code}/locations/{uid}` (foreground only).
- Turning sharing **Off**, leaving the family, or signing out removes your published location. The off/empty hint sits at the **bottom** of the map so it stays out of the way.

### Permissions

| Platform | What we declare |
|----------|-----------------|
| Android | `ACCESS_COARSE_LOCATION`, `ACCESS_FINE_LOCATION` (foreground / when-in-use only — no background location yet) |
| iOS / Mac Catalyst | `NSLocationWhenInUseUsageDescription` only (no Always) |

## Family groups

Privacy-first: **location is only uploaded when that user has Share my location ON.**

### How to create / join (testing)

1. Sign in as user A → **Settings** → **Create family**. Note the **6-character code**.
2. Sign in as user B (second device/emulator or after sign-out) → **Settings** → enter the code → **Join family**.
3. Both users tap the map **pin FAB** to turn sharing On and grant when-in-use permission.
4. Stay on the Map — you should see yourself and the other sharing member(s). Tap a marker to lock/follow (neighbourhood zoom ~16).

### Data model (Realtime Database)

```
users/{uid}/familyId
users/{uid}/displayName
families/{familyId}/createdBy, createdAt, code
families/{familyId}/members/{uid} = { displayName, joinedAt }
families/{familyId}/locations/{uid} = { lat, lon, updatedAt, displayName, sharing: true }
```

`familyId` is the invite code (6 chars). Create writes the family root **with** `members/{uid}` in one PUT. Join writes only `members/{ownUid}` (cannot read the family until after joining). Location/member self-writes stay under the signed-in uid.

## Firebase console steps (required)

This release uses **Firebase Realtime Database** REST with the Auth ID token (no Firestore package — keeps Android Release merges reliable). `google-services.json` already includes:

`https://whereuat-firebase-default-rtdb.asia-southeast1.firebasedatabase.app`

### Enable Realtime Database

1. Open [Firebase Console](https://console.firebase.google.com/) → project **whereuat-firebase**.
2. Build → **Realtime Database** → Create database (region **asia-southeast1** if prompted to match the URL above).
3. Start in **locked mode**, then paste the rules below.

### Security rules (paste in RTDB Rules)

Member-only family reads; create must include `members/{uid}` on first write; join writes only `members/{ownUid}` (no family read required beforehand).

```json
{
  "rules": {
    "users": {
      "$uid": {
        ".read": "auth != null && auth.uid == $uid",
        ".write": "auth != null && auth.uid == $uid"
      }
    },
    "families": {
      "$familyId": {
        ".read": "auth != null && data.child('members').child(auth.uid).exists()",
        ".write": "auth != null && ((!data.exists() && newData.child('members').child(auth.uid).exists()) || data.child('members').child(auth.uid).exists())",
        "members": {
          "$uid": {
            ".write": "auth != null && auth.uid == $uid"
          }
        },
        "locations": {
          "$uid": {
            ".write": "auth != null && auth.uid == $uid"
          }
        }
      }
    }
  }
}
```

The client matches these rules: **create** PUTs `families/{code}` atomically with `members/{uid}`; **join** PUTs only `families/{code}/members/{uid}` (then reads once membership grants access). Firebase **ID tokens** are sent as the RTDB REST `?auth=` query parameter (HTTPS). Do **not** use `Authorization: Bearer` for ID tokens — that header is only for Google OAuth2 access tokens.

> If you prefer Firestore instead: enable Firestore in the console and mirror the same collections (`users`, `families/{id}/members`, `families/{id}/locations`). This app build talks to **Realtime Database**, not Firestore.

## Maps (OpenStreetMap)

The map UI uses a **WebView** with **Leaflet** and free **OpenStreetMap** tiles (`Resources/Raw/map.html`). No Google Maps API key is required.

- Self pin + family markers when locations are available.
- Default / lock zoom is **16** (neighbourhood/street). Subsequent updates pan without resetting to world view; if zoom &lt; 14 while locked, it bumps back to 16.
- Tap a marker to lock/follow; Unlock on the bottom bar releases follow.
- Leaflet JS/CSS load from the unpkg CDN; OSM tiles need network access.


## Crash / error log (on-device)

Fatal and Error events (uncaught exceptions, unobserved tasks, Android unhandled exceptions, and caught startup failures) append to a **local** JSONL file:

`FileSystem.AppDataDirectory/crash.log`

- **Not uploaded** to Firebase or any server.
- Redacts passwords, tokens/JWTs, `?auth=` query values, and precise lat/lon (5+ decimal places).
- Rotates at ~256 KB (keeps the newest half).
- In **Settings → Diagnostics**, use **Share crash log** / **Copy crash log** to send the last ~48 KB to the developer, or **Clear crash log**.

## Privacy defaults (this release)

- Credentials / tokens are never logged; Firebase ID tokens for RTDB REST use the `?auth=` query parameter over HTTPS (Bearer is for OAuth access tokens only). Avoid logging full request URLs.
- Login failures show a generic message (no raw exception text).
- Precise location is never logged.
- On-device crash log only (Settings → Share/Copy); never auto-uploaded.
- Location is not requested until the user turns sharing on.
- No background / Always location.
- Location upload only while sharing is ON and the user is in a family; cleared on share-off / leave / sign-out.
- Family invite codes are validated (length + charset) before join.
- Map WebView loads packaged HTML only; top-level http(s) navigations are blocked.

## Note about repo docs

`Application for access to health information.docx` is unrelated documentation left in the repository; it is not part of the app.

## Next up

- Background tracking only if product explicitly requires it

## Android release APK (Obtainium)

Release builds are signed with a local keystore at `keystore/whereuat-release.jks` (gitignored). Set:

```bash
export WHEREUAT_KEYSTORE_PASS=...
export WHEREUAT_KEY_PASS=...
dotnet publish -f net10.0-android -c Release -r android-arm64 -p:AndroidPackageFormat=apk
```

Package id: `com.familytracker.whereuat`. Install via [Obtainium](https://github.com/ImranR98/Obtainium) from GitHub Releases.
