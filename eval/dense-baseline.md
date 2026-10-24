# Dense retrieval baseline

Status: reviewed by the vault owner on 2026-10-24; Milestone 6 acceptance is
complete.

## Run

- Benchmark: private `retrieval-benchmark-v1.json`, 24 questions.
- Benchmark SHA-256: `fb5b338df3af94d8f1c2bcfdac55029cc2f31bdea61450789e5d152f83c908a5`.
- Raw private report: `eval/local/runs/baseline-v1/dense-baseline.json` (ignored).
- Evaluated revision: `a456098b3ca006d48f2102813948d0d98107de10`.
- Pre-measurement worktree patch SHA-256: `bf436be6fd2d4bcf1aa534b93853bb8a64a083d7ccc622f6b64661f865e92b34`.
- Run time: 2026-10-24 20:48:29 UTC; application `0.1.0`.
- Source snapshot SHA-256: `0f26cf56b523526fadca9c7a0436f4c2f6c96e96c5e24bf01e21ab7cf5d796e5`.
- Source snapshot and indexed metadata fingerprints matched before and after.
- Index: 404 documents / 3,707 chunks. The configured parent root includes the
  nested `Personal/` vault; its 19 documents / 69 chunks were indexed too. The
  approved v1 expected paths were preserved unchanged.
- Retrieval: dense, K=5, 384-dimensional cosine; payload schema 1, embedding
  schema 2; profile `sha256:b88b27b97fa2c1233119117b0e8ee304adf1c3f15803deb011774e0bd0e1ed54`.
- Model: BAAI/bge-small-en-v1.5, revision
  `5c38ec7c405ec4b44b94cc5a9bb96e735b38267a`; model SHA-256
  `828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35`.
- Runtime: .NET `10.0.13`, ONNX Runtime `1.31.0.0`, Qdrant `1.19.2`.

## Results

Overall Hit@5 is **22/24 (91.7%)**, meeting the advisory 85% target.

| Category | Hit@5 |
|---|---:|
| acronym | 3/3 (100%) |
| architecture-decision | 2/3 (66.7%) |
| conceptual | 4/4 (100%) |
| difficult | 2/2 (100%) |
| exact-identifier | 4/4 (100%) |
| implementation-detail | 3/3 (100%) |
| multiple-plausible-notes | 2/2 (100%) |
| paraphrased | 2/3 (66.7%) |

## Relevance review

The private review artifact covers both misses and one successful result from each
of the eight categories. The two misses are safe IDs `context-002` and
`harness-002`. Their expected sources support their questions, while the top five
returned related but less specific material. The sampled successful passages
answer their questions and retain note path, section, and chunk provenance.
Repeated chunks from a relevant note sometimes use several of the five result
slots, as required by the scoring contract. No evidence pointed to invalid labels,
missing indexed sources, or changed source/index state. The exact-identifier and
acronym categories combined for 7/7; this is a small sample with no observed
identifier failure.

Prepared review: `eval/local/review-v1.md`. It contains private question text and
retrieved Markdown excerpts and remains ignored. The vault owner confirmed that the two misses are genuine ranking misses and
that the sampled successful passages support the relevance summary on
2026-10-24.

Recommendation: retain this dense configuration as the measured baseline. In a
later retrieval milestone, compare a sparse/hybrid candidate against this same
frozen benchmark and review whether it improves the two misses without harming
identifier and code-oriented cases. No retrieval changes are part of Milestone 6.
