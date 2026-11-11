# Reference docs index

Long-form reference material for AudioManager, grouped by folder. Start with DevContext for implementation invariants.

## Root

- [DevContext.md](DevContext.md) - implementation invariants, project structure and code patterns for routing, tags and integration.

## architecture/

- [Architecture/IntegrationAndParseCacheArchitecture.md](Architecture/IntegrationAndParseCacheArchitecture.md) - stage 3 integration and ParseCache internals for developers.
- [Architecture/ArchitectureLanguageDecision.md](Architecture/ArchitectureLanguageDecision.md) - the decided engine language (C# only) and why.
- [Architecture/ArchitectureCourse.html](Architecture/ArchitectureCourse.html) - a standalone course page on the architecture.
- [Architecture/MusicIntegratorDecompositionDesign.md](Architecture/MusicIntegratorDecompositionDesign.md) - a proposal to split MusicIntegrator into smaller pieces.

## contracts/

- [Contracts/AnalysisJSONFormat.md](Contracts/AnalysisJSONFormat.md) - the JSON data contract written by `analysis --json-output`.
- [Contracts/AudioMirrorFormat.md](Contracts/AudioMirrorFormat.md) - the layout and meaning of AudioMirror XML files.
- [Contracts/LibFixJSONFormat.md](Contracts/LibFixJSONFormat.md) - plan, journal and result JSON of the `--libfix-*` library fix commands.

## rules/

- [Rules/MusicLibraryRules.md](Rules/MusicLibraryRules.md) - tag and routing rules that decide where each track lands in the library.
- [Rules/PostIntegrationValidation.md](Rules/PostIntegrationValidation.md) - why post-integration LibChecker warnings are often false and how to check them.
- [Rules/SafetyConstraints.md](Rules/SafetyConstraints.md) - the safety review of library file operations against the rules.

## Where rules live

One canonical home per kind of rule. A rule is stated in its home only; anywhere else gets a one-line link, never a restatement.

| Kind | Canonical file |
|------|----------------|
| Tag cleanup, title and artist rules, routing, duplicate matching and policy | [Rules/MusicLibraryRules.md](Rules/MusicLibraryRules.md) |
| Safety review of file operations | [Rules/SafetyConstraints.md](Rules/SafetyConstraints.md) |
| LibChecker false-warning guidance | [Rules/PostIntegrationValidation.md](Rules/PostIntegrationValidation.md) |
| JSON fields and statuses (plan, apply, routing, gate, mirror, analysis) | [Contracts/](Contracts/) |
| Operator steps for a real integration | [GoLiveRunbook.md](GoLiveRunbook.md) |
| Check a batch is safe before integrating (readiness and rehearsal scripts) | [BatchReadiness.md](BatchReadiness.md) |
| Code invariants and patterns | [DevContext.md](DevContext.md) |

The top-level README is product overview only: no per-rule text. A test (`DocSingleSourceTests`) fails when README.md or CLAUDE.md contains rule-only keywords.
