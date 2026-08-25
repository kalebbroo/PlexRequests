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

There are two enforcement stages:

1. **Before download:** parsed release-title tokens and trusted release-group history rank/reject obvious
   mismatches. This saves bandwidth but is not proof; `Dual Audio`, `MULTI`, and `ENG SUB` are inconsistent.
2. **Before import:** inspect every selected media file with `ffprobe` (or `mkvmerge -J`) and evaluate audio
   and subtitle tracks separately. Only this stage can prove the requirement. Missing/undefined language tags
   fail a strict profile. A mismatch is quarantined, the release is blocklisted for that request/profile, and
   the job searches the next candidate; no file reaches any Plex root first.

The inspection result and policy decision are stored in the import audit so an admin can see exactly which
tracks caused acceptance or rejection. FFprobe exposes stream metadata and MKVToolNix exposes machine-readable
track identification and BCP 47/ISO language tags:
<https://ffmpeg.org/ffprobe.html>, <https://mkvtoolnix.download/doc/mkvmerge.html>.

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
instead of being copied, guessed, or marked complete. Plex documents both alternate episode ordering and the
multi-episode filename format in its TV naming guide linked above.

Kids cartoons use the same mapping machinery. They differ by destination/classification and often exercise
DVD/digital or multi-episode mappings; they are not a separate hard-coded media type.

## End-to-end acceptance criteria

- Anime search returns both movies and series with correct detail pages and request scopes.
- Anime Movie and Anime TV requests resolve to different named destinations and exact Plex sections.
- A metadata outage or incompatible user selection cannot fall through into another library.
- Strict dual-audio and sub/dub profiles reject a downloaded file before import when its actual tracks fail.
- A rejected release is blocklisted and the next candidate is tried without looping.
- A multi-episode file records coverage for every logical episode and receives Plex-compatible range naming.
- Kids, anime, and normal TV can use aired/absolute/DVD/custom mappings without duplicating files.
- Routing, track inspection, episode coverage, import, Plex scan, and verification are visible in one audit trail.
