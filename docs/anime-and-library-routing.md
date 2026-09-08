# Anime, library routing, and episode mapping

This document is the implementation contract for supporting Anime Movies, Anime TV, Kids TV, and any
number of additional Plex libraries without allowing a release preference or metadata outage to put a
file in the wrong root.

## Core model

Anime and Kids are **classifications**, not media shapes. A title is still either a movie or a series.
Keeping those dimensions separate is required because Plex uses different scanners and naming rules for
movie and television libraries. Anime catalog results therefore retain `MediaType.Movie` or
`MediaType.TvShow`; `IsAnime` is derived from Animation plus Japanese original-language/origin metadata.

The durable dimensions are:

- `MediaKind`: Movie, Series, Album, Artist, or Track.
- classifications: Anime, Kids/Family, Documentary, and other metadata-derived facets.
- `LibraryDestinationId`: the administrator-approved Plex library/root selected for this request.
- acquisition profile: quality plus required audio/subtitle tracks and release preferences.
- series numbering: aired, absolute, DVD/digital, or an explicit per-series mapping.

`MediaType.Anime` remains readable for old rows and API clients during migration, but new TMDB anime
catalog results must not be persisted with that type.

## Phase 1 — truthful anime catalog and request identity

1. Search both TMDB movie and TV namespaces for the Anime filter.
2. Require the Animation genre and a Japanese origin signal.
3. Return each card with its real Movie/TV identity so details, request scope, indexer search, naming, and
   Plex verification all use the correct shape.
4. Preserve season/episode/monitoring fields for legacy `MediaType.Anime` series rows.

This is the first implementation PR because safe destination routing cannot be built on an identity that
cannot represent Anime Movies.

## Phase 2 — named, allowlisted library destinations

Replace raw paths in routing rules with administrator-owned destinations:

```text
LibraryDestination
  Id                  stable identifier
  Name                e.g. Anime Movies, Anime TV, Kids TV
  Kind                Movie | Series | Music
  RootPath            canonical worker-visible path
  PlexSectionId       exact Plex library to scan and verify
  NamingTemplate      optional override
  AllowUserSelection  whether an ordinary requester may choose it
  Enabled
```

There is no fixed destination limit. Every media kind has one required default, and ordered routing rules
select another compatible destination using classification, genre, quality profile, or user/group. Existing
Movie/TV/Music paths and root rules are migrated into destinations without changing current imports.

The resolved destination ID is persisted on the request when it is approved and copied to the fulfillment
job. The worker revalidates that the destination kind matches the job and that the final canonical path is
contained beneath its configured root. It never accepts an arbitrary path from a browser or API request.
User choice, when enabled, is limited to compatible allowlisted destinations.

Classification-dependent routing fails closed. If a request is explicitly Anime but metadata cannot confirm
its shape/classification, it stays in a visible `Needs metadata` state; it does not fall through to the normal
Movie or TV root. A changed admin rule affects future approvals only unless an admin explicitly reroutes a
queued job.

Recommended initial rules:

| Priority | Match | Destination |
| --- | --- | --- |
| 1 | Movie + Anime | Anime Movies |
| 2 | Series + Anime | Anime TV |
| 3 | Series + Kids/Family + Animation | Kids TV |
| 4 | Movie | Movies |
| 5 | Series | TV Shows |

Plex requires movie and TV content to be separated and specifically supports using a Kids sub-root as its
own library source: <https://support.plex.tv/articles/naming-and-organizing-your-tv-show-files/>.

## Phase 3 — audio and subtitle policy

Language requirements extend the existing quality-profile/custom-format system and work for any media, not
only anime. An acquisition profile gets structured constraints:

```text
RequiredAudioLanguages       all must exist (e.g. en + ja)
AllowedAudioLanguages        optional allowlist
RequiredSubtitleLanguages    all must exist (e.g. en)
RequireForcedSubtitle        optional
AllowUnknownTrackLanguage    false for strict profiles
ReleaseHintRules             preferred/rejected title tokens and group scores
```

Initial presets:

