# Torvia Search API (for PHOENIXA and other apps)

All calls go to the Jellyfin server (e.g. `https://media.phoenicx.org`) with the signed-in user's
normal Jellyfin token, the same way the app already calls Jellyfin:

```
Authorization: MediaBrowser Client="PHOENIXA", Device="…", DeviceId="…", Version="…", Token="<AccessToken>"
```

(`X-Emby-Token: <AccessToken>` also works.) All answers are camelCase JSON. Errors are
`{ "error": "message to show" }` with status 403 (user not allowed), 404, 400 or 502.

## 1. `GET /TorviaSearch/Status` — show the button or not

* **404** → the plugin isn't on this server: hide Search & Add.
* **200** →

```json
{
  "installed": true, "version": "1.0.0.0",
  "allowed": true, "ready": true, "enabled": true, "isAdmin": false,
  "torviaInstalled": true,
  "tags": ["movies", "tv-shows"], "movieTag": "movies", "tvTag": "tv-shows",
  "remainingToday": 9, "dailyLimit": 10,
  "buttonLabel": "Search & Add"
}
```

Show the button only when `enabled` is true. `remainingToday` is `null` when there is no limit.

## 2. `GET /TorviaSearch/Search?query=…`

`query` can be a name ("Kaantha 2025"), a TMDB number, a TMDB link, an IMDb id (`tt…`) or an IMDb link.

```json
{ "results": [ { "type": "movie", "tmdbId": 1234, "title": "…", "year": 2025, "overview": "…",
                 "poster": "https://image.tmdb.org/t/p/w342/….jpg", "backdrop": "…w780…", "rating": 7.8 } ] }
```

`type` is `"movie"` or `"tv"`.

## 3. `GET /TorviaSearch/Title/{type}/{tmdbId}`

```json
{
  "title": { …same fields as a search result… },
  "imdbId": "tt1234567",
  "seasons": [ { "season": 1, "name": "Season 1", "episodes": 8, "year": 2024 } ],
  "jellyfinItemId": "a1b2…"   // null if not in Jellyfin yet
}
```

`seasons` is empty for movies (and leaves out Specials).

## 4. `GET /TorviaSearch/Releases`

* Movie: `?type=movie&tmdbId=1234`
* One episode: `?type=tv&tmdbId=5678&season=1&episode=5`
* Whole season (season packs only): `?type=tv&tmdbId=5678&season=1&wholeSeason=true`

Optional filters (combine freely):

* `quality=1080p` — one or more of `4K,1080p,720p,480p` (comma separated; `all` = no filter)
* `tenBit=true` — only 10-bit
* `hdr=true` — only HDR / Dolby Vision
* `lang=Tam,Hin` — releases with any of these languages
* `hideCam=true` — drop CAM/TS
* `kind=episode|pack` — TV only: just single-episode releases, or just packs
* `sort=best|smallest|largest|seeders` (default `best`)

Suggested default in apps: `quality=1080p`, and if `count` is 0 but `totalCount` > 0, show all qualities.
`available` lists what exists in the unfiltered list (with counts), so only real options are shown:
`{ "qualities":[{"value":"1080p","count":3}], "languages":[{"value":"Tam","count":3}], "tenBit":2, "hdr":1, "cam":1 }`.
An app can also fetch without filters once and filter on the device (the web page does this).

For TV, releases for another season or episode are removed, and every release is labelled
(single episode, episode range, season pack, multi-season, complete series). For a whole season,
`size` is the whole pack. A pack adds every episode in it to TorBox.

Only releases already cached on TorBox are returned, already sorted "Best" first
(CAM/TS last, then 10-bit, then 1080p → 4K → 720p → 480p, then smallest, then most seeders).

