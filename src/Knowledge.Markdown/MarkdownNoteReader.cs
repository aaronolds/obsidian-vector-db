using System.Text;
using System.Text.RegularExpressions;
using Knowledge.Core;

namespace Knowledge.Markdown;

/// <summary>Reads eligible authoritative Markdown notes from one configured vault.</summary>
public sealed class MarkdownNoteReader(MarkdownIngestor ingestor, MarkdownIngestionOptions options)
    : IKnowledgeNoteReader
{
    private static readonly Regex DocumentIdPattern = new("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    public Task<KnowledgeNote?> GetNoteAsync(KnowledgeNoteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hasPath = !string.IsNullOrWhiteSpace(request.Path);
        var hasDocumentId = !string.IsNullOrWhiteSpace(request.DocumentId);
        if (hasPath == hasDocumentId)
            throw new ArgumentException("Specify exactly one of path or documentId.", nameof(request));

        string? canonicalPath = null;
        if (hasPath) canonicalPath = ValidatePath(request.Path!);
        if (hasDocumentId && !DocumentIdPattern.IsMatch(request.DocumentId!))
            throw new ArgumentException("Document ID must use the canonical sha256 format.", nameof(request));

        cancellationToken.ThrowIfCancellationRequested();
        var scan = ingestor.Discover(options, cancellationToken);
        if (!scan.IsComplete)
            throw new IOException("The configured vault could not be completely enumerated.");

        var matches = scan.Files.Where(file => hasPath
            ? string.Equals(file.RelativePath, canonicalPath, StringComparison.Ordinal)
            : string.Equals(KnowledgeIdentity.DocumentId(options.VaultId, file.RelativePath), request.DocumentId,
                StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) return Task.FromResult<KnowledgeNote?>(null);
        if (matches.Length != 1) throw new InvalidDataException("The requested note identity is ambiguous.");

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = ingestor.ReadSnapshot(matches[0]);
        var metadata = ingestor.ReadNoteMetadata(snapshot, options);
        if (metadata.OptedOut) return Task.FromResult<KnowledgeNote?>(null);

        var content = new UTF8Encoding(false, true).GetString(snapshot.Bytes);
        return Task.FromResult<KnowledgeNote?>(new KnowledgeNote(
            KnowledgeIdentity.DocumentId(options.VaultId, snapshot.File.RelativePath), snapshot.File.RelativePath,
            metadata.Title, content, snapshot.FileHash, snapshot.ModifiedUtc));
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains('\0') ||
            path.Any(char.IsControl) || path.StartsWith('/') || Path.IsPathRooted(path) ||
            path.Contains("://", StringComparison.Ordinal) ||
            (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'))
            throw new ArgumentException("Note path must be a canonical vault-relative Markdown path.", nameof(path));

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or "..") ||
            !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Note path must be a canonical vault-relative Markdown path.", nameof(path));
        return path;
    }
}