- Dual audio: require English and Japanese audio.
- Japanese with English subtitles: require Japanese audio and English subtitle.
- English dub: require English audio.
- Any/original: no hard language requirement.

The normal **Smart** profile is itself an enforceable anime safety contract, not merely a score bonus. For
anime it accepts either the preferred audio language, or Japanese audio with subtitles in the preferred
subtitle language. Thus English/Japanese dual audio and Japanese-with-English-subtitles both work, while a
German/Japanese multi-audio release with no English translation does not slip through just because it has
many seeders. Ordinary foreign films remain valid Smart fallbacks; the narrower rule is tied to the durable
anime classification. Smart anime also retains matching sidecar subtitles even when the global library
setting would otherwise discard subtitles.

There are two enforcement stages:

1. **Before download:** parsed release-title tokens and trusted release-group history rank/reject obvious
   mismatches. This saves bandwidth but is not proof; `Dual Audio`, `MULTI`, and `ENG SUB` are inconsistent.
2. **Before import:** inspect every selected media file with MediaInfo (`ffprobe`/`mkvmerge -J` are equivalent
   implementation options) and evaluate audio and subtitle tracks separately. Only this stage can prove the
   requirement. Missing/undefined language tags fail a strict profile. A mismatch is blocklisted and removed
   from transfer staging, and the job searches the next candidate; no file reaches any Plex root first.

Accepted-file track inventories are stored in the import audit; rejected-file decisions and their exact reason
are stored in the release blocklist. This lets an admin see why a release was accepted or rejected. FFprobe
exposes stream metadata and MKVToolNix exposes machine-readable track identification and BCP 47/ISO language tags:
<https://ffmpeg.org/ffprobe.html>, <https://mkvtoolnix.download/doc/mkvmerge.html>.

For MKV imports, profiles can also prepare playback order automatically. Smart mode physically puts English audio
first for ordinary media, retaining a forced English subtitle when present. Anime puts Japanese audio and the best
full English subtitle first, avoiding forced/signs-only, SDH, and commentary tracks when a normal translation
exists. It also sets Matroska default flags, but does not rely on those flags alone because Plex may ignore them.
The remux runs on a private staged library copy and its track/attachment/chapter/tag inventory is verified before
the atomic commit, so hardlinked torrent data and the previous Plex file are never modified. If preparation or
verification fails, the import fails safely and searches another release rather than publishing an ambiguous file.

## Phase 4 — numbering and multi-episode files

Series get an explicit numbering scheme. TMDB episode groups can represent original-air-date, absolute, DVD,
digital, story-arc, production, and TV orders:
<https://developer.themoviedb.org/reference/tv-episode-group-details>.
Indexer parsing retains every recognized form (`SxxEyy`, ranges, absolute numbers, season packs) and maps it to
canonical Plex episodes before ranking.

One physical file may cover multiple logical episodes. Model that as a one-to-many relation rather than
pretending the file is only the first episode:

```text
ImportedMediaFile 1 ── * ImportedEpisodeCoverage(Season, Episode)
```

For a contiguous range, name it `Show - S02E18-E19.ext`, which Plex recognizes. Plex will display both
episodes but play the full physical file for either entry. Automatic splitting is allowed only when reliable
chapter/timestamp boundaries exist; otherwise the file remains a range. Ambiguous bundles go to manual mapping
once that workflow is available, instead of being copied, guessed, or marked complete. Plex documents both
alternate episode ordering and the multi-episode filename format in its TV naming guide linked above.

The range/coverage slice persists that one-to-many audit, protects upgrades and issue replacements from
deleting a sibling episode, and fails closed on unmapped or overlapping files. Until the manual mapping UI and
alternate-order profiles ship, an ambiguous release is blocklisted with its exact reason and the job searches
another candidate; it never enters a Plex root.

Kids cartoons use the same mapping machinery. They differ by destination/classification and often exercise
DVD/digital or multi-episode mappings; they are not a separate hard-coded media type.

## Phase 5 — release qualification and collection preflight

