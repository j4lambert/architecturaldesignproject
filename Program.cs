using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

public interface IPluginMetadata
{
    string ID { get; }
    string Name { get; }
    string Version { get; }
    string Description { get; }
    string Author { get; }
    IEnumerable<string> Dependencies { get; }
    IEnumerable<string> SupportedVersions { get; }
}

public interface IPlugin : IPluginMetadata
{
    bool IsInitialized { get; }
    Task<bool> InitializeAsync(IPluginContext context);
    Task<bool> StartAsync();
    Task StopAsync();
    Task CleanupAsync();
    Task<PluginStatus> GetStatusAsync();
}

public interface IPluginContext
{
    string PluginDirectory { get; }
    ILogger Logger { get; }
    IConfiguration Configuration { get; }
    IServiceProvider ServiceProvider { get; }
    IDictionary<string, object> SharedData { get; }
}

public interface IPluginManager
{
    Task<IEnumerable<IPlugin>> DiscoverPluginsAsync(string pluginPath);
    Task<bool> LoadPluginAsync(IPlugin plugin);
    Task<bool> UnloadPluginAsync(string pluginID);
    Task<IPlugin?> GetPluginAsync(string pluginID);
    Task<IEnumerable<T>> GetPluginsByTypeAsync<T>() where T : class, IPlugin;
    Task<bool> ValidatePluginAsync(IPlugin plugin);
}

public enum PluginStatus
{
    NotLoaded, Loading, Loaded, Running, Stopping, Stopped, Error
}

public interface ILogger
{
    void LogInformation(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? exception = null);
}

public interface IConfiguration
{
    string? GetValue(string key);
}

public interface IContentProcessor : IPlugin
{
    IEnumerable<string> SupportedContentTypes { get; }
    Task<bool> CanProcessAsync(ContentItem content);
    Task<ProcessingResult> ProcessContentAsync(ContentItem content, ProcessingOptions options);
    Task<ValidationResult> ValidateContentAsync(ContentItem content);
}

public interface IStorageProvider : IPlugin
{
    StorageCapabilities Capabilities { get; }
    Task<string> StoreContentAsync(ContentItem content, StorageOptions options);
    Task<ContentItem?> RetrieveContentAsync(string contentID, RevealOptions options);
    Task<bool> DeleteContentAsync(string contentID);
    Task<StorageStatistics> GetStatisticsAsync();
}

public interface IAnalyticsEngine : IPlugin
{
    IEnumerable<string> SupportedMetrics { get; }
    Task<AnalyticsReport> GenerateReportAsync(AnalysisQuery query);
    Task RecordEventAsync(AnalyticsEvent analyticsEvent);
    Task<IEnumerable<AnalyticsInsight>> GetInsightsAsync(string contentID);
}

