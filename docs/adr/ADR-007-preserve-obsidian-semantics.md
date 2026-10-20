# ADR-007: Preserve Obsidian Semantics During Indexing

## Status

Accepted

## Context

Obsidian notes contain useful structure beyond plain text.

## Decision

Preserve at minimum:

- title
- heading hierarchy
- frontmatter tags
- aliases
- wikilink targets/display text
- original Markdown

Wikilinks are stored but not traversed during V1 retrieval.

## Consequences

- The index retains enough structure for later graph-assisted retrieval.
- No graph database is needed initially.
