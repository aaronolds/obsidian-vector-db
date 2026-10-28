using Knowledge.Core;
using Knowledge.Markdown;
using Xunit;

namespace Knowledge.Mcp.Tests;

public sealed class MarkdownNoteReaderTests
{
    [Fact]
    public async Task Reads_current_markdown_by_canonical_path_and_document_id()
    {
        using var vault = new TemporaryVault();
        const string markdown = "---\r\ntitle: Current title\r\ntags: [one]\r\nai-index: true\r\n---\r\n# Heading\r\nCurrent body.\r\n";
        vault.Write("Folder/Note.md", markdown);
        var reader = vault.Reader();

        var byPath = await reader.GetNoteAsync(new KnowledgeNoteRequest(Path: "Folder/Note.md"));
        Assert.NotNull(byPath);
        Assert.Equal("Folder/Note.md", byPath.Path);
        Assert.Equal("Current title", byPath.Title);
        Assert.Equal(markdown, byPath.ContentMarkdown);
        Assert.Equal(KnowledgeIdentity.DocumentId("test-vault", "Folder/Note.md"), byPath.DocumentId);
        Assert.Equal(KnowledgeIdentity.FileHash(File.ReadAllBytes(vault.FilePath("Folder/Note.md"))), byPath.FileHash);

        vault.Write("Folder/Note.md", "---\ntitle: Updated title\n---\nLatest body.");
        var byId = await reader.GetNoteAsync(new KnowledgeNoteRequest(DocumentId: byPath.DocumentId));
        Assert.NotNull(byId);
        Assert.Equal("Updated title", byId.Title);
        Assert.Contains("Latest body.", byId.ContentMarkdown);
    }

    [Fact]
    public async Task Missing_excluded_opted_out_and_deleted_notes_are_not_found()
    {
        using var vault = new TemporaryVault();
        vault.Write("Visible.md", "# Visible\nBody.");
        vault.Write("Private.md", "---\nai-index: false\n---\nPrivate body.");
        vault.Write("Templates/Template.md", "Template body.");
        var reader = vault.Reader();

        Assert.Null(await reader.GetNoteAsync(new KnowledgeNoteRequest(Path: "Missing.md")));
        Assert.Null(await reader.GetNoteAsync(new KnowledgeNoteRequest(Path: "Private.md")));
        Assert.Null(await reader.GetNoteAsync(new KnowledgeNoteRequest(Path: "Templates/Template.md")));
        var id = KnowledgeIdentity.DocumentId("test-vault", "Visible.md");
        Assert.NotNull(await reader.GetNoteAsync(new KnowledgeNoteRequest(DocumentId: id)));
        File.Delete(vault.FilePath("Visible.md"));
        Assert.Null(await reader.GetNoteAsync(new KnowledgeNoteRequest(DocumentId: id)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Note.md", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("../outside.md", null)]
    [InlineData("..\\outside.md", null)]
    [InlineData("/absolute.md", null)]
    [InlineData("C:/absolute.md", null)]
    [InlineData("Folder//Note.md", null)]
    [InlineData("Folder/./Note.md", null)]
    [InlineData("Note.md?download=1", null)]
    public async Task Rejects_invalid_selectors_and_noncanonical_paths(string? path, string? documentId)
    {
        using var vault = new TemporaryVault();
        var reader = vault.Reader();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.GetNoteAsync(new KnowledgeNoteRequest(path, documentId)));
    }

    [Fact]
    public async Task External_file_links_are_not_available_through_the_reader()
    {
        using var vault = new TemporaryVault();
        var outside = Path.Combine(Path.GetTempPath(), $"knowledge-outside-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(outside, "Outside vault.");
        try
        {
            try { File.CreateSymbolicLink(vault.FilePath("Escaped.md"), outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            Assert.Null(await vault.Reader().GetNoteAsync(new KnowledgeNoteRequest(Path: "Escaped.md")));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task Returns_original_body_even_when_ingestion_syntax_is_malformed()
    {
        using var vault = new TemporaryVault();
        const string markdown = "# Recoverable note\n```csharp\nunterminated body";
        vault.Write("Malformed.md", markdown);

        var note = await vault.Reader().GetNoteAsync(new KnowledgeNoteRequest(Path: "Malformed.md"));

        Assert.NotNull(note);
        Assert.Equal("Recoverable note", note.Title);
        Assert.Equal(markdown, note.ContentMarkdown);
    }

    [Fact]
    public async Task Empty_notes_are_valid_and_noncanonical_case_does_not_resolve()
    {
        using var vault = new TemporaryVault();
        vault.Write("Empty.md", string.Empty);
        vault.Write("MixedCase.md", "Current body.");
        var reader = vault.Reader();

        var empty = await reader.GetNoteAsync(new KnowledgeNoteRequest(Path: "Empty.md"));

        Assert.NotNull(empty);
        Assert.Equal(string.Empty, empty.ContentMarkdown);
        Assert.Null(await reader.GetNoteAsync(new KnowledgeNoteRequest(Path: "mixedcase.md")));
    }

    [Fact]
    public async Task Invalid_utf8_source_fails_instead_of_returning_replacement_text()
    {
        using var vault = new TemporaryVault();
        Directory.CreateDirectory(vault.Path);
        await File.WriteAllBytesAsync(vault.FilePath("Invalid.md"), [0xC3, 0x28]);

        await Assert.ThrowsAsync<System.Text.DecoderFallbackException>(() =>
            vault.Reader().GetNoteAsync(new KnowledgeNoteRequest(Path: "Invalid.md")));
    }

    [Fact]
    public async Task Internal_file_symlinks_remain_readable()
    {
        using var vault = new TemporaryVault();
        vault.Write("Target.md", "Inside the vault.");
        try { File.CreateSymbolicLink(vault.FilePath("Alias.md"), vault.FilePath("Target.md")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var note = await vault.Reader().GetNoteAsync(new KnowledgeNoteRequest(Path: "Alias.md"));

        Assert.NotNull(note);
        Assert.Equal("Alias.md", note.Path);
        Assert.Equal("Inside the vault.", note.ContentMarkdown);
    }

    [Fact]
    public void Read_snapshot_rechecks_every_ancestor_for_vault_containment()
    {
        using var vault = new TemporaryVault();
        var outside = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"knowledge-mcp-ancestor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(System.IO.Path.Combine(outside, "Leak.md"), "Outside content.");
        try
        {
            try { Directory.CreateSymbolicLink(System.IO.Path.Combine(vault.Path, "External"), outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var file = new VaultFile(System.IO.Path.Combine(vault.Path, "External", "Leak.md"),
                "External/Leak.md", vault.Path);
            Assert.Throws<UnauthorizedAccessException>(() => new MarkdownIngestor().ReadSnapshot(file));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    private sealed class TemporaryVault : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"knowledge-mcp-{Guid.NewGuid():N}");

        public TemporaryVault() => Directory.CreateDirectory(Path);

        public string FilePath(string relativePath) => System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Write(string relativePath, string content)
        {
            var path = FilePath(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public IKnowledgeNoteReader Reader() => new MarkdownNoteReader(new MarkdownIngestor(),
            new MarkdownIngestionOptions("test-vault", Path, ["**/*.md"], ["Templates/**"], "ai-index", 300, 400, 40));

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
