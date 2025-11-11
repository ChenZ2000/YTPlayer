# Comments Dialog Accessibility Plan

## Requirement Grouping
- **Navigation Consistency**: Home/End jumps, keyboard focus changes, and context-menu selection updates must always yield synchronized visual focus and spoken feedback, regardless of tree length.
- **Reply Expansion Flow**: Lazy-loaded replies, nested load-more branches, and collapse actions must behave identically whether a node owns zero or dozens of children, without triggering phantom announcements.
- **Speech Priority & Cancellation**: Screen-reader output must always reflect the most recent command; queued speech or stale announcements must be interrupted immediately by newer requests.
- **Build & Workflow Hygiene**: Existing build scripts, solution files, and git workflow must incorporate any new helper code or resources introduced by this fix.

## Implementation Sequence
1. **Upgrade TTS Helper** – add interruptible speech and explicit cancellation hooks so every consumer can preempt stale announcements without blocking the UI thread.
2. **Wire Comment Announcer** – introduce a dedicated announcer around `CommentsDialog` that reacts to selection changes, expand/collapse events, reply loads, and context actions to read the actual focused node.
3. **Harden Reply Loader** – guard asynchronous reply fetches with single-flight state, keep placeholder semantics intact, and avoid duplicate loads that desync focus vs. spoken content.
4. **Regression + Build** – run the debug build, verify keyboard interactions across long comment lists, and update any affected documentation or shortcut descriptions.

## API Verification
- The fix relies on existing `NeteaseApiClient.GetCommentsAsync` and `GetCommentFloorAsync` calls whose signatures already match the upstream `chaunsin/netease-cloud-music` and `binaryify/NeteaseCloudMusicApi` projects; no protocol or payload adjustments are required.

## Validation Plan
- Manual keyboard walkthroughs (Home/End, PageUp/PageDown, expand/collapse, reply load-more) with a long comment list to ensure announcements always match focus.
- Comment CRUD regression (post, reply, delete) to confirm selection restoration still works with the new announcer.
- Debug build via `Build-Debug.ps1` to surface warnings/errors introduced by the accessibility changes.
