## API Upgrade Targets

These are the endpoints that will be refactored or newly integrated while aligning with `@neteaseapireborn/api`.

1. `POST /eapi/feedback/weblog`
   - Replaces current WeAPI variant for playback telemetry.
   - Requires enriched payload (device/app metadata) and EAPI encryption.

2. `POST /eapi/song/scrobble`
   - Additional scrobble endpoint to ensure weekly/total listen counts are credited.

3. `POST /eapi/v1/play/record`
   - Reborn-provided variant for fetching recent (type=0) and weekly (type=1) listening stats after reports.

4. `POST /weapi/song/enhance/player/url` and `POST /eapi/song/enhance/player/url`
   - Dual-path playback URL retrieval for better availability; EAPI variant added as fallback/parallel option.

5. `POST /eapi/login/token/refresh`
   - Align token refresh with the Reborn package behavior to share headers/device info.

6. `POST /eapi/user/level` and `/eapi/nuser/account/get`
   - Account data refresh endpoints to ensure accurate profile/level after scrobbles.

7. `POST /eapi/feedback/weblog` (batch operations)
   - Explicitly listed again to cover combined start/play/end batches after redesign.

All subsequent validation, simulation, and implementation tasks will reference this list.
