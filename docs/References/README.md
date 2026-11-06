# Reference docs index

Long-form reference material for AudioManager, grouped by folder. Start with DevContext for implementation invariants.

## Root

- [DevContext.md](DevContext.md) - implementation invariants, project structure and code patterns for routing, tags and integration.

## architecture/

- [architecture/Integration-and-ParseCache-Architecture.md](architecture/Integration-and-ParseCache-Architecture.md) - stage 3 integration and ParseCache internals for developers.
- [architecture/Architecture-Language-Decision.md](architecture/Architecture-Language-Decision.md) - the decided polyglot split between C# and Python and why it stays.
- [architecture/Architecture-Course.html](architecture/Architecture-Course.html) - a standalone course page on the architecture.

## contracts/

- [contracts/AnalysisJson-Format.md](contracts/AnalysisJson-Format.md) - the JSON data contract written by `analysis --json-output`.
- [contracts/AudioMirror-Format.md](contracts/AudioMirror-Format.md) - the layout and meaning of AudioMirror XML files.

## rules/

- [rules/Music-Library-Rules.md](rules/Music-Library-Rules.md) - tag and routing rules that decide where each track lands in the library.
- [rules/Post-Integration-Validation.md](rules/Post-Integration-Validation.md) - why post-integration LibChecker warnings are often false and how to check them.
- [rules/SAFETY_CONSTRAINTS.md](rules/SAFETY_CONSTRAINTS.md) - the safety review of library file operations against the rules.

## design/

- [design/Execution-Record-Contract-Research.md](design/Execution-Record-Contract-Research.md) - research findings behind the execution-record contract.
- [design/Execution-Record-Contract-Design.md](design/Execution-Record-Contract-Design.md) - the settled execution-record contract design.
- [design/MusicIntegrator-Decomposition-Design.md](design/MusicIntegrator-Decomposition-Design.md) - a proposal to split MusicIntegrator into smaller pieces.