public sealed record ContentItem(string ID, string ContentType, byte[] Data, IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ProcessingOptions(bool ValidateBeforeProcessing = true);

public sealed record ProcessingResult(bool Succeeded, byte[]? ProcessedData = null, string? OutputContentID = null, string? ErrorMessage = null);

[Flags]
public enum StorageCapabilities
{
    None = 0, Read = 1, Write = 2, Delete = 4, Statistics = 8
}

public sealed record StorageOptions(string? Container = null, bool OverwriteExisting = false);

public sealed record RevealOptions(bool IncludeMetadata = true);

public sealed record StorageStatistics(long ItemCount,long TotalBytes);

public sealed record AnalysisQuery(IReadOnlyCollection<string> Metrics, string? ContentID = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

public sealed record AnalyticsReport(DateTimeOffset GeneratedAt, IReadOnlyDictionary<string, double> Metrics);

public sealed record AnalyticsEvent(string Name, DateTimeOffset OccurredAt,string? ContentID = null, string? UserID = null, IReadOnlyDictionary<string, string>? Properties = null);

public sealed record AnalyticsInsight(string Description, string? ContentID = null,double? Confidence = null);

public abstract class PluginBase : IPlugin
{
    public virtual string ID { get; }
    public virtual string Name { get; }
    public virtual string Version { get; }
    public virtual string Description { get; }
    public virtual string Author { get; }
    public virtual IEnumerable<string> Dependencies { get; protected set; }
    public virtual IEnumerable<string> SupportedVersions { get; protected set; }

    public bool IsInitialized { get; private set; }

    protected IPluginContext? Context { get; private set; }

    protected ILogger Logger =>
        Context?.Logger ?? throw new InvalidOperationException($"Plugin {Name} has no initialized context.");

    protected PluginStatus Status { get; private set; } = PluginStatus.NotLoaded;

    protected PluginBase(string id, string name, string version, string description, string author)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        ID = id;
        Name = name;
        Version = version;
        Description = description;
        Author = author;
        Dependencies = Array.Empty<string>();
        SupportedVersions = new[] { "1.0" };
    }

    public async Task<bool> InitializeAsync(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsInitialized)
        {
            throw new InvalidOperationException($"Plugin {Name} is already initialized.");
        }

        Context = context;
        Status = PluginStatus.Loading;

        try
        {
            Logger.LogInformation($"Initializing plugin {Name} v{Version}...");
            IsInitialized = await InitializePluginAsync();
            Status = IsInitialized ? PluginStatus.Loaded : PluginStatus.Error;
            return IsInitialized;
        }
        catch (Exception ex)
        {
            IsInitialized = false;
            Status = PluginStatus.Error;
            Logger.LogError($"Initialization of plugin {Name} failed.", ex);
            return false;
        }
    }

    protected abstract Task<bool> InitializePluginAsync();

    public async Task<bool> StartAsync()
    {
        ValidateInitialized();

        try
        {
            Logger.LogInformation($"Starting plugin {Name} v{Version}...");
            var started = await StartPluginAsync();
            Status = started ? PluginStatus.Running : PluginStatus.Error;
            return started;
        }
        catch (Exception ex)
        {
            Status = PluginStatus.Error;
            Logger.LogError($"Starting plugin {Name} failed.", ex);
            return false;
        }
    }

    protected abstract Task<bool> StartPluginAsync();

    public async Task StopAsync()
    {
        if (!IsInitialized)
        {
            return;
        }

        try
        {
            Status = PluginStatus.Stopping;
            Logger.LogInformation($"Stopping plugin {Name}...");
            await StopPluginAsync();
            Status = PluginStatus.Stopped;
        }
        catch (Exception ex)
        {
            Status = PluginStatus.Error;
            Logger.LogError($"Stopping plugin {Name} failed.", ex);
            throw;
        }
    }

    protected abstract Task StopPluginAsync();

    public async Task CleanupAsync()
    {
        if (!IsInitialized)
        {
            return;
        }

        try
        {
            Logger.LogInformation($"Cleaning up plugin {Name}...");
            await CleanupPluginAsync();
            IsInitialized = false;
            Status = PluginStatus.Stopped;
        }
        catch (Exception ex)
        {
            Status = PluginStatus.Error;
            Logger.LogError($"Cleaning up plugin {Name} failed.", ex);
            throw;
        }
    }

    protected abstract Task CleanupPluginAsync();

    public Task<PluginStatus> GetStatusAsync() =>
        Task.FromResult(Status);

    protected void ValidateInitialized()
    {
        if (!IsInitialized)
        {
            throw new InvalidOperationException(
                $"Plugin {Name} v{Version} has not been initialized.");
        }
    }
}

public abstract class ContentProcessorBase : PluginBase, IContentProcessor
{
    public abstract IEnumerable<string> SupportedContentTypes { get; }

    protected ContentProcessorBase(string id, string name, string version, string description, string author):base(id, name, version, description, author){}

    public Task<bool> CanProcessAsync(ContentItem content)
    {
        ValidateInitialized();
        ArgumentNullException.ThrowIfNull(content);

        return Task.FromResult(
            SupportedContentTypes.Contains(content.ContentType, StringComparer.OrdinalIgnoreCase));
    }

    public abstract Task<ProcessingResult> ProcessContentAsync(
        ContentItem content,
        ProcessingOptions options);

    public async Task<ValidationResult> ValidateContentAsync(ContentItem content)
    {
        ValidateInitialized();
        ArgumentNullException.ThrowIfNull(content);

        return await ValidateContentImplementationAsync(content);
    }

    protected abstract Task<ValidationResult> ValidateContentImplementationAsync(ContentItem content);
}

public sealed class TextContentProcessor : ContentProcessorBase
{
    public override IEnumerable<string> SupportedContentTypes { get; } = new[] { "text/plain", "text/markdown", "text/html" };

