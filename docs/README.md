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

## rules/

- [Rules/MusicLibraryRules.md](Rules/MusicLibraryRules.md) - tag and routing rules that decide where each track lands in the library.
- [Rules/PostIntegrationValidation.md](Rules/PostIntegrationValidation.md) - why post-integration LibChecker warnings are often false and how to check them.
- [Rules/SafetyConstraints.md](Rules/SafetyConstraints.md) - the safety review of library file operations against the rules.
