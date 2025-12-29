# MangHoMagnet
Scans the DCInside PEAK gallery for `steam://joinlobby/...` links and lets you join from an in-game UI. Auto-join for valid links is available (off by default).

## Key Features
- Scans the gallery list page and checks posts for Steam lobby links
- Collects `steam://joinlobby/...` links from post bodies
- Highlights lobby status (valid/full/checking/invalid/unknown)
- Optional auto-join for valid links (off by default)
- In-game list with post metadata + link, open the original post or join manually

## UI
- Toggle key: `F8` by default
- Header controls: Refresh Now / Hide (F8) / Auto Join
- Info row: Last poll / Next refresh countdown
- Columns: Number / Title / Author / Exact time / Views + Steam link
- Buttons: `Open Post` and `Join` (status text)

![Example UI](https://github.com/user-attachments/assets/431ad94a-dbcc-4efa-aa0f-4255365e729d)

## Settings
BepInEx config: `BepInEx/config/com.github.manghomagnet.cfg`

- `General.Enabled`: Enable/disable the mod
- `General.GalleryListUrl`: DCInside gallery list URL to scan
- `Filter.SubjectKeyword`: Optional subject filter (default empty = no filtering)
- `General.PollIntervalSeconds`: Scan interval (seconds)
- `General.MaxPostsPerPoll`: Max posts per poll (minimum 50 to cover the first page)
- `General.MaxLobbyEntries`: Max lobby entries to keep
- `General.ValidateLobbies`: Check lobby validity via Steam API
- `General.ValidationMode`: None/FormatOnly/Steam
- `General.ValidationIntervalSeconds`: Lobby recheck interval (seconds)
- `General.SuppressLobbyPopups`: Auto-dismiss lobby error popups while validating
- `General.ExpectedAppId`: Expected Steam App ID (default: 3527290)
- `Join.AutoJoin`: Auto-join valid links (default OFF)
- `Join.JoinCooldownSeconds`: Auto-join cooldown (seconds)
- `Network.UserAgent`: User-Agent for HTTP requests
- `General.LogFoundLinks`: Log found links
- `UI.ToggleKey`: UI toggle key
- `UI.ShowOnStart`: Show UI on start
- `UI.WindowWidth`: UI width
- `UI.WindowHeight`: UI height

The `Next refresh` timer counts down to 0 and triggers an automatic refresh.

## Build
```sh
dotnet build -c Release
```

Copy `Config.Build.user.props.template` to `Config.Build.user.props` to enable post-build copy into `BepInEx/plugins`.