    public TextContentProcessor():base("text-processor", "Text Content Processor", "1.0.0", "Processes text content.", "TechFlow")
    {
    }

    protected override Task<bool> InitializePluginAsync() =>
        Task.FromResult(true);

    protected override Task<bool> StartPluginAsync() =>
        Task.FromResult(true);

    protected override Task StopPluginAsync() =>
        Task.CompletedTask;

    protected override Task CleanupPluginAsync() =>
        Task.CompletedTask;

    protected override Task<ValidationResult> ValidateContentImplementationAsync(ContentItem content)
    {
        if (!SupportedContentTypes.Contains(content.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(new ValidationResult($"Unsupported content type: {content.ContentType}"));
        }

        if (content.Data.Length == 0)
        {
            return Task.FromResult(new ValidationResult("Content data cannot be empty."));
        }

        return Task.FromResult(ValidationResult.Success!);
    }

    public override async Task<ProcessingResult> ProcessContentAsync(ContentItem content,ProcessingOptions options)
    {
        ValidateInitialized();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        if (options.ValidateBeforeProcessing)
        {
            var validation = await ValidateContentAsync(content);
            if (validation != ValidationResult.Success)
            {
                return new ProcessingResult(Succeeded: false, ErrorMessage: validation.ErrorMessage);
            }
        }

        var text = Encoding.UTF8.GetString(content.Data);
        var processedData = Encoding.UTF8.GetBytes(text.ToUpperInvariant());

        return new ProcessingResult(Succeeded: true, ProcessedData: processedData);
    }
}

public sealed class InMemoryStorageProvider : PluginBase, IStorageProvider
{
    private readonly Dictionary<string, ContentItem> _items = new();

    public StorageCapabilities Capabilities => StorageCapabilities.Read | StorageCapabilities.Write | StorageCapabilities.Delete |StorageCapabilities.Statistics;

    public InMemoryStorageProvider():base("memory-storage", "In-Memory Storage Provider", "1.0.0", "Stores content in memory for the demo.", "TechFlow"){}

    protected override Task<bool> InitializePluginAsync() => Task.FromResult(true);

    protected override Task<bool> StartPluginAsync() => Task.FromResult(true);

    protected override Task StopPluginAsync() => Task.CompletedTask;

    protected override Task CleanupPluginAsync()
    {
        _items.Clear();
        return Task.CompletedTask;
    }

    public Task<string> StoreContentAsync(ContentItem content, StorageOptions options)
    {
        ValidateInitialized();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        var id = $"content_{Guid.NewGuid():N}";
        _items[id] = content with
        {
            ID = id,
            Data = content.Data.ToArray()
        };

        return Task.FromResult(id);
    }

    public Task<ContentItem?> RetrieveContentAsync(string contentID, RevealOptions options)
    {
        ValidateInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(contentID);
        ArgumentNullException.ThrowIfNull(options);

        if (!_items.TryGetValue(contentID, out var item))
        {
            return Task.FromResult<ContentItem?>(null);
        }

        var result = item with
        {
            Data = item.Data.ToArray(),
            Metadata = options.IncludeMetadata ? item.Metadata : null
        };

        return Task.FromResult<ContentItem?>(result);
    }

    public Task<bool> DeleteContentAsync(string contentID)
    {
        ValidateInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(contentID);

        return Task.FromResult(_items.Remove(contentID));
    }

    public Task<StorageStatistics> GetStatisticsAsync()
    {
        ValidateInitialized();

        var totalBytes = _items.Values.Sum(item => (long)item.Data.Length);
        return Task.FromResult(new StorageStatistics(_items.Count, totalBytes));
    }
}

public sealed class BasicAnalyticsEngine : PluginBase, IAnalyticsEngine
{
    private readonly List<AnalyticsEvent> _events = new();

    public IEnumerable<string> SupportedMetrics { get; } =
        new[] { "views", "engagement", "performance", "errors", "TotalEvents" };

    public BasicAnalyticsEngine():base("basic-analytics", "Basic Analytics Engine", "1.0.0", "Provides simple in-memory analytics.", "TechFlow"){}

    protected override Task<bool> InitializePluginAsync() =>
        Task.FromResult(true);

    protected override Task<bool> StartPluginAsync() =>
        Task.FromResult(true);

