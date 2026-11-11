# Music Library Rules

**Reference guide.**

## Paths
- Library: `<home>\Audio\`
- New music inbox: `<home>\Downloads\NewMusic\`
- AudioManager tool: `<home>\GitHubRepos\AudioManagerCLI\` (compiled exe in `bin/Release/`)

## Library Structure
```
Audio/
  Artists/{artist}/{album or Singles}/
  Compilations/
  Miscellaneous Songs/
  Motivation/
  Musivation/{artist}/{album or Singles}/
  Sources/
    Films/{film name}/
    Shows/{show name}/
    Anime/{anime name}/
```

---

## Global Tag Rules (all tracks)

**AUTOMATED VIA TAGFIXER** - These rules are automatically applied by the TagFixer module before integration. Do not manually fix these; the program handles them.

- All files must be `.mp3` format
  - To convert many: Format Factory -> To MP3 -> Add files -> Output to source folder
  - Search Explorer for `NOT *.mp3 AND NOT kind:folder` to find non-MP3s
- `TCMP` = 1 (Compilation) on every track - stops iTunes grouping each track as a separate album
  - **AutoFixed:** TagFixer sets TCMP=1 on all files from NewMusic
- Required tags: Title (TIT2), Artists (TPE1), Album (TALB), Year (TDRC), exactly 1 album cover (APIC)
  - If no album exists, use the track Title as the Album value
- Featured artists semicolon-separated in TPE1, primary artist first: `MainArtist;FeatArtist`
  - **AutoFixed:** TagFixer ensures all featured artists are in TPE1, primary first
- **Remove entire parenthetical phrases from Title and Album tags** (NOT just substrings):
  - ❌ Wrong: Remove substring "feat." → leaves `(Akon)`
  - ✅ Right: Remove entire phrase `(feat. Akon)` → clean title
  - Phrases to remove: `(feat. ...)`, `(ft. ...)`, `(Album Version)`, `(Explicit)`, `(Edit)`, `(Radio Edit)`, `(Original)`, `(Remix)`, `(Version)` 
  - Example: `"Cool Song (feat. Akon)"` → `"Cool Song"` (entire `(feat. Akon)` removed)
  - **Trailing bare credit (BL-65):** a credit at the END of a title with no brackets is also moved to the artist: `"360 featuring robyn & yung lean"` becomes title `"360"` plus artists Robyn, Yung Lean (split once, no recase, deduped). Only `featuring`, `feat.`, `ft.` at the end, or `(featuring X)`; never mid-title, and never a bare `with` (`Stay With Me` and `Dancing With Myself` stay). `(with X)` in brackets still works. Curly apostrophes stay in the tag; only the new file name uses a straight one. `keep=title` skips all of it.
  - **CRITICAL:** Never strip just "feat." or "ft." - they may be part of other words (e.g., "LEFT" contains "ft.", "Safety" contains "feat.")
  - Move featured artist info to TPE1 tag instead: extract artist name from parenthetical and add to artist field as semicolon-separated
  - **AutoFixed:** TagFixer removes full phrases from Title and Album tags, moves featured artists to TPE1
- Genre must match folder for Musivation/Motivation tracks
  - **AutoFixed:** TagFixer sets genre for Musivation/Motivation tracks if not already set

## Filename Convention

**AUTOMATED VIA TAGFIXER** - Filenames are automatically set by the TagFixer module before integration based on the TPE1 (artist) and TIT2 (title) tags.

- Format: `{all TPE1 artists semicolon-separated} - {title}.mp3`
- Example: `Chiddy Bang;Icona Pop - Mind Your Manners.mp3`
  - Not: `Chiddy Bang - Mind Your Manners (feat. Icona Pop).mp3`
  - The "(feat. Icona Pop)" is removed from title tag and Icona Pop is added to TPE1 artist tag
- **AutoFixed:** TagFixer renames files to match this convention, pulling from cleaned TPE1 and TIT2 tags
- Manual alternative: Use MP3Tag's tag-to-filename feature: `%artist% - %title%` (but TagFixer should handle this automatically)

---

## Routing Rules (priority order)
1. Genre = `Musivation` -> `Musivation/` (see Akira The Don / Loot Bryon Smith rules)
2. Genre = `Motivation` -> `Motivation/`
3. Compilation album -> `Compilations/`
4. Discovered via film/show/anime -> `Sources/` (see Sources rules below)
5. Artist folder exists in `Artists/` -> `Artists/{primaryArtist}/` + album subfolder if applicable
6. 3+ songs same artist accumulate in Misc -> move all to new `Artists/{artist}/` folder
7. Otherwise -> `Miscellaneous Songs/`

## Album Subfolder Rule (CRITICAL for routing logic)
**Holistic count: library + integration batch combined**
- Count all songs from this album: existing library copies + new songs being integrated
- **If total is 2+ songs** → create/use album subfolder: `{artist}/{Album Name}/`
- **If total is only 1 song** → put in `Singles/` subfolder: `{artist}/Singles/`
- Example: "Evergreen Nights" (Dylan Owen) has 1 in library + 1 in NewMusic = 2 total → album folder
- Example: Album has 0 in library + 1 in NewMusic = 1 total → Singles folder
- No loose files directly in an artist folder - all files must be in album subfolder or Singles/

## Same Song On Two Albums Rule (David, 2026-11-08)
When one artist has the same song (same artist+title) on two albums, one in the library and one incoming, **the album with more tracks wins** (the "real" release); the song is kept there and the copy on the smaller album is the one replaced. Tie: the incoming copy wins.
- Example: Akira The Don, "Don't Stop Asking Questions" is on library album "Me Is A Disease" (3 tracks) and on incoming "The Real Currency Of Life" (5 tracks) -> keep it on "The Real Currency Of Life", the Me Is A Disease copy goes (that album drops to 2 tracks, and the 2+ rule above still applies).
- Status: the recommendation is implemented: `BuildDupData` in `MusicIntegrator.cs` recommends L or D by album track count (tie: L), after the deluxe, single and compilation rules. It is a recommendation only; no-input runs downgrade both L and D to K unless the manifest resolution names one.

## Duplicate Matching Rule
Duplicate detection (`FindDuplicateInMirror`) compares primary artist + title, case-insensitive, tags only. `NormaliseTitleForDedup()` runs on BOTH sides of every dedup key (index build in `GetAliasExpandedKeys` and lookup), and strips `(feat./ft./featuring X)` only, never (Remix), (Edit) or (Live), which are distinct tracks. Rationale: library tags may predate TagFixer cleanup and the incoming batch is already cleaned, so without it "Song (feat. X)" in the library silently fails to match "Song" in the batch. A missing or "Missing" artist or title never matches.

## Richer Metadata Wins Rule (David, 2026-11-08)
When an incoming track duplicates a library track and the incoming copy, AFTER TagFixer, lists MORE artists than the library copy, **keep the incoming copy** (recommend L), even when the album is the same. More artists means more correct metadata. This is checked BEFORE the same-album "keep library" rule.
- Example: library `Miscellaneous Songs\The Avalanches - Frankie Sinatra.mp3` has only The Avalanches; incoming "Frankie Sinatra (ft. Danny Brown & MF DOOM)" becomes artists The Avalanches;Danny Brown;MF DOOM after Tag Fix -> keep incoming. After L the library copy is replaced and the file routes by the normal rules.
- Status: policy only, not yet built (backlog). Until built, answer the duplicate prompt L for such tracks.

<!-- md-block: mirror-and-artist-folder-decisions -->
## Mirror Commit And Artist Folder Decisions (David, 2026-11-11)
- **Mirror commit is blocked by issues.** David: "COMMITS on audioamirror repo shuld be blocked if there are issues!" Any LibChecker hit leaves the mirror files regenerated on disk but uncommitted (`mirror-result.json` status `libchecker_issues`, hits listed in `hits[]`). Pinned by `MirrorResult_LibCheckerIssues_GivesLibcheckerIssuesAndNeverCommits`.
- **Artist folder matches the tag; tag data takes precedence.** David: "artist folder should mathc artist name closely as psosible to keep things simple", then "artist folder should mathc tag, with folder being renamed to tag, tag data takes precedent". This supersedes the earlier same-day reading that an existing folder keeps its on-disk spelling.
  - The folder name is the tag run through the folder-name sanitiser (a registered mark and similar symbols are stripped), so `BNYX®` lives in `BNYX`; LibChecker compares tag and folder after the same sanitising, so that is not a hit.
  - An existing Artists folder that differs from the tag only by case is renamed to the tag spelling at integrate time: journaled to `artist-folder-rename-*.json` before the first rename, revertable from that journal, listed in the plan. Never a rename across different names.
  - Mixed spellings of one artist (batch tags plus the Misc songs that migrate with them): the folder takes the most frequent spelling (ordinal-first on a tie). Tracks tagged otherwise get a `[review: tag 'X' but folder 'Y'; tag not edited, fix the tag]` note in their routing reason and stay LibChecker hits until David fixes the tag. Tags are never edited silently.
  - David picks the spelling (2026-11-11): "yes with my approval and i choose name fo exmaple for charli i want the captilised form!!! XCX". The plan lists spelling candidates (`artistSpellingCandidates`, a suggestion only); a tag changes only when the manifest carries `artistSpellings` for that artist, through the normal tag fix path with backup and revert, and the folder then follows the chosen spelling. Unattended runs without a choice never change a tag. Contract: `docs/Contracts/RoutingJSONFormat.md`.
<!-- /md-block: mirror-and-artist-folder-decisions -->

---

## Folder: Artists
- Artists who have 3 or more songs in the library
- Albums go in named subfolders; remaining singles go in `Singles/`
- No loose files directly in the artist folder

## Folder: Compilations
- Full compilation albums (various artists, released as a compilation)

## Folder: Miscellaneous Songs
- Random singles that don't meet the 3-song artist threshold
- Periodically review: look for trios to promote to `Artists/`, look for soundtracks to move to `Sources/`

## Folder: Musivation
- All songs must have genre `Musivation`
- See Akira The Don and Loot Bryon Smith rules below

## Folder: Motivation
- All songs must have genre `Motivation`

## Folder: Sources
Tracks discovered through a specific form of media. **CRITICAL DISTINCTION:**

**Official Soundtracks** (album IS the soundtrack compilation, e.g., "Peacemaker (Original Soundtrack)")
- Album field format: `{Show/Movie Name} OST` (e.g., `Peacemaker OST`, `The White Lotus OST`)
- Strip `Original Motion Picture Soundtrack` -> replace with show/movie name + OST
- Folder: `Sources/Shows/{show name}/` or `Sources/Films/{film name}/`
- Example: Cristobal Tapia de Veer - Aloha! -> Album: "The White Lotus OST"

**Songs featured in shows/movies** (regular artist album that appears in credits, NOT a soundtrack)
- **DO NOT add OST to album tag** - album is the artist's original album name
- Album field: keep original (e.g., `Hunting High and Low`, `The Very Best of Bonnie Tyler`)
- Folder: either leave in `Artists/` OR move to `Sources/Shows/{show name}/` with original album tag intact
- Example: a-ha - Take on Me -> Album: "Hunting High and Low" (NOT "OST"), Folder: Sources/Shows/The Super Mario Bros. Movie/
- **Why:** Adding OST is misleading - this is NOT an official soundtrack album, just a featured track
- Strip `feat.` and unwanted strings from tags per global rules

**Anime** (`Sources/Anime/{anime name}/`)
- Use English titles for memorability
- Use anime name as Album
- Full-length version for songs you like, anime-length (OP/ED cut) for lesser ones
- Replace cover art with anime cover art

---

## Akira The Don (Musivation)
- All ATD tracks: genre = `Musivation`, destination = `Musivation/Akira The Don/`
- TPE1 = `Akira The Don;{Sampled Person}` (sampled person MUST be listed as co-artist)
- Routing within ATD folder (holistic count: library + batch combined):
  - **3+ songs from sampled person** -> `People/{person}/` folder (creates folder if needed)
  - **Fewer than 3 songs from sampled person** -> `Singles/` folder
- Example: "NO SMARTER THAN YOU" (Steve Jobs) -> 1-2 songs total from Steve Jobs -> `Singles/`
- Example: "INTO THE INFINITE" (Brian Tracy) -> 3+ songs total from Brian Tracy -> `People/Brian Tracy/`

## Loot Bryon Smith
- Lives in `Musivation/Loot Bryon Smith/` (NOT Artists)
- Genre must be `Musivation`
- Subfolder structure: `Spotify Albums/{album}/`, `Spotify Singles/`, `YouTube Singles/`

---

## Known Gotchas
- Jay-Z: folder is `Jay-Z` - TPE1 and filename must match exactly (not `JAY-Z`)
- `mike.` artist: folder name is `mike` (no trailing dot - Windows limitation)
- Album names containing `Edition` (e.g. `One Of The Boys (15th Anniversary Edition)`) are fine to keep - LibChecker uses word-boundary matching for `edit` so `Edition` is never a false positive
- **Folder name may intentionally differ from ID3 album tag (version disambiguation):** The folder can retain a version suffix for on-disk disambiguation while the ID3 album tag is the clean short name. Example: `Artists\The Notorious B.I.G\Life After Death (2014 Remastered Edition)\` with album tag = `Life After Death`. LibChecker does NOT compare folder name to album tag, so this never triggers an error. This is the preferred state when you own multiple versions of the same album and want to distinguish them on disk while keeping metadata clean. **Implication for routing/TagFixer:** strip version suffixes from the ID3 album tag (clean metadata), but the destination folder name is determined by what's already on disk - a folder named `Life After Death (2014 Remastered Edition)/` is valid and intentional even when the tag says `Life After Death`.
