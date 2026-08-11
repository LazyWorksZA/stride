# SDSL/SDFX grammar and conformance harness — spec A

Status: **in progress.** Sections 1 and 2 are agreed. Section 3 is presented but not yet
approved, and sections 4 to 6 are not written. Open questions are listed at the end.

## Context

SDSL has no written definition. The language is whatever the hand-written parser in
`sources/shaders/Stride.Shaders.Parsers/Parsing/SDSL/` accepts — 83 files, about 13,800 lines of
parser combinators — and before that it was "whatever HLSL did". There is no way to state which
parts of the language the test suite exercises, and no mechanism that stops the parser and the
language drifting apart.

This spec covers the first of three pieces of work:

- **A (this document)** — the grammar, a recognizer for it, and a conformance gate.
- **B** — coverage measurement: which productions the corpus actually exercises. Needs A.
- **C** — grammar-directed fuzzing, nightly. Needs A, independent of B.

The three were split because A's cost dominates and because B's design depends on what A's
recognizer turns out to look like. Each lands as its own PR.

## What exploration found

Measured on `upstream/master` at `72b6ad680`. These numbers exclude `obj/` and `bin/`; counts
taken over the whole tree are roughly 3x higher because build output duplicates the corpus.

**Corpus.** 593 `.sdsl` and 60 `.sdfx`. Total 952 KB, mean 1,605 bytes, largest 88,604 bytes
(`Rendering/Images/AntiAliasing/FXAAShader.sdsl`).

**One direction of the gate already exists.** `ParsingTests.ParseFile` in
`sources/shaders/Stride.Shaders.Tests/ParsingTests.cs` already asserts that every `.sdsl` and
`.sdfx` under Stride.Graphics, Stride.Rendering and Stride.Engine parses without errors, wired
through `Content Include` globs in the test csproj. That is 397 of the 593 `.sdsl` files.

**The corpus has a gap.** About 100 files sit outside those globs, including all 81 shaders in
Stride.Voxels, plus Particles, Video, BepuPhysics, the samples and the templates.

**The existing gate parses one branch of every conditional.** Production preprocesses with real
defines (`ShaderLoaderBase.cs:177`); the test path preprocesses with an empty define set. The
corpus holds 817 `#define`, 315 `#if`, 145 `#else`, 124 `#ifndef` and 29 `#ifdef` across 88 files,
so every `#ifdef FOO` true branch is currently invisible to CI. Quantifying this belongs to B.

**There is already a second definition of the language.** `generate-sdsl-grammar.cs` under
`sources/tools/Stride.VisualStudio.Package.Shaders/` generates a TextMate grammar for editor
colouring by reflecting the vocabulary out of `Stride.Shaders.Parsing.SDSL.Reserved`. A CFG is a
third. Its header instructs the author to rerun it by hand after the keyword list changes, which
is the same class of manual anti-drift step this work replaces with a gate.

**SDFX is small.** 524 lines of parser and AST against SDSL's 13,785 — about 4%.

## Decisions

### Destination

Upstream `stride3d/stride` eventually. This constrains dependencies and CI cost.

### Grammar input: post-preprocessor text

The grammar describes what `SDSLParser.Parse` actually receives, after `MonoGamePreProcessor`.
This keeps the grammar genuinely context-free. Directives can open a brace in one branch and close
it in another, which is not context-free and would force escape hatches into the grammar.

Consequence: a file is only covered under whatever define set it is fed. Widening coverage across
conditional branches is a corpus problem, not a grammar problem, and belongs to B.

### Formalism: plain EBNF plus an Earley recognizer, no dependencies

Considered and rejected:

| Option | Why not |
| --- | --- |
| ANTLR4 | Alive (`Antlr4.Runtime.Standard` 4.13.1, Sep 2023, 41M downloads) but codegen needs Java on every runner unless generated code is committed. Ordered choice hides grammar ambiguity. |
| Hime | `Hime.Redist` 4.0.0, last published Jun 2022, 304K downloads. Dormant. |
| Pegasus | 4.1.0, last published Mar 2019. Dormant, and PEG cannot detect ambiguity. |
| Pidgin, Superpower, Parlot, CSLY | Alive, but the grammar is C# code, not a reviewable artifact. Writing a second combinator parser to check the first is more work for a weaker result. |

