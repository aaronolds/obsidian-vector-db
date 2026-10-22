using Knowledge.Core;
using Knowledge.Markdown;

namespace Knowledge.Indexer.Tests;

public sealed class IngestionTests
{
    [Fact]
    public void Parses_frontmatter_headings_tags_aliases_and_wikilinks()
    {
        using var vault = new TemporaryVault();
        vault.Write("Notes/Example.md", "---\ntitle: Custom title\ntags: [architecture, api]\naliases:\n  - Example note\n---\n# Top\n## Child\nUses [[Target|friendly name]].\n");
        var result = Read(vault);
        var chunk = Assert.Single(result.Chunks);
        Assert.Equal("Custom title", chunk.Title);
        Assert.Equal(["Top", "Child"], chunk.HeadingPath);
        Assert.Equal(["architecture", "api"], chunk.Tags);
        Assert.Contains(new WikiLink("Target", "friendly name"), chunk.Links);
        Assert.Contains("Uses [[Target|friendly name]]", chunk.ContentMarkdown);
        Assert.Contains("Uses friendly name", chunk.ContentNormalized);
    }

    [Fact]
    public void Opt_out_and_excludes_are_honored()
    {
        using var vault = new TemporaryVault();
        vault.Write("Included.md", "# Included\nText.");
        vault.Write("Templates/Excluded.md", "# Excluded\nText.");
        vault.Write("Opted.md", "---\nai-index: false\n---\n# Opted\nText.");
        var result = Read(vault);
        Assert.Single(result.Chunks);
        Assert.Equal("Included.md", result.Chunks[0].RelativePath);
        Assert.Equal(1, result.DocumentsSkipped);
    }

    [Fact]
    public void Code_does_not_create_headings_or_wikilinks()
    {
        using var vault = new TemporaryVault();
        vault.Write("Code.md", "# Real\n```md\n# Fake\n[[fake]]\n```\n\nText [[real]].");
        var result = Read(vault);
        var chunk = Assert.Single(result.Chunks);
        Assert.Equal(["Real"], chunk.HeadingPath);
        Assert.Equal([new WikiLink("real", null)], chunk.Links);
    }

    [Theory]
    [InlineData("```", "---")]
    [InlineData("```yaml", "---")]
    [InlineData("~~~", "===")]
    [InlineData("~~~yaml", "---")]
    public void Fenced_examples_starting_with_heading_underlines_remain_code(string openingFence, string underline)
    {
        using var vault = new TemporaryVault();
        var closingFence = openingFence[..3];
        var example = $"{openingFence}\n{underline}\nname: example\n---\n# Fake\n[[fake]]\n{closingFence}";
        vault.Write("Example.md", $"# Examples\n\n{example}\n\nActual heading\n===\nText [[real]].");

        var result = Read(vault);

        Assert.Empty(result.Diagnostics);
        var code = Assert.Single(result.Chunks, chunk => chunk.HeadingPath.SequenceEqual(["Examples"]));
        Assert.Contains(example, code.ContentMarkdown);
        Assert.All(result.Chunks, chunk => Assert.DoesNotContain(chunk.Links, link => link.Target == "fake"));
        var prose = Assert.Single(result.Chunks, chunk => chunk.HeadingPath.SequenceEqual(["Actual heading"]));
        Assert.Contains("Text [[real]].", prose.ContentMarkdown);
        Assert.Equal([new WikiLink("real", null)], prose.Links);
    }

    [Fact]
    public void Empty_note_produces_no_chunks_and_ids_are_deterministic()
    {
        using var vault = new TemporaryVault();
        vault.Write("Empty.md", "---\ntags: []\n---\n\n");
        var first = Read(vault);
        var second = Read(vault);
        Assert.Empty(first.Chunks);
        Assert.Equal(first.Chunks, second.Chunks);
    }

    [Fact]
    public void Discovery_skips_external_symlinks_and_terminates_internal_cycles()
    {
        using var vault = new TemporaryVault();
        using var external = new TemporaryVault();
        vault.Write("Inside.md", "# Inside\nIncluded.");
        external.Write("Outside.md", "# Outside\nNot included.");
        Directory.CreateSymbolicLink(Path.Combine(vault.Path, "external"), external.Path);
        Directory.CreateSymbolicLink(Path.Combine(vault.Path, "cycle"), vault.Path);

        var result = new MarkdownIngestor().Discover(new MarkdownIngestionOptions(
            "primary", vault.Path, ["**/*.md"], [], "ai-index", 300, 400, 40));

        Assert.True(result.IsComplete);
        Assert.Equal("Inside.md", Assert.Single(result.Files).RelativePath);
    }

    [Fact]
    public void Large_prose_is_split_without_losing_all_content()
    {
        using var vault = new TemporaryVault();
        var body = string.Join(' ', Enumerable.Repeat("semantic-token", 900));
        vault.Write("Large.md", $"# Large\n{body}");
        var result = Read(vault, target: 30, softMax: 40);
        Assert.True(result.Chunks.Count > 1);
        Assert.All(result.Chunks, chunk => Assert.Contains("semantic-token", chunk.ContentNormalized));
    }

    [Fact]
    public void Preserves_structured_markdown_blocks_and_reports_malformed_fences()
    {
        using var vault = new TemporaryVault();
        vault.Write("Structured.md", "# Structured\n- one\n- two\n\n> quoted\n\n| Name | Value |\n| --- | --- |\n| A | B |\n");
        vault.Write("Broken.md", "# Broken\n```\nnever closes");
        var result = Read(vault);
        Assert.Contains(result.Chunks, x => x.ContentMarkdown.Contains("- one", StringComparison.Ordinal));
        Assert.Contains(result.Chunks, x => x.ContentNormalized.Contains("quoted", StringComparison.Ordinal));
        Assert.Contains(result.Chunks, x => x.ContentNormalized.Contains("Name", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, x => x.RelativePath == "Broken.md" && x.Code == "parse-failed");
    }

    private static IngestionResult Read(TemporaryVault vault, int target = 300, int softMax = 400) =>
        new MarkdownIngestor().ReadVault(new MarkdownIngestionOptions("primary", vault.Path, ["**/*.md"], [".obsidian/**", ".trash/**", "Templates/**"], "ai-index", target, softMax, 5));
}

internal sealed class TemporaryVault : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "knowledge-vault-" + Guid.NewGuid().ToString("N"));
    public TemporaryVault() => Directory.CreateDirectory(Path);
    public void Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }
}