    protected override Task StopPluginAsync() =>
        Task.CompletedTask;

    protected override Task CleanupPluginAsync()
    {
        _events.Clear();
        return Task.CompletedTask;
    }

    public Task RecordEventAsync(AnalyticsEvent analyticsEvent)
    {
        ValidateInitialized();
        ArgumentNullException.ThrowIfNull(analyticsEvent);

        _events.Add(analyticsEvent);
        return Task.CompletedTask;
    }

    public Task<AnalyticsReport> GenerateReportAsync(AnalysisQuery query)
    {
        ValidateInitialized();
        ArgumentNullException.ThrowIfNull(query);

        if (query.From is not null && query.To is not null && query.From > query.To)
        {
            throw new ArgumentException("The query start time must be earlier than its end time.", nameof(query));
        }

        var events = FilterEvents(query).ToList();
        var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var metric in query.Metrics)
        {
            metrics[metric] = string.Equals(metric, "TotalEvents",StringComparison.OrdinalIgnoreCase) ? events.Count:events.Count(e => string.Equals(e.Name, metric, StringComparison.OrdinalIgnoreCase));
        }

        return Task.FromResult(new AnalyticsReport(DateTimeOffset.UtcNow, metrics));
    }

    public Task<IEnumerable<AnalyticsInsight>> GetInsightsAsync(string contentID)
    {
        ValidateInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(contentID);

        var count = _events.Count(e => string.Equals(e.ContentID, contentID, StringComparison.Ordinal));

        IEnumerable<AnalyticsInsight> insights = count == 0 ? Array.Empty<AnalyticsInsight>():new[]
            {
                new AnalyticsInsight($"Recorded {count} analytics event(s).", contentID)
            };

        return Task.FromResult(insights);
    }

    private IEnumerable<AnalyticsEvent> FilterEvents(AnalysisQuery query) => _events.Where(e => (query.ContentID is null || e.ContentID == query.ContentID) && (query.From is null || e.OccurredAt >= query.From) && (query.To is null || e.OccurredAt <= query.To));
}

public sealed class PluginManager : IPluginManager
{
    private readonly Dictionary<string, IPlugin> _loadedPlugins = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public PluginManager(ILogger logger, IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    public Task<IEnumerable<IPlugin>> DiscoverPluginsAsync(string pluginPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginPath);

        // This demo uses known plugin types instead of scanning assemblies.
        IEnumerable<IPlugin> plugins = new IPlugin[]
        {
            new TextContentProcessor(),
            new InMemoryStorageProvider(),
            new BasicAnalyticsEngine()
        };

        return Task.FromResult(plugins);
    }

    public async Task<bool> LoadPluginAsync(IPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        if (!await ValidatePluginAsync(plugin))
        {
            _logger.LogWarning($"Plugin validation failed: {plugin.Name}");
            return false;
        }

        var context = new PluginContext($"/plugins/{plugin.ID}", _logger, _configuration, _serviceProvider);

        var initialized = await plugin.InitializeAsync(context);
        if (!initialized)
        {
            _logger.LogError($"Plugin initialization failed: {plugin.Name}");
            return false;
        }

        var started = await plugin.StartAsync();
        if (!started)
        {
            _logger.LogError($"Plugin start failed: {plugin.Name}");
            await plugin.CleanupAsync();
            return false;
        }

        _loadedPlugins.Add(plugin.ID, plugin);
        _logger.LogInformation($"Loaded plugin: {plugin.Name}");
        return true;
    }

    public async Task<bool> UnloadPluginAsync(string pluginID)
    {
        if (!_loadedPlugins.TryGetValue(pluginID, out var plugin))
        {
            _logger.LogWarning($"Plugin not found: {pluginID}");
            return false;
        }

        try
        {
            await plugin.StopAsync();
            await plugin.CleanupAsync();
            _loadedPlugins.Remove(pluginID);
            _logger.LogInformation($"Unloaded plugin: {plugin.Name}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to unload plugin: {plugin.Name}", ex);
            return false;
        }
    }

    public Task<IPlugin?> GetPluginAsync(string pluginID)
    {
        _loadedPlugins.TryGetValue(pluginID, out var plugin);
        return Task.FromResult(plugin);
    }

