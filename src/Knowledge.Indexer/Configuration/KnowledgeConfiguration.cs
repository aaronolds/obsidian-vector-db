using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Knowledge.Indexer.Configuration;

public static class KnowledgeConfiguration
{
    public static ConfigurationManager Load(string path)
    {
        using var reader = File.OpenText(path);
        var yaml = new YamlStream();
        try { yaml.Load(reader); }
        catch (YamlException ex)
        {
            // Parser messages can contain source text. Report location only.
            throw new KnowledgeConfigurationException($"Invalid YAML at line {ex.Start.Line}, column {ex.Start.Column}.");
        }
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root || root.Children.Count == 0)
            throw new KnowledgeConfigurationException("knowledge.yaml must contain one non-empty YAML mapping.");

        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        Flatten(root, "", typeof(KnowledgeOptions), values);
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values).AddEnvironmentVariables("KNOWLEDGE_");
        return configuration;
    }

    public static IServiceCollection AddKnowledgeOptions(this IServiceCollection services, IConfiguration configuration, string configPath)
    {
        var basePath = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        services.AddSingleton<IValidateOptions<KnowledgeOptions>, KnowledgeOptionsValidator>();
        services.AddOptions<KnowledgeOptions>()
            .Bind(configuration, binder => binder.ErrorOnUnknownConfiguration = true)
            .PostConfigure(options =>
            {
                if (!string.IsNullOrWhiteSpace(options.Vault.Path))
                    options.Vault.Path = Path.GetFullPath(options.Vault.Path, basePath);
                if (!string.IsNullOrWhiteSpace(options.Embedding.TokenizerPath))
                    options.Embedding.TokenizerPath = Path.GetFullPath(options.Embedding.TokenizerPath, basePath);
            });
        return services;
    }

    private static void Flatten(YamlNode node, string path, Type expectedType, Dictionary<string, string?> values)
    {
        // Check the YAML shape against the options schema before flattening so
        // empty/incorrect mappings cannot disappear or silently use defaults.
        if (expectedType.IsArray && node is YamlSequenceNode sequence)
        {
            for (var i = 0; i < sequence.Children.Count; i++)
                Flatten(sequence.Children[i], $"{path}:{i.ToString(CultureInfo.InvariantCulture)}", expectedType.GetElementType()!, values);
            if (sequence.Children.Count == 0) Add(path, null);
        }
        else if ((expectedType.IsPrimitive || expectedType == typeof(string)) && node is YamlScalarNode scalar)
        {
            Add(path, scalar.Value ?? "");
        }
        else if (!expectedType.IsArray && !expectedType.IsPrimitive && expectedType != typeof(string) && node is YamlMappingNode mapping)
        {
            HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in mapping.Children)
            {
                if (key is not YamlScalarNode { Value: { } keyName } || !keys.Add(keyName))
                    throw new KnowledgeConfigurationException("YAML mapping keys must be unique strings (case-insensitive).");
                var property = expectedType.GetProperties().FirstOrDefault(p => p.Name.Equals(keyName, StringComparison.OrdinalIgnoreCase));
                if (property is null)
                    throw new KnowledgeConfigurationException("Unknown YAML setting. Check the configuration reference.");
                Flatten(value, path.Length == 0 ? keyName : $"{path}:{keyName}", property.PropertyType, values);
            }
        }
        else throw new KnowledgeConfigurationException($"Invalid YAML shape for {path}: expected a configuration section, list, or scalar matching the options schema.");

        void Add(string key, string? value)
        {
            if (!values.TryAdd(key, value))
                throw new KnowledgeConfigurationException("Duplicate configuration keys are not allowed (keys are case-insensitive).");
        }
    }
}

public sealed class KnowledgeConfigurationException(string message) : Exception(message);
