# Where U At (WhereUAtNative)

Family Tracker — a privacy-first .NET MAUI app for Android and iOS. This foundation release provides Firebase email/password authentication and a signed-in home shell. Location sharing is **off by default** and not implemented yet.

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
| Firebase project | `whereuat-firebase` |

Firebase config files already live under:

- `Platforms/Android/google-services.json`
- `Platforms/iOS/GoogleService-Info.plist`

## Testing login

Create (or use) a **Firebase Authentication** email/password user in the `whereuat-firebase` console. The app has login only in this release — no in-app sign-up UI yet.

## Privacy defaults (this release)

- Credentials are never logged.
- Login failures show a generic message.
- No location permissions are requested yet.
- Location sharing UI is a placeholder (“coming next / off by default”).

## Note about repo docs

`Application for access to health information.docx` is unrelated documentation left in the repository; it is not part of the app.

## Next up

Maps and opt-in location sharing will land in a follow-up PR.
