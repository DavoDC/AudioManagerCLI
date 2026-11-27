# AudioMirror Format

## Repo
- Path: `<home>\GitHubRepos\AudioMirror`
- Commit format: `Mar 8 Update` (abbreviated month + day number + "Update")

## Commit Rules
- **Do NOT commit if LibChecker reports any issues.** Fix all issues first, then commit both AudioMirror and the audio report together.
- If LibChecker is clean, the audio report shows a single `LibChecker: Clean` line.
- If LibChecker has issues, they are prominently flagged in the report and the AudioMirror commit is blocked until resolved.

## XML Format

Each track is an individual `.xml` file. **Final schema (locked 2026-06-06):**
```xml
<Track>
  <Title>Song Name</Title>
  <Artists>Artist1;Artist2</Artists>   <!-- semicolon-separated, first = primary -->
  <Album>Album Name</Album>
  <Year>2001</Year>
  <TrackNumber>1</TrackNumber>
  <Genres>Rap; Hip Hop</Genres>
  <Length>00:04:38.9520000</Length>
  <AlbumCover>
    <Count>1</Count>
    <Width>800</Width>
    <Height>800</Height>
  </AlbumCover>
  <Compilation>True</Compilation>
  <HasLyrics>True</HasLyrics>          <!-- optional: omitted when unknown -->
  <DateAdded>2026-03-04</DateAdded>    <!-- optional: first-seen date, yyyy-MM-dd -->
  <FileBytes>7654321</FileBytes>       <!-- optional: MP3 size in bytes -->
</Track>
```

`HasLyrics`, `DateAdded` and `FileBytes` are optional: an XML without them reads as unknown (null), and the writer omits each one when it is unknown. Older XML stays valid.

`DateAdded` is the day a track was first seen. A regeneration reads it from the XML it is about to replace (in memory, no sidecar file) and writes the same value back, so it is never overwritten once stored. A track with no earlier XML, or an earlier XML with no `DateAdded`, gets the day of the run. `FileBytes` is the MP3's size from the file system at the time the XML is written. The parse cache (`PARSE_CACHE_V4`) carries both fields; an older cache is discarded and rebuilt.

**Schema policy:** This is the only supported schema. AudioManager enforces strict parsing - all fields are required. No backwards compatibility with old formats. Schema changes require full library regeneration.

## Folder Structure

Under `AUDIO_MIRROR/`:
- `Artists/<ArtistName>/<Album>/` - standard tracks
- `Compilations/`, `Miscellaneous Songs/`, `Motivation/`, `Musivation/`, `Sources/` - special categories
