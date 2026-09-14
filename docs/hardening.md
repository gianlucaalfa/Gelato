# Hardening preview channel

This branch targets Jellyfin 12.0 and HTTP(S) sources returned by AIOStreams. It includes upstream PRs #212 and #213 from the stable fork, plus the native-version work from PR #214 with additional isolation and regression coverage. Torrent/debrid functionality is not required. Keep P2P disabled for an HTTP-only installation.

## Install and update

In Dashboard → Plugins → Repositories, add:

```
https://raw.githubusercontent.com/gianlucaalfa/Gelato/refs/heads/hardening-feed/repository.json
```

The feed is created only after the first successful publication. It uses the existing Gelato plugin ID, so it updates the installed plugin; it does not install a second copy. Disable the stable Gelato repository while testing this channel to make version selection unambiguous. Install the available Gelato update and restart Jellyfin.

Every successful push to `fix/hardening-native-versions` runs regression tests, builds the declared plugin dependencies, and boots that package in both official Jellyfin 12 and LinuxServer Jellyfin 12 containers. The container tests check plugin loading, administrator/user/anonymous access and SMTP settings across restart. Only then does the workflow publish a GitHub prerelease and update `hardening-feed/repository.json`. Failed builds leave the previous feed untouched. No stable release or `gh-pages/repository.json` is updated by preview publication.

Preview versions are numeric `0.26.17.(1000 + workflow run number)`, as required by Jellyfin's version comparison. Release tags are `hardening-v<version>`. Each ZIP has MD5 metadata for Jellyfin and a separate SHA-256 checksum. Published artifacts are not replaced. A superseded build is skipped before publication.

## Security changes

- Palco cache, SMTP and catalog administration require Jellyfin's `RequiresElevation` policy. Regular user tokens can no longer read global secrets or start global imports. Existing administrator integrations retain the same routes and JSON format.
- Anonymous registration requires an explicitly stored `true` flag. Missing, false or malformed flags disable registration. Requests have body/field limits, a finite lifetime, a bounded queue and process-local per-client/global rate limits. Deployments behind a proxy should configure Jellyfin's trusted proxies so client addressing is accurate; the global limit still applies.
- Palco's optional integration can be disabled in plugin settings. Its database remains at `DataPath/Palco/cache.db`. Migration preserves legacy rows and uses `(namespace, key)` as the primary key. Registration/index updates are atomic and bulk reads use a single query.
- SMTP configuration is protected at rest with a persistent key ring at `DataPath/Gelato/keys`. Include both the key directory and database in backups. The key directory is owner-only on Unix. This protects database-only disclosure; it does not protect against someone who can read both the database and keys. Administrator API reads still return the original settings for UI compatibility.
- Stream synchronization is user-scoped and mutations are serialized per primary item. Selected stream versions must belong to the requesting user. Metadata requests use that user's addon configuration.
- HTTP downloads forward only `Range` and `If-Range`, preserve 206/416 responses and stream data without whole-file buffering. Jellyfin credentials and cookies are not forwarded. Responses remain attachments, and the native Jellyfin download authorization policy still applies.
- Non-HTTP media URLs are rejected before reaching Jellyfin/FFmpeg. Addon JSON, subtitles and proxied images have size/time limits. Image responses allow only raster image types and disable MIME sniffing. HTTP client logs for addon/proxy requests do not expose credential-bearing URLs.
- Deletion is scoped to the requested library and primary item. Virtual stream cleanup never deletes underlying media files. Purging and synchronization share the same mutation lock.
- Optional torrent requests now require a protected capability and a local caller; concurrent engines and cleanup are bounded. The HTTP channel does not enable P2P.

Configured addons remain trusted sources of outbound HTTP URLs. Private/LAN endpoints and CDN redirects remain supported for local AIOStreams installations. This is not a network sandbox or a complete SSRF defense against a malicious addon. Jellyfin/FFmpeg and other providers also perform outbound requests; use network egress policy when untrusted addons are involved.

## Performance and organization

