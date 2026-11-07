# Architecture: Language Decision

Decided 2026-09-06, do not relitigate: the engine is C# only.

Reasoning: TagLib# covers every tagging use case the engine needs (MP3/ID3v2), the library and routing logic live in one language with one test host, and the stable contract for outside callers is the engine's versioned output, not its internals.

Consequence: no other language is added to this repo, and no external caller writes to the library, reimplements engine logic, or parses AudioMirror XML. Callers go through the engine's executable and its versioned JSON.