    public Task<IEnumerable<T>> GetPluginsByTypeAsync<T>()
        where T : class, IPlugin
    {
        IEnumerable<T> plugins = _loadedPlugins.Values.OfType<T>().ToArray();
        return Task.FromResult(plugins);
    }

    public Task<bool> ValidatePluginAsync(IPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        var valid = !string.IsNullOrWhiteSpace(plugin.ID) && !string.IsNullOrWhiteSpace(plugin.Name) && !string.IsNullOrWhiteSpace(plugin.Version) && !_loadedPlugins.ContainsKey(plugin.ID);

        return Task.FromResult(valid);
    }
}

public sealed class PluginContext : IPluginContext
{
    public string PluginDirectory { get; }
    public ILogger Logger { get; }
    public IConfiguration Configuration { get; }
    public IServiceProvider ServiceProvider { get; }
    public IDictionary<string, object> SharedData { get; } = new Dictionary<string, object>();

    public PluginContext(string pluginDirectory, ILogger logger, IConfiguration configuration, IServiceProvider serviceProvider)
    {
        PluginDirectory = pluginDirectory; Logger = logger;
        Configuration = configuration; ServiceProvider = serviceProvider;
    }
}

public sealed class ConsoleLogger : ILogger
{
    public void LogInformation(string message) => Console.WriteLine($"INFO: {message}");

    public void LogWarning(string message) => Console.WriteLine($"WARN: {message}");

    public void LogError(string message, Exception? exception = null)
    {
        Console.WriteLine($"ERROR: {message}");

        if (exception is not null)
        {
            Console.WriteLine(exception);
        }
    }
}

public sealed class DictionaryConfiguration : IConfiguration
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public DictionaryConfiguration(IReadOnlyDictionary<string, string> values)
    {
        _values = values;
    }

    public string? GetValue(string key) => _values.TryGetValue(key, out var value) ? value : null;
}

public sealed class DemoServiceProvider : IServiceProvider
{
    private readonly IConfiguration _configuration;

    public DemoServiceProvider(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public object? GetService(Type serviceType) => serviceType == typeof(IConfiguration) ? _configuration : null;
}

public static class Program
{
    public static async Task Main()
    {
        var logger = new ConsoleLogger();
        var configuration = new DictionaryConfiguration(new Dictionary<string, string>());
        var services = new DemoServiceProvider(configuration);
        var manager = new PluginManager(logger, services, configuration);

        var plugins = await manager.DiscoverPluginsAsync("/plugins");

        foreach (var plugin in plugins)
        {
            await manager.LoadPluginAsync(plugin);
        }

        var processors = await manager.GetPluginsByTypeAsync<IContentProcessor>();
        var processor = processors.FirstOrDefault();

        if (processor is not null)
        {
            var content = new ContentItem("demo-content","text/plain", Encoding.UTF8.GetBytes("Hello plugin architecture!"));

            var result = await processor.ProcessContentAsync(content, new ProcessingOptions());

            Console.WriteLine(result.Succeeded ? Encoding.UTF8.GetString(result.ProcessedData!):result.ErrorMessage);
        }

        var storage = await manager.GetPluginsByTypeAsync<IStorageProvider>();
        var storageProvider = storage.FirstOrDefault();

        if (storageProvider is not null)
        {
            var content = new ContentItem("stored-content", "text/plain", Encoding.UTF8.GetBytes("Stored in memory."));

            var storedID = await storageProvider.StoreContentAsync(content, new StorageOptions());

            Console.WriteLine($"Stored content ID: {storedID}");
        }

        var analytics = await manager.GetPluginsByTypeAsync<IAnalyticsEngine>();
        var analyticsEngine = analytics.FirstOrDefault();

        if (analyticsEngine is not null)
        {
            await analyticsEngine.RecordEventAsync(new AnalyticsEvent("views",DateTimeOffset.UtcNow, "demo-content", "demo-user"));

            var report = await analyticsEngine.GenerateReportAsync(new AnalysisQuery(new[] { "views", "TotalEvents" }, ContentID: "demo-content"));

            foreach (var metric in report.Metrics)
            {
                Console.WriteLine($"{metric.Key}: {metric.Value}");
            }
        }

        foreach (var plugin in plugins)
        {
            await manager.UnloadPluginAsync(plugin.ID);
        }

        Console.WriteLine("Plugin demo completed.");
    }
}