```json
{
  "title": { … }, "imdbId": "tt…", "jellyfinItemId": null,
  "season": 1, "episode": 5, "wholeSeason": false,
  "totalCount": 12, "count": 7, "available": { … },
  "releases": [ {
    "hash": "40-char info hash",
    "name": "Release.Name.2025.1080p.WEB-DL…",
    "fileName": "file inside (for packs)",
    "sizeBytes": 2469606195, "size": "2.3 GB",
    "seeders": 40, "speed": "Fast",            // Fast / Good / Slow
    "quality": "1080p",                         // 4K / 1080p / 720p / 480p / ?
    "source": "WEB-DL", "isCam": false,         // isCam true → show ⚠️
    "codec": "H.264", "hdr": "SDR", "bitDepth": 8,
    "audio": "DD+ 5.1 Atmos",
    "languages": ["Tam", "Mal", "Hin"],
    "subtitles": "ESub", "container": "MKV",
    "isSeasonPack": false,
    "kind": "episode",          // TV: episode, range, season, multiseason, complete, pack ("" for movies)
    "label": "S02E05",          // TV: "S02E05", "S02 Episodes 1–8", "Season 2 pack", "Seasons 1–3", "Complete series", "Pack"
    "playsLabel": "",           // TV packs: the episode that will play, e.g. "S02E05" (show "Plays S02E05 · fileName")
    "packSizeBytes": 0, "packSize": "",   // whole torrent size when it is bigger than the file (packs)
    "summary": "WEB-DL · 1080p · H.264 · SDR · 8-bit · DD+ 5.1 Atmos · Tam/Mal/Hin · 2.3 GB"
  } ]
}
```

This call can take 5–15 seconds; show a spinner.

## 5. `POST /TorviaSearch/Add`

Body: `{ "hash": "<hash from the releases list>", "tag": "movies" }` (`tag` optional — the default
for the type is used). The hash must come from a Releases list fetched in the last 2 hours.

**200**:

```json
{
  "ok": true, "message": "Added to TorBox with tag “movies”. It will appear in Jellyfin in a minute or two.",
  "torrentId": 123456, "name": "Kaantha (2025)", "tag": "movies",
  "alreadyInTorBox": false,
  "streamUrl": "https://…torbox…/file.mkv",   // may be null
  "syncStarted": true, "remainingToday": 8
}
```

**400**: `{ "ok": false, "message": "…" }` — show `message` (daily limit reached, list too old, no longer cached…).
This call can take up to ~25 seconds (it waits for TorBox to list the files to make the stream link).

## 6. `GET /TorviaSearch/InLibrary/{type}/{tmdbId}`

Poll every ~10 s after an add (for up to ~3 minutes). **404** until Torvia has synced it, then
**200** `{ "found": true, "itemId": "…" }` → open that item's normal Jellyfin detail screen.

## 7. `GET /TorviaSearch/Adds` — "My adds"

Titles the signed-in user added (newest first). Admins add `?all=true` to get everyone's, and
optionally `&user=<userId>` for one user.

```json
{
  "isAdmin": false, "all": false,
  "users": null,      // admins with all=true: [ { "id": "…", "name": "divya", "count": 4 } ]
  "items": [ {
    "id": "record id", "userId": "…", "userName": "divya", "when": "2026-11-09T08:30:00Z",
    "type": "tv", "tmdbId": 1234, "title": "Paatal Lok", "year": 2025, "poster": "https://…",
    "season": 2, "episode": 5, "label": "S02E05", "quality": "WEB-DL · 1080p", "size": "2.3 GB",
    "tag": "tv-shows", "name": "name in TorBox", "release": "release name",
    "alreadyInTorBox": false, "canRemove": true,
    "jellyfinItemId": "…"   // null until Torvia has synced it
  } ]
}
```

## 8. `DELETE /TorviaSearch/Adds/{id}` — remove

Users can remove their own adds; admins can remove anyone's. It deletes the item from TorBox and
syncs Torvia so it leaves Jellyfin. It is **kept in TorBox** (only removed from the list) if it was
already in TorBox before the add, or another user added the same release too.
Answer: `{ "ok": true, "message": "…", "deletedFromTorBox": true }` (403 if not yours, 404 if gone).
Ask the user to confirm before calling it.

The add list is saved on the server (Jellyfin data folder, `torvia-search/adds.json`), so it survives restarts.
The daily limit counts from this list too.

## Web page

`/TorviaSearch/Web` is the same flow as a web page, for allowed users in any browser.

To open it inside an app's WebView without a second sign-in, pass the app's token in the
address **after #** (it is never sent to the server's logs and is removed from the address bar):

```
https://media.phoenicx.org/TorviaSearch/Web#token=<AccessToken>&name=<UserName>
```

In an Android WebView turn on `settings.domStorageEnabled = true` and `javaScriptEnabled = true`.
Without a token the page uses its own saved sign-in, then the Jellyfin web sign-in in that
browser, and only then shows its sign-in form. A sign-in made on the page is remembered on the device.
