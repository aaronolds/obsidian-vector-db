using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Knowledge.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Knowledge.Markdown;

/// <summary>A deterministic approximation for asset-free chunk inspection only; it does not enforce the model limit.</summary>
public sealed class ApproximateBertTokenCounter : ITokenCounter
{
    public int Count(string text, bool includeSpecialTokens = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return includeSpecialTokens ? 2 : 0;
        var count = Regex.Matches(text, @"[\p{L}\p{N}]+|[^\s\p{L}\p{N}]", RegexOptions.CultureInvariant).Count;
        return count + (includeSpecialTokens ? 2 : 0);
    }
}

public sealed record MarkdownIngestionOptions(
    string VaultId,
    string RootPath,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    string FrontmatterOptOutField,
    int TargetTokens,
    int SoftMaxTokens,
    int OverlapTokens,
    int ModelTokenLimit = 512);

public sealed record IngestionDiagnostic(string RelativePath, string Code, string Message);

public sealed record IngestionResult(
    IReadOnlyList<KnowledgeChunk> Chunks,
    IReadOnlyList<IngestionDiagnostic> Diagnostics,
    int DocumentsProcessed,
    int DocumentsSkipped);

public sealed record VaultFile(string FullPath, string RelativePath, string RootPath);

public sealed record VaultScanResult(
    IReadOnlyList<VaultFile> Files,
    IReadOnlyList<IngestionDiagnostic> Diagnostics,
    bool IsComplete);

public sealed record SourceSnapshot(VaultFile File, byte[] Bytes, DateTimeOffset ModifiedUtc)
{
    public string FileHash => KnowledgeIdentity.FileHash(Bytes);
}

public enum DocumentIngestionStatus { Indexed, OptedOut, Empty }

public sealed record DocumentIngestionResult(
    DocumentIngestionStatus Status,
    KnowledgeDocument Document,
    IReadOnlyList<KnowledgeChunk> Chunks);

public sealed record NoteMetadata(string Title, bool OptedOut);

