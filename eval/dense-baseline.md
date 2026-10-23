# Dense retrieval baseline

Status: pending; no benchmark run or Hit@5 measurement is recorded.

This checkout does not include the ignored pinned ONNX model weights, and its
checked-in configuration points at the repository `docs/` directory as a sample
vault. No verified user-vault benchmark or human relevance labels were supplied.
Recording metrics from a synthetic fixture or presenting the sample docs as the
user's vault would not meet Milestone 6's real-vault acceptance criteria.

After configuring a real vault, installing the pinned local model, indexing it,
and authoring at least 20 verified questions, run the command in
[eval/README.md](README.md). Review every miss and representative successes, then
replace this status with the benchmark hash, source snapshot, model/index
configuration, overall and category Hit@5 counts, target status, reviewed IDs,
failure patterns, and the recommended next retrieval experiment. Keep private
query text and detailed per-query reports under ignored `eval/local/`.