Anime release names are not scene-TV names. They commonly have a leading release group, romanized or alternate
titles, absolute episode numbers, version suffixes, edition/source shorthand such as `BD`, and franchise batches
whose only usable season or story-arc identity is in numbered parent folders. Treating every unmatched token as
part of the title makes good releases invisible; treating every unnumbered batch as a complete series risks a
wrong import.

The decision ladder is deliberately fail-closed:

1. Normalize anime-only identity conventions without weakening ordinary movie/TV identity matching. Retain the
   original title and release group for audit and custom-format scoring.
2. Prefer candidates whose outer name proves canonical episode coverage. For alternate orders, translate through
   the immutable per-series map before planning.
3. For an unscoped collection, fetch torrent metadata before payload data and parse every video path into a
   proposed manifest: source identity, canonical identity, duplicate/overlap state, extras, and selected bytes.
4. Accept automatically only when that manifest uniquely covers the outstanding canonical episodes, stays within
   the pack byte limit, and every selected video can be mapped. Apply file priorities before resuming payload.
5. Inspect actual audio/subtitle tracks before import as already required by the language policy. A title hint can
   improve ranking but never proves a dual-audio or English-subtitle requirement.
6. If no candidate is provably safe after retries, notify an admin once. The review screen must show the real
   rejection reasons and require a configured episode map before it allows an explicit force-download. Releases
   with conflicting language hints get a separate warning and confirmation. Even then, the organizer enforces
   the mapping, target set, and actual audio/subtitle contract before any Plex write.

The qualification slice implements steps 1, 2, and 6, including numbered story-arc folders and preservation of
every canonical season target during a forced collection download. The manifest slice implements steps 3 and 4
with Deluge's purpose-built `prefetch_magnet_metadata` operation: Deluge retrieves the content-addressed metadata in upload
mode without retaining a session torrent, the worker verifies the raw info dictionary against the magnet's SHA-1,
and the accepted file-priority list is applied while adding that exact metadata. An unscoped collection remains
labelled `PackScopeUnknown`; it becomes a provisional automatic fallback only when an immutable episode map covers
the whole job, and no payload starts unless the manifest independently proves every target exactly once. Deluge's
implementation describes this RPC as downloading magnet metadata for file selection before adding it:
<https://github.com/deluge-torrent/deluge/blob/develop/deluge/core/core.py>.

Manifest parsing is bounded by byte, file, node, and nesting limits. It rejects a hash mismatch, malformed bencode,
unsafe paths, duplicate canonical episodes, mixed target/non-target multi-episode files, missing targets, unknown
selected file lengths, and a selected payload above the profile's pack limit. Non-video extras and mapped episodes
outside the request stay at priority zero. A timeout is retryable; a structurally invalid collection is blocklisted
for that request so retries advance to another candidate instead of looping.

The verified priority set stays authoritative after Deluge exposes its live file list. A second, defensive trim
re-derives the same canonical video coverage but treats unnumbered video files (NCOP, NCED, samples, trailers, and
other extras) as deselected—not as generic companion files. Recognized subtitle sidecars remain selected so a
Japanese-audio release can still satisfy Smart anime through English subtitles. This prevents post-add monitoring
from widening a payload-free 12-file decision into 22 files and later failing import on the extras it re-enabled.

When an anime job has no configured order, claim now includes bounded, server-authored snapshots of every valid
TMDb episode group for that series. The downloader inspects at most the three best collections rejected solely for
unknown outer scope and runs the real manifest through every snapshot. It adopts an order automatically only when
one complete mapping contract uniquely passes; duplicate TMDb groups are equivalent only when their entire
source-to-canonical maps are identical, not merely when they happen to cover today's requested subset. The worker
sends only the selected group id back, and the web app re-imports that group from TMDb before freezing it onto the
current job. It does not change the reusable series setting because a later release may use a different order.
For numbered collection folders, imported profiles also retain each TMDb group's name. Both the ordinal and a
conservative tokenized title match must agree; folder `02 - Kizumonogatari` can never satisfy official group 2
`Nisemonogatari` merely because the numbers collide. Explicit `S1`/`Part 2`/`Cour 2` suffixes are tolerated, while
arbitrary extra title words fail closed and appear in the manifest diagnostic.
Season-based TMDb profiles saved before this folder-title provenance existed are not trusted silently; the admin
must re-import that group once. Legacy hand-authored maps remain valid, and absolute groups do not need folder
names because their source identity is the absolute episode number itself.

