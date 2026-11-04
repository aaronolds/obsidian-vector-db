# Knowledge.Markdown

Milestone 1 provides local vault discovery, frontmatter and Markdown structure
extraction, Obsidian wikilink handling, normalization, and deterministic
structure-aware chunks. It has no Qdrant or embedding inference dependency.

Milestone 8 enforces include/exclude eligibility on both link aliases and fully
resolved targets, resolves linked target ancestors, bounds link chains, and
rechecks vault containment before reads. Checks are conservative about path
casing and do not provide atomic protection against concurrent filesystem swaps.