- Removed synchronous waits for stream discovery from DTO/media-source generation. Preparation runs asynchronously with cancellation; concurrent requests from the same user coalesce. Subtitle prewarming remains conditional on library settings and runs alongside stream lookup.
- Plugin caches have size/expiry limits and no longer clear Jellyfin's shared cache. Metadata keys include media type; provider URL keys are case-sensitive. Subtitle entries are keyed by URL identity as well as addon ID.
- Manual imports use a bounded hosted queue instead of unbounded `Task.Run` calls. Identical imports coalesce. Imports preserve source order, honor the global fallback when `MaxItems` is zero, and update collection membership by differences. Partial item failures do not remove existing collection members. A full library scan is no longer started after every import.
- Stream persistence lives in `GelatoManager.Streams.cs`; Stremio models are separate from transport. Locks, caches, preparation, import scheduling, registration limits and torrent lifecycle have dedicated types.
- Native-version DTOs match input items by ID after Jellyfin visibility filtering. Season child counts include linked streams only for the correction query; played/total counts do not subtract linked versions twice.
- Workflow actions are pinned. Stable release creation verifies tests first; manual stable publication resolves and verifies the exact existing release tag. Stable feed updates preserve prior history without a release-list limit that previews could exhaust.

The new Jellyfin search interfaces were evaluated against Jellyfin 12 source. Their results refer to persisted item IDs; Gelato currently returns and hydrates external metadata that is not yet in the library. Replacing that adapter requires a separate integration design, so this branch retains the existing search behavior.

## Upstream PR #214 review

Reviewed through `9d2152e8e67cfc8943edd1a32bd8d97bf4bbc468`. The latest watch-state changes are included: playback start does not clear resume, replaying watched content retains progress, and favourite/rating requests update only the requested fields. The request filter additionally rejects unsuccessful results and verifies the target user can access both the row and its primary.

Native links are read from persistence to handle stale cached instances and manually merged local versions. Remote rows receive refresh timestamps, retained legacy rows receive their primary ID, subtitle lookups include native versions, and mixed-mode opt-out removes linked remote sources. Stream discovery retains the asynchronous preparation introduced by this branch. Deletion keeps same-folder legacy lookup and stable per-primary stream IDs instead of adopting cross-library rows or deriving IDs from temporary playback URLs. DTO mapping remains by ID for every patch; season counts retain the separate linked-version correction.

## Validation and rollback

Automated coverage includes concurrency/cancellation, cache isolation, legacy SQLite migration, SMTP protection and restart, registration limits, HTTP ranges, source URL schemes, metadata identity, import limits, native episode counts and filtered DTO mapping. The real-container test exercises dependency injection and HTTP authorization on the packaged plugin.

Before treating this preview as your production baseline, check your own AIOStreams configuration: movie and episode playback, seeking, resume after restart, changing versions, subtitles, favorite/watched state, and two users with different addon configurations. This CI environment does not contain your addons, media library or client devices, and the tests do not establish end-to-end playback compatibility with them.

Take a Jellyfin configuration/database backup before first installation. For rollback, disable the preview repository. Run Gelato's **Purge streams** task on this branch to unlink temporary native stream versions while preserving primary items. Stop Jellyfin and restore the previous plugin version (automatic updates do not downgrade a higher numeric version). The Palco database migration and protected SMTP values are not understood by the old Palco implementation: restore the pre-upgrade Palco database or re-enter its settings when downgrading. Restore the full backup if a complete pre-upgrade state is required.

## References used