Mixed franchise archives intentionally remain a last-resort review case. A collection that embeds movies among TV
arcs, resets numbering under title folders, splits one official group across multiple folders, or predates current
episodes cannot be made safe by an episode-count coincidence. It stays payload-free, records the conflicting or
missing group evidence in the job error, keeps retrying other releases, and reaches the existing one-time admin
notification after the retry threshold. The per-series custom metadata builder is that explicit review layer:

- Each source folder and episode maps to one exact Plex season/episode. Numbered collection folders can also carry
  an expected arc name, so both the ordinal and title must agree.
- Season 0 rows are first-class OVA, ONA, or Special targets. The **Wanted** switch decides whether a whole-series
  request monitors that sparse target; unselected recaps and extras remain mapped for identity but never download
  merely because they exist in an archive.
- Several source rows may share one Plex target only with a complete, consecutive `Part 1..N` contract. The manifest
  requires every part, and the organizer losslessly appends compatible MKV streams into one atomic Plex file. A
  missing part, duplicate part, external subtitle sidecar, incompatible stream layout, or join failure publishes
  nothing and leaves the source material intact.
- Custom season labels, episode titles, summaries, and original dates are locked through Plex's metadata API after
  availability scans. This preserves an administrator's chosen order without changing the metadata agent for the
  entire library. Mapped targets that Plex has not indexed yet are reported rather than fabricated.

The builder can be seeded from a TMDb episode group and then edited, or authored entirely by hand. Saving validates
the whole contract before it can be snapshotted onto a job. In-flight jobs keep their immutable snapshot; applying a
new contract requires a deliberate requeue, preventing a mid-download edit from changing destinations underneath
the worker. It must never reinterpret the files silently.

Jobs created before language-policy snapshots existed are repaired at claim time. The web service resolves the
job/request's quality profile and freezes its current media policy before returning work to the downloader; an
existing snapshot is never overwritten. If an old row cannot resolve any profile, it receives the conservative
Smart English/English fallback instead of crossing the process boundary without track validation. This matters
after long deferrals: retrying an old anime request must not bypass protections added while it was waiting.

Partial progress is non-terminal. Anime and kids catalogs rarely expose one trustworthy release for an entire
multi-season request, so each successfully imported pack is recorded as durable episode coverage and subtracted
from the same immutable job contract. The worker discards its completed transfer plan before the server makes the
job claimable again, waits through a short hand-off window, then searches only the remaining episodes. A manual
grab is converted back to automatic search for the remainder so it cannot replay the administrator's one-season
choice. If later passes collectively cover the complete target, the request becomes Available; otherwise it stays
Partially available while retries continue. A legacy `MediaType.Anime` request also uses series/episode-aware Plex
verification—one visible episode is never enough to complete a whole-series request. Only repeated searches with
no safe match reach the one-time admin review notification.

Uploader season numbers are not canonical identity. Every job freezes the provider's name and episode count for
all seasons—not only the seasons currently missing—because franchise/arc uploaders routinely call canonical S03
"S04" or restart a later named season at S01. A release number is translated only when its title uniquely contains
one distinctive canonical season name and still carries the series identity; generic names such as "Season 4" are
not evidence. The transfer stores both source and canonical season numbers, manifest preflight requires every
selected filename to use the expected source season, and the organizer applies the same translation before naming
or writing anything. Already-satisfied season identities remain in the frozen catalog so their releases cannot
later impersonate a still-missing season. Configured/TMDb episode-order maps remain authoritative when present.

Some named-season packs reset their internal files to absolute `- 01`, `- 02`, … numbering instead of repeating
the uploader's `Sxx`. That sentinel is accepted only after the outer release uniquely maps a distinctive canonical
season name and each internal path repeats that same series + season identity. Manifest preflight must then prove
the exact, gap-free canonical target set; post-add priority selection and the organizer independently repeat the
same check. A bare number, generic season name, different arc, duplicate, gap, or out-of-range episode still fails
closed and remains eligible for the normal admin-review escalation rather than being guessed into Plex.

