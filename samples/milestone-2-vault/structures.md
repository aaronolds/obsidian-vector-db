# Code and tables

The model can embed technical syntax without sending it to a remote service.

```csharp
var vector = await embeddings.EmbedAsync(text, cancellationToken);
```

| Field | Value |
| --- | --- |
| Provider | onnx |
| Dimensions | 384 |
