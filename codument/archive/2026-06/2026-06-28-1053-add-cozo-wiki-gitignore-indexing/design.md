# Design

## Ignore Source

`RepositoryIndexer` reads only the root `.gitignore` file under `RepositoryIndexRequest.RepositoryPath`.

## Supported Pattern Semantics

MVP supports common `.gitignore` forms:

- blank lines and `#` comments
- directory rules ending with `/`
- root-relative rules starting with `/`
- wildcard `*` and `?`
- recursive wildcard `**`
- negation with `!`

Rules are evaluated in order, so later rules can override earlier rules.

## Traversal

The directory traversal checks ignored directories before pushing them to the stack. This prevents large folders such as `node_modules` from being recursively scanned.

Files are checked before extension filtering so ignored files are not considered index candidates.

## Configuration

`RepositoryIndexRequest` gains `UseGitIgnore = true`. Existing callers get the new behavior by default, while tests or special invocations can disable it.
