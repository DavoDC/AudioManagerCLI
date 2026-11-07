# Reference docs index

Long-form reference material for AudioManager, grouped by folder. Start with DevContext for implementation invariants.

## Root

- [DevContext.md](DevContext.md) - implementation invariants, project structure and code patterns for routing, tags and integration.

## architecture/

- [architecture/IntegrationAndParseCacheArchitecture.md](architecture/IntegrationAndParseCacheArchitecture.md) - stage 3 integration and ParseCache internals for developers.
- [architecture/ArchitectureLanguageDecision.md](architecture/ArchitectureLanguageDecision.md) - the decided engine language (C# only) and why.
- [architecture/ArchitectureCourse.html](architecture/ArchitectureCourse.html) - a standalone course page on the architecture.

## contracts/

- [contracts/AnalysisJSONFormat.md](contracts/AnalysisJSONFormat.md) - the JSON data contract written by `analysis --json-output`.
- [contracts/AudioMirrorFormat.md](contracts/AudioMirrorFormat.md) - the layout and meaning of AudioMirror XML files.

## rules/

- [rules/MusicLibraryRules.md](rules/MusicLibraryRules.md) - tag and routing rules that decide where each track lands in the library.
- [rules/PostIntegrationValidation.md](rules/PostIntegrationValidation.md) - why post-integration LibChecker warnings are often false and how to check them.
- [rules/SafetyConstraints.md](rules/SafetyConstraints.md) - the safety review of library file operations against the rules.

## design/

- [design/MusicIntegratorDecompositionDesign.md](design/MusicIntegratorDecompositionDesign.md) - a proposal to split MusicIntegrator into smaller pieces.