Deciding factor: `a953051eb "[deps] - Remove unused Irony (#1685)"` and
`74399a357 "Shader: removed old ShaderMixer and HLSL to GLSL system"`. Stride already had a parser
framework in this subsystem and deliberately removed it. The current combinator parser exists
because they moved off one. A zero-dependency proposal avoids relitigating that.

Plain EBNF also gives what the alternatives do not: ambiguity is reported rather than silently
resolved, which matters for an artifact whose job is to define a language; the notation needs no
tool-specific knowledge to review; and generating sentences from it for C is simpler than
inverting ANTLR's left-recursion rewriting.

Accepted cost: we own the engine. Mitigated by keeping it independently unit-testable against toy
grammars, including deliberately ambiguous ones.

### Scope: SDFX first, then SDSL, in one spec

SDFX is 4% of the grammar work and serves as the proving ground. Build the EBNF loader, the
recognizer, the corpus runner and the CI gate against a ~100-line grammar, then start the SDSL
grind with debugged machinery underneath.

### Placement: a new test project

`sources/shaders/Stride.Shaders.Grammar.Tests/`, graphics-API independent, one `ProjectReference`
to `Stride.Shaders.Parsers`.

Not folded into `Stride.Shaders.Tests`: that project already carries D3D11 and Vulkan frame
renderers, Silk.NET, Vortice and SPIRV-Cross, and cannot cleanly unit-test an engine that is not
itself a test. Not a shipped library: no test-support-assembly precedent exists in the repo, and
B and C are also tests, so they land as more files in this same project without a test project
having to reference another.

## Architecture

```
Stride.Shaders.Grammar.Tests/
  Grammar/
    sdfx.ebnf              the SDFX grammar        ~100 lines
    sdsl.ebnf              the SDSL grammar        ~500 lines
  Ebnf/
    EbnfParser.cs          .ebnf text -> Grammar model
    Grammar.cs             rules, alternatives, terminals
  Earley/
    Recognizer.cs          Grammar + tokens -> accept/reject + diagnostics
    ItemSet.cs
  Lexing/
    Lexer.cs               source text -> tokens, driven by the grammar's lexical section
  Conformance/
    Corpus.cs              enumerates the corpus, applies the preprocessor
    GrammarAcceptsCorpus.cs
    ParserAcceptsCorpus.cs
    NegativeCorpus.cs
  EbnfParserTests.cs       unit tests, toy grammars
  RecognizerTests.cs       unit tests, toy grammars including ambiguous ones
```

Data flow for one corpus file:

```
.sdsl on disk
  -> MonoGamePreProcessor.Run(text, path, defines)   reused
  -> CommentProcessedCode.Span                       reused
  -> Lexer                                           tokens
  -> Recognizer(Grammar)                             accepted | rejected + furthest position
```

Each component has one job and a boundary that can be tested without the next one: the EBNF parser
knows nothing about SDSL, the recognizer nothing about lexing, the lexer nothing about the corpus,
the conformance tests nothing about Earley internals.

### Three deliberate reuses

- `MonoGamePreProcessor` — the gate sees exactly what the compiler sees.
- `CommentProcessedCode` — public struct exposing comment-stripped text plus a `Links` table
  mapping back to original positions, so comment handling never diverges and diagnostics get
  accurate source locations for free.
- `Reserved.TypeNames` / `Reserved.Keywords` — the lexer's vocabulary is read at runtime from the
  parser's own sets, so the keyword lists cannot drift. The fields are `internal` on a public
  class; reach them by reflection as `generate-sdsl-grammar.cs` already does, or via
  `InternalsVisibleTo`.

## Lexing

The parser is scannerless — combinators run over characters. Matching that exactly would need an
Earley recognizer over a character alphabet, which is the highest-fidelity option. It is not
viable: Earley is O(n^2) even on unambiguous grammars, and FXAA at n = 88,604 characters is about
7.9e9 item operations for one file. Tokenised, that file is roughly 20,000 tokens and the mean
file about 400.

So the grammar is tokenised, and lexer fidelity is treated as a named risk with three structural
mitigations: comments never reach the lexer; the keyword vocabulary has exactly one copy; and
every remaining lexical decision is declared in the grammar file rather than in C#.