Release aliases should eventually come from a durable identity table populated from metadata aliases and AniDB's
title dump, not an ever-growing stop-word list. Alias matches are ranking evidence only; canonical provider IDs and
the manifest mapping remain the authority.

## End-to-end acceptance criteria

- Anime search returns both movies and series with correct detail pages and request scopes.
- Anime Movie and Anime TV requests resolve to different named destinations and exact Plex sections.
- A metadata outage or incompatible user selection cannot fall through into another library.
- Strict dual-audio and sub/dub profiles reject a downloaded file before import when its actual tracks fail.
- A rejected release is blocklisted and the next candidate is tried without looping.
- A multi-episode file records coverage for every logical episode and receives Plex-compatible range naming.
- Kids, anime, and normal TV can use aired/absolute/DVD/custom mappings without duplicating files.
- Routing, track inspection, episode coverage, import, Plex scan, and verification are visible in one audit trail.
- An unscoped anime collection never starts automatically until its internal manifest proves unique canonical
  coverage; the last-resort admin path preserves the same import and target safeguards.
- Automatic episode-order discovery persists its authoritative TMDb group on the current job only and rejects
  zero-match or conflicting-match manifests without starting payload data.
- Legacy queued anime jobs cannot be claimed without a frozen language contract, and Smart anime rejects actual
  tracks unless they contain the preferred dub or Japanese audio with preferred-language subtitles.
- A partial season/episode import automatically narrows and continues the same job without re-downloading proven
  coverage, replaying a manual grab, or falsely marking a legacy Anime series complete after one episode.
- A distinctive canonical season name can safely translate an uploader season number, and that source/canonical
  pair survives worker restarts; a generic or conflicting manifest stays blocked for admin review.
- A uniquely named season pack may use `- 01`-style internal files only when every selected path repeats that same
  canonical season identity and the manifest proves exact coverage; unscoped absolute numbering remains blocked.
- Monitoring cannot enqueue a second season/episode job while an active whole-series job already owns that exact
  canonical target. Queued, downloading, and deferred target snapshots plus durable imports are combined before a
  monitor child is allowed, preventing duplicate payloads and two organizers racing the same Plex path.
- Fractional/special names such as `S01E06.5`, `1x06.5`, and `- 06.5` are never truncated to canonical E06. They
  remain unselected extras inside an otherwise complete pack, or reject an attempted E06 fulfillment with an
  explicit mapping/admin-review reason; normal `E06v2` revisions remain valid episode 6 releases.
- One narrow fractional insertion can map automatically when a complete, uniquely named season manifest proves
  the whole sequence. For example, source `01..06, 06.5, 07..14` maps by position to canonical `01..15`; the
  insertion point is frozen on the durable transfer and rechecked during post-add selection and final import.
  Incomplete, duplicated, multi-fraction, non-`.5`, generic, and standalone cases continue to fail closed and
  surface through the normal admin-review notification only after automatic safe searches are exhausted.
- Automatic episode-mapping blocklist decisions carry the mapping-engine version that produced them. A newer
  engine can reconsider older parser decisions instead of permanently hiding a now-safe release, while manual
  blocks, confirmed wrong content, and immutable track-policy failures remain durable and cannot be weakened by
  a later automatic rejection. Interactive and background searches apply the same expiry/version policy.
- The one-time stalled-search notification is the last-resort human handoff. It deep-links an admin to the exact
  request's interactive results, where accepted and rejected candidates and their reasons remain visible. A
  manual grab does not bypass manifest mapping, actual-track inspection, atomic import, or Plex verification.
- A multi-season anime collection may satisfy only the missing seasons whose internal folders uniquely match
  frozen canonical season names. Each season must independently prove exact current coverage, and the combined
  selection is repeated after add and at final import. Ambiguous sibling arcs are left for another release while
  the same job continues; a structurally rejected collection is versioned-blocklisted so it cannot loop forever.