- [Upstream PR #214](https://github.com/lostb1t/Gelato/pull/214)
- [Jellyfin 12 release notes](https://jellyfin.org/posts/jellyfin-release-12.0/)
- [Jellyfin 12 source](https://github.com/jellyfin/jellyfin/tree/v12.0): `ItemCountService`, repository access filtering, `DtoService`, `ILibraryManager`, authorization policies and startup/user controllers.
- [Jellyfin Web 12 source](https://github.com/jellyfin/jellyfin-web/tree/v12.0): version selection in the item details controller.
- [Official container documentation](https://jellyfin.org/docs/general/installation/container/)


## HTTP-focused fork review (September 2026)

The following changes adapt nistei's Jellyfin 12 branches rather than importing unrelated history:

- `fix/redact-keys-in-logs` (`9831096`): all plugin logger categories now redact complete remote URLs, including path credentials, query strings, user info, fragments, JSON-escaped and percent-encoded addresses. Logging providers receive neither the original structured values nor raw exception objects. Error types remain available; detailed exception messages/stacks are intentionally omitted from these plugin events. Direct logger-factory callers use the same protection. This does not rewrite historical logs or control Jellyfin/FFmpeg, reverse proxy, addon or other plugin logs.
- `fix/library-settings-hardening` (`d747681`): chapter-image extraction and trickplay generation skip only Gelato remote playback items. Chapter cleanup, ordinary metadata images and local media with Stremio IDs retain their behavior. Stream version names are locked against embedded-title replacement by Jellyfin's probe. Verified against Jellyfin 12's `IChapterManager`, `ITrickplayManager` and `FFProbeVideoInfo`.
- `fix/search-addon-failures` (`22e1e83`): independent movie/series search failures no longer discard a successful catalog. Empty successful results are legitimate; total catalog failure remains an error. Request cancellation is propagated to manifest and catalog requests. Searches remain concurrent.
- Image placeholders use non-truncating creation; bounded per-path locks serialize sidecar writers, and same-directory atomic replacement prevents partial URL reads. Existing downloaded images survive concurrent refreshes. Actual filesystem failures are surfaced rather than silently ignored.

The LinuxServer smoke uses image `12.0ubu2604-ls48` pinned by digest, PUID/PGID 1000 and persistent `/config`. It installs the package under `/config/data/plugins`, matching LinuxServer's `JELLYFIN_DATA_DIR`. It checks plugin loading, authorization and encrypted SMTP round-trips across a container restart. It does not simulate Intel GPU acceleration, Docker Mods, the production reverse proxy or private upstream media.

### Optional proposals not enabled

| Proposal | Decision for this HTTP/Jellyfin 12 branch |
| --- | --- |
| ptohy PR #206 | Do not import the health-probe fallback as-is. It downloads sample data from multiple sources before playback, uses item/source caches without explicit user scoping, and its byte threshold can reject a valid short HLS response. It does not recover arbitrary interruptions after playback starts. A future fallback needs real failures, expiring-URL awareness and per-user isolation. |
| Bobbls PR #155 | Retain Jellyfin's DTO service. A handcrafted search DTO drops fields whose necessity varies by client; the existing code specifically records Infuse compatibility trouble. Optimizing work before pagination remains a separate idea, requiring deduplication and total-count preservation. |
| ptohy PR #205 | Do not assume Jellyfin 12 fixed every synthetic-ID client issue. This patch targets Moonfin 2.5.1 and bypasses the native detail action in one path. Reproduce on the actual client first, then preserve route authorization and visibility if an alias repair is needed. |
| NoahSKipp `feat/prefetch-streams-endpoint` (`19dd6b4`) | No new speculative probing endpoint. It needs a client that actually calls it and can generate extra network/ffprobe work. Current Gelato prepares stream lists asynchronously; that is distinct from pre-probing a selected stream. AIOStreams preload options alone do not prove that Gelato invokes its precache flow. |

Sources: [nistei branches](https://github.com/nistei/Gelato/branches), [PR #206](https://github.com/lostb1t/Gelato/pull/206), [PR #155](https://github.com/lostb1t/Gelato/pull/155), [PR #205](https://github.com/lostb1t/Gelato/pull/205), [LinuxServer run script](https://github.com/linuxserver/docker-jellyfin/blob/12.0ubu2604-ls48/root/etc/s6-overlay/s6-rc.d/svc-jellyfin/run), [Jellyfin 12 sources](https://github.com/jellyfin/jellyfin/tree/v12.0).

No production configuration, authenticated manifest, credentials or exported addon data are included in this repository or CI. The production manifest request from the development environment returned HTTP 403; no production playback was attempted. End-to-end validation still needs actual client names/versions, the installed LinuxServer image digest and a small reproducible HTTP fixture or isolated test addon. See the validation matrix above before moving production onto this preview.