### Tokenisation hazards, checked against the corpus

Three hypotheses were tested. Two were wrong.

- **`>>` closing nested generics — not present.** Zero occurrences. All 44 uses of `>>` are
  right-shift. Note the corpus does not settle whether the parser *accepts* `Foo<Bar<T>>`, so the
  grammar must decide something no test covers. That is a B finding, not a lexing problem.
- **`float4` versus `float4x4` — not a hazard.** Longest-match handles prefix-shared keywords.
- **Trailing-dot floats — all inside comments.** Every one of the 101 `N.` and `N.f` matches was
  prose in a doc comment (`CoC == 0.`, `range from -3 to 3.`, `[0..`). None reach the lexer.
- **Leading-dot floats — real.** `else if(MipLevel < +.5f)` and `(color*(6.2*color+.5))` are live
  code. Unambiguous, but the lexical rules must support the form. Hex literals (`0xFFFFFFFF`,
  about 25) and exponents (`1e-5`, 12) likewise.

Net: tokenisation risk is lower than first assumed, and the mitigations are load-bearing rather
than precautionary. Surprises are still expected during the grind; there is currently no concrete
example of one.

## Grammar file format

Roughly ISO-14977 minus anything that grows the engine without making the grammar clearer. Rules,
alternation, sequence, grouping, `?`, `*`, `+`, quoted literals, uppercase token classes,
`(* comments *)`. No semantic actions, no predicates, no inline code. A construct that needs more
than this is a signal the grammar is wrong, not that the notation is too weak.

```ebnf
(* lexical *)
%token IDENT      = /[A-Za-z_][A-Za-z0-9_]*/ ;
%token INT        = /[0-9]+/ ;

(* syntax *)
file        = using* namespace* ;
using       = 'using' qualified_name ';' ;
namespace   = 'namespace' qualified_name '{' decl* '}' ;
decl        = params_block | effect_block ;

params_block = 'params' IDENT '{' param* '}' ';' ;
param        = type IDENT ';' ;

effect_block = 'effect' IDENT '{' effect_stmt* '}' ';' ;
effect_stmt  = using_params | mixin_stmt | if_stmt ;
using_params = 'using' 'params' qualified_name ';' ;
mixin_stmt   = 'mixin' qualified_name ';' ;
if_stmt      = 'if' '(' expr ')' block ( 'else' block )? ;
```

## Gate semantics

**Correction to the original framing.** "A parser change that quietly widens the accepted language
fails the grammar check" is not true as stated. Widening the parser alone fails nothing: every
existing corpus file still parses and the grammar still accepts every one of them, so both
directions stay green. The gate bites only once a corpus file *uses* the new syntax. It binds new
usage, not new capability — real, but a lag rather than a tripwire.

Three checks:

1. **Corpus to parser.** Every `.sdsl` and `.sdfx` parses without errors. Exists as
   `ParsingTests.ParseFile`; extend its globs to the ~100 uncovered files.
2. **Corpus to grammar.** Every corpus file is accepted by the EBNF. New, and also how the grammar
   gets built: grind until green.
3. **Negative corpus to both.** A curated set that both parser and grammar must reject. New, and
   the only check that pins the grammar's upper bound — without it the grammar can drift
   arbitrarily wider than the language while 1 and 2 stay green forever.

Check 3 is what makes the gate bidirectional in the intended sense. Twenty or thirty files, one
construct each: a missing semicolon, an unclosed generic, `[numthreads]` in an illegal position, a
stage qualifier where none is legal. They double as executable documentation of what SDSL is not.

## Open questions

1. Does the three-check shape stand, and does the negative corpus belong in A or in a follow-up?
2. Diagnostics: what a rejection reports. Earley's furthest-progress item set gives "file X, line
   N, expected one of ...", mapped back through `CommentProcessedCode.Links`. Not yet designed.
3. Engine testing strategy beyond "toy grammars" — what the ambiguity-detection tests assert.
4. CI wiring and the performance budget. Runtime across 593 files must be measured before this
   goes into PR CI rather than assumed acceptable.
5. Whether extending the `ParsingTests` globs to Voxels and the samples belongs in A or ships
   first as a standalone fix, given it may surface existing failures unrelated to the grammar.