public sealed class MarkdownIngestor
{
    private static readonly Regex WikiLinkRegex = new(@"\[\[([^\]|#]+?)(?:\|([^\]]+))?\]\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly ITokenCounter _tokens;

    public MarkdownIngestor(ITokenCounter? tokens = null) => _tokens = tokens ?? new ApproximateBertTokenCounter();

    public VaultScanResult Discover(MarkdownIngestionOptions options, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<IngestionDiagnostic>();
        var files = new List<VaultFile>();
        if (!Directory.Exists(options.RootPath))
        {
            diagnostics.Add(new IngestionDiagnostic("", "vault-missing", "The configured vault directory does not exist."));
            return new VaultScanResult(files, diagnostics, false);
        }

        var root = Path.GetFullPath(options.RootPath);
        var rootInfo = ResolveDirectory(new DirectoryInfo(root));
        root = Path.GetFullPath(rootInfo.FullName);
        var resolvedRoot = rootInfo;
        var pathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var visited = new HashSet<string>(pathComparer);
        var complete = true;
        try
        {
            Visit(rootInfo, "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new IngestionDiagnostic("", "scan-failed", "The vault could not be completely enumerated."));
            complete = false;
        }

        files.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        return new VaultScanResult(files, diagnostics, complete);

        void Visit(DirectoryInfo directory, string relativeDirectory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var physical = ResolveDirectory(directory);
            if (!IsInside(resolvedRoot, physical)) return;
            if (!visited.Add(Path.GetFullPath(physical.FullName))) return;

            FileSystemInfo[] children;
            try { children = physical.GetFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new IngestionDiagnostic(relativeDirectory, "scan-failed", "A vault directory could not be enumerated."));
                complete = false;
                return;
            }

            foreach (var child in children.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var childRelative = NormalizePath(Path.Combine(relativeDirectory, child.Name));
                if (child is DirectoryInfo childDirectory)
                {
                    Visit(childDirectory, childRelative);
                    continue;
                }

                if (!child.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = childRelative;
                if (!MatchesAny(relative, options.Include) || MatchesAny(relative, options.Exclude)) continue;
                // GetFileSystemInfos can return child paths through a different alias
                // (for example /var versus /private/var on macOS). Build the child
                // path from the already-resolved parent before resolving file links.
                var candidate = new FileInfo(Path.Combine(physical.FullName, child.Name));
                var resolved = candidate.ResolveLinkTarget(true) ?? candidate;
                if (!IsInside(resolvedRoot, resolved)) continue;
                files.Add(new VaultFile(resolved.FullName, relative, root));
            }
        }
    }

    public SourceSnapshot ReadSnapshot(VaultFile file)
    {
        var resolvedPath = ResolveContainedFilePath(file);
        var info = new FileInfo(resolvedPath);
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var bytes = File.ReadAllBytes(resolvedPath);
        return new SourceSnapshot(file, bytes, modified);
    }

    private static string ResolveContainedFilePath(VaultFile file)
    {
        var root = ResolveDirectory(new DirectoryInfo(Path.GetFullPath(file.RootPath)));
        var relativeParts = file.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (relativeParts.Length == 0 || relativeParts.Any(part => part is "." or ".." ||
            part.Contains(Path.DirectorySeparatorChar) || part.Contains(Path.AltDirectorySeparatorChar)))
            throw new UnauthorizedAccessException("The note path is not a canonical vault-relative path.");

        var currentPath = Path.GetFullPath(root.FullName);
        for (var index = 0; index < relativeParts.Length; index++)
        {
            currentPath = Path.Combine(currentPath, relativeParts[index]);
            FileSystemInfo component = index == relativeParts.Length - 1
                ? new FileInfo(currentPath)
                : new DirectoryInfo(currentPath);
            var resolved = component.ResolveLinkTarget(true) ?? component;
            if (!IsInside(root, resolved))
                throw new UnauthorizedAccessException("The resolved note path is outside the configured vault.");
            currentPath = Path.GetFullPath(resolved.FullName);
        }

        return currentPath;
    }

    public DocumentIngestionResult IngestDocument(SourceSnapshot snapshot, MarkdownIngestionOptions options)
    {
        var text = new UTF8Encoding(false, true).GetString(snapshot.Bytes);
        var parsed = Parse(text, snapshot.File.RelativePath, options.FrontmatterOptOutField);
        var document = CreateDocument(snapshot, options, parsed);
        if (parsed.OptOut)
            return new DocumentIngestionResult(DocumentIngestionStatus.OptedOut, document, []);
        var chunks = Chunk(document, parsed, options);
        return new DocumentIngestionResult(chunks.Count == 0 ? DocumentIngestionStatus.Empty : DocumentIngestionStatus.Indexed,
            document, chunks);
    }

    public (KnowledgeDocument Document, bool OptedOut) ReadMetadata(SourceSnapshot snapshot,
        MarkdownIngestionOptions options)
    {
        var text = new UTF8Encoding(false, true).GetString(snapshot.Bytes);
        var parsed = Parse(text, snapshot.File.RelativePath, options.FrontmatterOptOutField);
        return (CreateDocument(snapshot, options, parsed), parsed.OptOut);
    }

    public NoteMetadata ReadNoteMetadata(SourceSnapshot snapshot, MarkdownIngestionOptions options)
    {
        var text = new UTF8Encoding(false, true).GetString(snapshot.Bytes);
        var (lines, start, frontmatter) = ReadFrontmatter(text);
        var title = Values(frontmatter, "title").FirstOrDefault() ?? FindFirstH1(lines, start) ??
            Path.GetFileNameWithoutExtension(snapshot.File.RelativePath);
        var optedOut = frontmatter.TryGetValue(options.FrontmatterOptOutField, out var value) &&
            string.Equals(value?.ToString(), "false", StringComparison.OrdinalIgnoreCase);
        return new NoteMetadata(title, optedOut);
    }

    private static KnowledgeDocument CreateDocument(SourceSnapshot snapshot, MarkdownIngestionOptions options, ParsedNote parsed)
    {
        var documentId = KnowledgeIdentity.DocumentId(options.VaultId, snapshot.File.RelativePath);
        return new KnowledgeDocument(options.VaultId, documentId, snapshot.File.RelativePath, parsed.Title,
            parsed.Tags, parsed.Aliases, parsed.Links, snapshot.FileHash, snapshot.ModifiedUtc);
    }

    public IngestionResult ReadVault(MarkdownIngestionOptions options, CancellationToken cancellationToken = default)
    {
        var scan = Discover(options, cancellationToken);
        var chunks = new List<KnowledgeChunk>();
        var diagnostics = scan.Diagnostics.ToList();
        var processed = 0;
        var skipped = 0;
        foreach (var file in scan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = IngestDocument(ReadSnapshot(file), options);
                if (document.Status == DocumentIngestionStatus.OptedOut) skipped++;
                else
                {
                    processed++;
                    chunks.AddRange(document.Chunks);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or YamlException or FormatException)
            {
                diagnostics.Add(new IngestionDiagnostic(file.RelativePath, "parse-failed", "The note could not be parsed or read."));
            }
        }
        return new IngestionResult(chunks, diagnostics, processed, skipped);
    }

    private static DirectoryInfo ResolveDirectory(DirectoryInfo directory) =>
        (DirectoryInfo?)directory.ResolveLinkTarget(true) ?? directory;

    private static bool IsInside(DirectoryInfo root, FileSystemInfo path)
    {
        var fullRoot = Path.GetFullPath(root.FullName).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path.FullName);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return true;
        return full.StartsWith(fullRoot, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    private static bool MatchesAny(string path, IReadOnlyList<string> patterns) => patterns.Any(pattern => GlobMatch(path, pattern));

    private static bool GlobMatch(string path, string pattern)
    {
        var p = NormalizePath(pattern);
        var regex = "^" + Regex.Escape(p).Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*", "[^/]*") + "$";
        return Regex.IsMatch(path, regex, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized.TrimStart('/');
    }

    private sealed record ParsedNote(string Title, IReadOnlyList<string> Tags, IReadOnlyList<string> Aliases, IReadOnlyList<WikiLink> Links, bool OptOut, IReadOnlyList<Section> Sections);
    private sealed record Section(IReadOnlyList<string> HeadingPath, IReadOnlyList<Block> Blocks, string Source);
    private sealed record Block(string Markdown, string Normalized, BlockKind Kind);
    private enum BlockKind { Prose, Code, List, Table, Quote }

    private static ParsedNote Parse(string text, string relativePath, string optOutField)
    {
        var (lines, start, frontmatter) = ReadFrontmatter(text);
        var tags = Values(frontmatter, "tags");
        var aliases = Values(frontmatter, "aliases");
        var optOut = frontmatter.TryGetValue(optOutField, out var value) && string.Equals(value?.ToString(), "false", StringComparison.OrdinalIgnoreCase);
        var title = Values(frontmatter, "title").FirstOrDefault() ?? Path.GetFileNameWithoutExtension(relativePath);
        var sections = ParseSections(lines, start, out var firstH1, out var links);
        if (!frontmatter.ContainsKey("title") && !string.IsNullOrWhiteSpace(firstH1)) title = firstH1!;
        return new ParsedNote(title, tags, aliases, links, optOut, sections);
    }

    private static (string[] Lines, int BodyStart, Dictionary<string, object?> Frontmatter) ReadFrontmatter(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var frontmatter = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var start = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
            if (end < 0) throw new FormatException("Unterminated frontmatter");
            ParseFrontmatterYaml(string.Join('\n', lines.Skip(1).Take(end - 1)), frontmatter);
            start = end + 1;
        }
        return (lines, start, frontmatter);
    }

    private static void ParseFrontmatterYaml(string yaml, Dictionary<string, object?> target)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode map) throw new FormatException("Frontmatter must be a mapping");
        foreach (var item in map.Children)
        {
            if (item.Key is not YamlScalarNode key) throw new FormatException("Frontmatter key must be scalar");
            target[key.Value ?? ""] = item.Value switch
            {
                YamlSequenceNode seq => seq.Children.OfType<YamlScalarNode>().Select(x => x.Value ?? "").ToArray(),
                YamlScalarNode scalar => scalar.Value,
                _ => throw new FormatException("Unsupported frontmatter value")
            };
        }
    }

    private static IReadOnlyList<string> Values(Dictionary<string, object?> map, string key) => map.TryGetValue(key, out var value) switch
    {
        true when value is string s => [s.Trim()],
        true when value is string[] values => values.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        _ => []
    };

    private static string? FindFirstH1(string[] lines, int start)
    {
        var inFence = false;
        for (var index = start; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;
            if (TryHeading(line, out var level, out var heading) && level == 1) return heading;
            if (index + 1 < lines.Length && !string.IsNullOrWhiteSpace(line) &&
                Regex.IsMatch(lines[index + 1], @"^\s*=+\s*$")) return line.Trim();
        }
        return null;
    }

    private static IReadOnlyList<Section> ParseSections(string[] lines, int start, out string? firstH1, out IReadOnlyList<WikiLink> links)
    {
        firstH1 = null;
        var allLinks = new List<WikiLink>();
        var sections = new List<Section>();
        var headingStack = new List<(int Level, string Text)>();
        var currentPath = new List<string>();
        var blocks = new List<Block>();
        var raw = new List<string>();
        void FlushSection()
        {
            if (raw.Count == 0) return;
            sections.Add(new Section(currentPath.ToArray(), blocks.ToArray(), string.Join('\n', raw).Trim('\n')));
            blocks.Clear(); raw.Clear();
        }
        var i = start;
        while (i < lines.Length)
        {
            var line = lines[i];
            var isCodeFence = line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal);
            var setext = !isCodeFence && i + 1 < lines.Length && !string.IsNullOrWhiteSpace(line) && Regex.IsMatch(lines[i + 1], @"^\s*(?:=+|-+)\s*$");
            if (TryHeading(line, out var level, out var heading) || setext)
            {
                if (setext)
                {
                    level = lines[i + 1].TrimStart().StartsWith('=') ? 1 : 2;
                    heading = line.Trim();
                    i++;
                }
                FlushSection();
                while (headingStack.Count > 0 && headingStack[^1].Level >= level) headingStack.RemoveAt(headingStack.Count - 1);
                headingStack.Add((level, heading));
                currentPath = headingStack.Select(x => x.Text).ToList();
                if (level == 1 && firstH1 is null) firstH1 = heading;
                _ = Normalize(heading, BlockKind.Prose, allLinks);
                raw.Add(line);
                if (setext) raw.Add(lines[i]);
                i++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) { if (raw.Count > 0) raw.Add(line); i++; continue; }
            var kind = BlockKind.Prose;
            var blockLines = new List<string> { line };
            if (isCodeFence)
            {
                kind = BlockKind.Code; var fence = line.TrimStart()[..3]; var closed = false; i++;
                while (i < lines.Length) { blockLines.Add(lines[i]); if (lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal)) { i++; closed = true; break; } i++; }
                if (!closed) throw new FormatException("Unterminated fenced code block");
            }
            else if (line.TrimStart().StartsWith('>'))
            {
                kind = BlockKind.Quote; i++; while (i < lines.Length && (lines[i].TrimStart().StartsWith('>') || string.IsNullOrWhiteSpace(lines[i]))) { blockLines.Add(lines[i]); i++; }
            }
            else if (Regex.IsMatch(line, @"^\s*(?:[-*+] |\d+[.)] )"))
            {
                kind = BlockKind.List; i++; while (i < lines.Length && (Regex.IsMatch(lines[i], @"^\s+(?:[-*+] |\d+[.)] )") || string.IsNullOrWhiteSpace(lines[i]))) { blockLines.Add(lines[i]); i++; }
            }
            else if (line.Contains('|') && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1], @"^\s*\|?\s*:?-{3,}"))
            {
                kind = BlockKind.Table; i++; while (i < lines.Length && (lines[i].Contains('|') || string.IsNullOrWhiteSpace(lines[i]))) { blockLines.Add(lines[i]); i++; }
            }
            else { i++; while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !TryHeading(lines[i], out _, out _)) { blockLines.Add(lines[i]); i++; } }
            var markdown = string.Join('\n', blockLines).Trim();
            raw.Add(markdown);
            var normalized = Normalize(markdown, kind, allLinks);
            blocks.Add(new Block(markdown, normalized, kind));
        }
        FlushSection();
        links = allLinks.Distinct().ToArray();
        return sections;
    }

    private static bool TryHeading(string line, out int level, out string text)
    {
        var match = Regex.Match(line, @"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$");
        level = match.Success ? match.Groups[1].Length : 0;
        text = match.Success ? match.Groups[2].Value.Trim() : "";
        return match.Success;
    }

    private static string Normalize(string markdown, BlockKind kind, List<WikiLink> links)
    {
        var value = markdown;
        if (kind == BlockKind.Code) return value;
        value = Regex.Replace(value, @"<[^>]+>", "");
        value = WikiLinkRegex.Replace(value, match =>
        {
            var target = match.Groups[1].Value.Trim(); var display = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;
            links.Add(new WikiLink(target, display)); return display ?? target;
        });
        value = Regex.Replace(value, @"!?(?:\[([^\]]+)\]\([^)]*\))", "$1");
        value = Regex.Replace(value, @"[`*_~]", "");
        value = Regex.Replace(value, @"^\s*(?:[-*+] |\d+[.)] )", "", RegexOptions.Multiline);
        value = Regex.Replace(value, @"^\s*>\s?", "", RegexOptions.Multiline);
        if (kind == BlockKind.Table) value = value.Replace('|', ' ');
        return Regex.Replace(value, @"[ \t]+", " ").Trim();
    }

    private IReadOnlyList<KnowledgeChunk> Chunk(KnowledgeDocument document, ParsedNote parsed, MarkdownIngestionOptions options)
    {
        var output = new List<KnowledgeChunk>(); var ordinal = 0;
        foreach (var section in parsed.Sections)
        {
            if (section.Blocks.Count == 0) continue;
            if (_tokens.Count(Format(document.Title, section.HeadingPath, document.Tags, []), true) >= options.ModelTokenLimit)
                throw new FormatException("Title, heading, and tag context exceeds the model token window.");
            var pending = new List<Block>();
            foreach (var block in section.Blocks)
            {
                var candidate = pending.Append(block).ToArray();
                if (pending.Count > 0 && Count(document.Title, section.HeadingPath, document.Tags, candidate) > options.SoftMaxTokens)
                {
                    output.Add(Create(document, section.HeadingPath, pending, ordinal++, options));
                    pending = [];
                }
                if (_tokens.Count(Format(document.Title, section.HeadingPath, document.Tags, [block]), true) > options.ModelTokenLimit)
                {
                    foreach (var split in SplitBlock(block, document.Title, section.HeadingPath, document.Tags, options))
                        output.Add(Create(document, section.HeadingPath, [split], ordinal++, options));
                }
                else pending.Add(block);
            }
            if (pending.Count > 0) output.Add(Create(document, section.HeadingPath, pending, ordinal++, options));
        }
        return output;
    }

    private int Count(string title, IReadOnlyList<string> path, IReadOnlyList<string> tags, IReadOnlyList<Block> blocks) => _tokens.Count(Format(title, path, tags, blocks), true);
    private static string Format(string title, IReadOnlyList<string> path, IReadOnlyList<string> tags, IReadOnlyList<Block> blocks) =>
        EmbeddingTextFormatter.Format(title, path, tags, string.Join("\n\n", blocks.Select(x => x.Normalized)));

    private KnowledgeChunk Create(KnowledgeDocument document, IReadOnlyList<string> path, IReadOnlyList<Block> blocks, int ordinal, MarkdownIngestionOptions options)
    {
        var markdown = string.Join("\n\n", blocks.Select(x => x.Markdown));
        var normalized = string.Join("\n\n", blocks.Select(x => x.Normalized));
        return new KnowledgeChunk(document.VaultId, document.DocumentId, KnowledgeIdentity.ChunkId(document.DocumentId, path, ordinal), document.RelativePath,
            document.Title, path, document.Tags, document.Links, ordinal, markdown, normalized, document.FileHash,
            KnowledgeIdentity.ChunkHash(markdown, document.Title, path, document.Tags), document.ModifiedUtc);
    }

    private IEnumerable<Block> SplitBlock(Block block, string title, IReadOnlyList<string> path, IReadOnlyList<string> tags, MarkdownIngestionOptions options)
    {
        if (block.Kind == BlockKind.Code)
        {
            var lines = block.Markdown.Split('\n');
            var opening = lines[0].TrimStart();
            var closing = lines.Length > 1 && lines[^1].TrimStart().StartsWith(opening[..Math.Min(3, opening.Length)], StringComparison.Ordinal)
                ? lines[^1] : "```";
            var body = lines.Skip(1).Take(Math.Max(0, lines.Length - 2)).ToArray();
            if (body.Length == 0) { yield return new Block(block.Markdown, block.Normalized, block.Kind); yield break; }
            var part = new List<string>();
            foreach (var line in body)
            {
                var candidate = string.Join('\n', new[] { opening }.Concat(part).Append(line).Append(closing));
                if (part.Count > 0 && _tokens.Count(Format(title, path, tags, [new Block(candidate, candidate, BlockKind.Code)]), true) > options.SoftMaxTokens)
                {
                    var value = string.Join('\n', new[] { opening }.Concat(part).Append(closing));
                    yield return new Block(value, value, BlockKind.Code);
                    part = [];
                }
                part.Add(line);
            }
            if (part.Count > 0)
            {
                var value = string.Join('\n', new[] { opening }.Concat(part).Append(closing));
                yield return new Block(value, value, BlockKind.Code);
            }
            yield break;
        }
        if (block.Kind == BlockKind.Table)
        {
            var rows = block.Markdown.Split('\n');
            var header = rows.Take(Math.Min(2, rows.Length)).ToArray();
            var part = new List<string>();
            foreach (var row in rows.Skip(2))
            {
                var candidate = string.Join('\n', header.Concat(part).Append(row));
                if (part.Count > 0 && _tokens.Count(Format(title, path, tags, [new Block(candidate, Normalize(candidate, BlockKind.Table, new List<WikiLink>()), BlockKind.Table)]), true) > options.SoftMaxTokens)
                {
                    var value = string.Join('\n', header.Concat(part));
                    yield return new Block(value, Normalize(value, BlockKind.Table, new List<WikiLink>()), BlockKind.Table);
                    part = [];
                }
                part.Add(row);
            }
            if (part.Count > 0)
            {
                var value = string.Join('\n', header.Concat(part));
                yield return new Block(value, Normalize(value, BlockKind.Table, new List<WikiLink>()), BlockKind.Table);
            }
            yield break;
        }
        var words = block.Markdown.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var current = new List<string>();
        foreach (var word in words)
        {
            var candidate = string.Join(' ', current.Append(word));
            if (current.Count > 0 && _tokens.Count(Format(title, path, tags, [new Block(candidate, Normalize(candidate, block.Kind, new List<WikiLink>()), block.Kind)]), true) > options.SoftMaxTokens)
            {
                var part = string.Join(' ', current); yield return new Block(part, Normalize(part, block.Kind, new List<WikiLink>()), block.Kind); current = [];
            }
            if (current.Count == 0 && _tokens.Count(Format(title, path, tags, [new Block(word, word, block.Kind)]), true) > options.ModelTokenLimit)
            {
                var piece = new StringBuilder();
                foreach (var character in word)
                {
                    var next = piece.ToString() + character;
                    if (piece.Length > 0 && _tokens.Count(Format(title, path, tags, [new Block(next, next, block.Kind)]), true) > options.ModelTokenLimit)
                    {
                        var value = piece.ToString(); yield return new Block(value, value, block.Kind); piece.Clear();
                    }
                    piece.Append(character);
                }
                if (piece.Length > 0) { var value = piece.ToString(); yield return new Block(value, value, block.Kind); }
                continue;
            }
            current.Add(word);
        }
        if (current.Count > 0) { var part = string.Join(' ', current); yield return new Block(part, Normalize(part, block.Kind, new List<WikiLink>()), block.Kind); }
    }
}
