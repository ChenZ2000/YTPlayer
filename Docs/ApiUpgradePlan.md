## API Upgrade Implementation Plan

### 1. Request Fingerprint & Crypto Pipeline

| Task | Files | Notes |
| --- | --- | --- |
|1.1|`Core/NeteaseApiClient.cs`, `Utils/EncryptionHelper.cs`|Refactor `PostWeApiAsync` / `PostEApiAsync` to inject UA, `X-Real-IP`, `X-Forwarded-For`, and random `_ntes_nuid`, `WNMCID`, `NMTID`, etc. Mirror `request.js` behaviour from `@neteasecloudmusicapienhanced/api`.|
|1.2|`Core/Auth/AuthContext.cs`|Store device metadata pulled from `account.json` (UA, osver, deviceId, channel). Provide helper method returning a normalized cookie header map for both WeAPI and EAPI.|
|1.3|`Core/NeteaseApiClient.cs`|Add helpers `BuildWeapiHeaders`, `ApplyCookieFingerprint`, `ChooseRandomChineseIp`, `GenerateRequestId`. Reuse them in all HTTP clients; unify `HttpRequestMessage` creation so retries reuse the same fingerprint per attempt.|
|1.4|`Utils/EncryptionHelper.cs`|Extend EAPI pipeline to support raw byte encryption/decryption parity with JS version (md5 -> AES -> RSA). Ensure gzip/deflate fallback matches `request.js`.|

### 2. Playback Reporting & Scrobble Enhancements

| Task | Files | Notes |
| --- | --- | --- |
|2.1|`Core/Playback/PlaybackReportingService.cs`|Rework queue to emit start/play/playend batches. Introduce `PlaybackReportBatch` that packages logs for `/weapi/feedback/weblog` and optionally `/eapi/feedback/weblog`. Only send complete log when playback duration ≥ min threshold (50% or 240s).|
|2.2|`Core/NeteaseApiClient.cs`|Add `SendScrobbleAsync` hitting `/api/song/scrobble` (WeAPI) + `/eapi/song/scrobble` (new). Both called from reporting service; include all metadata (source/sourceId/time/type/wifi/download).|
|2.3|`Core/NeteaseApiClient.cs`|Expose `FetchPlayRecordAsync(type)` to call `/weapi/v1/play/record` right after successful log for verification, returning the delta for telemetry dashboards.|
|2.4|`Core/Playback/PlaybackReportingService.cs`|Persist minimal retry queue (JSON file) so failed logs are retried after restart. Integrate with new API client methods.|

### 3. Playback URL & Account Data Paths

| Task | Files | Notes |
| --- | --- | --- |
|3.1|`Core/NeteaseApiClient.cs`|For `/song/enhance/player/url` add fallback to `/eapi/song/enhance/player/url`. Respect quality level selection (`level`, `encodeType`, `immerseType`). If WeAPI returns 404/509 -> auto retry with EAPI + random IP.|
|3.2|`Core/NeteaseApiClient.cs`, `Core/Streaming` components|Update streaming factory to pick EAPI fallback URL when WeAPI fails, and ensure caching keys include quality level.|
|3.3|`Core/NeteaseApiClient.cs`|Switch account refresh endpoints to enhanced forms: `/api/nuser/account/get` and `/api/user/level` using new fingerprint headers.|
|3.4|`Core/Auth/AuthContext.cs`|When updating login profile, ensure new fingerprint fields (deviceId, sDeviceId, musicA, antiCheat token) are refreshed and persisted.|

### 4. Testing Hooks

| Task | Files | Notes |
| --- | --- | --- |
|4.1|`Scripts/ApiRegressionTests.ps1` (new)|PowerShell script invoking new API client methods (playback log, scrobble, play record) with `account.json` + song `31877512`, logging JSON responses for manual verification.|
|4.2|`Docs/ApiUpgradePlan.md` (this file)|Update with execution notes once implementation completes.|
|4.3|`README.md`|Add section describing enhanced API strategy (WeAPI/EAPI, scrobble, fingerprint) and regression script usage.|

### 5. Validation & Deliverables

1. Run regression script twice to confirm scrobble + record increments.
2. `MSBuild.exe YTPlayer.sln /t:Rebuild /p:Configuration=Debug`.
3. Provide summary in final response detailing:
   - Fingerprint injection
   - Playback logging changes
   - Song URL fallback
   - Test protocol results
