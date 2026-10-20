# Retrieval Evaluation Plan

## 1. Purpose

The system must be able to answer a more useful question than "does vector search return something?"

The benchmark measures whether retrieval returns the knowledge a user actually expects.

## 2. Initial benchmark size

Create 20 to 50 representative queries from the real vault.

The benchmark should include:

- conceptual questions
- paraphrased questions
- acronyms
- exact identifiers
- architecture decision questions
- implementation-detail questions
- queries with multiple plausible notes
- intentionally difficult queries

## 3. Benchmark format

Recommended `eval/retrieval-benchmark.json`:

```json
[
  {
    "id": "auth-001",
    "query": "How do services authenticate to downstream APIs?",
    "expectedPaths": [
      "Architecture/Azure Authentication.md"
    ],
    "expectedTerms": [
      "OBO",
      "downstream API"
    ],
    "category": "semantic"
  },
  {
    "id": "afd-001",
    "query": "X-Azure-FDID",
    "expectedPaths": [
      "Architecture/Azure Front Door.md"
    ],
    "expectedTerms": [
      "X-Azure-FDID"
    ],
    "category": "exact-identifier"
  }
]
```

`expectedTerms` are diagnostic, not mandatory scoring criteria.

## 4. Primary POC metric

### Hit@5

A query passes if at least one expected document appears in the top five results.

```text
Hit@5 = passing queries / total queries
```

Recommended initial target:

```text
>= 85%
```

This is a baseline target, not a production SLA.

## 5. Secondary metrics

After the POC:

### MRR

Measures how early the first expected result appears.

### Recall@K

Useful when multiple documents are expected.

### nDCG

Useful once graded relevance is introduced.

Do not add metric complexity before the benchmark itself is trustworthy.

## 6. Category reporting

Report results by category.

Example:

| Category | Queries | Hit@5 |
|---|---:|---:|
| Semantic | 12 | 92% |
| Exact identifier | 6 | 67% |
| Architecture decision | 8 | 88% |
| Acronym | 4 | 75% |

This is important because dense search may perform well semantically while underperforming on exact identifiers.

## 7. Dense baseline

The first benchmark run must use:

- BGE-small-en-v1.5
- dense retrieval only
- no sparse search
- no reranking
- no graph expansion

Record:

- model identity
- chunking configuration
- Qdrant collection schema version
- benchmark version
- result metrics

## 8. Hybrid evaluation

Hybrid retrieval should be introduced only after the dense baseline is recorded.

Then rerun the exact same benchmark.

Compare:

```text
Dense baseline
vs.
Dense + sparse + RRF
```

Expected hypothesis:

- semantic queries remain comparable
- exact identifiers improve
- acronyms improve
- overall Hit@5 improves

If hybrid does not materially improve the benchmark, keep the simpler dense architecture.

## 9. Regression policy

Any change to the following should trigger benchmark execution:

- embedding model
- chunking logic
- normalization logic
- retrieval algorithm
- sparse model
- RRF parameters
- reranker
- graph expansion

A material regression should block adoption until understood.

## 10. Human review

Metrics are necessary but insufficient.

For a sample of benchmark queries, manually inspect:

- whether the returned passage actually answers the question
- whether the heading context is sufficient
- whether chunks are too broad or too narrow
- whether duplicate chunks dominate the results
- whether code-heavy notes retrieve correctly

## 11. POC report

Create a short result report with:

- benchmark version
- index configuration
- overall Hit@5
- category results
- notable failures
- recommended next retrieval improvement

The POC should not hide failure cases. They are input to the next retrieval iteration.
