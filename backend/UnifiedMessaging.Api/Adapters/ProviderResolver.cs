namespace UnifiedMessaging.Api.Adapters;

public class ProviderResolver(IEnumerable<IMessagingProviderAdapter> adapters)
{
    private readonly Dictionary<string, IMessagingProviderAdapter> _byName =
        adapters.ToDictionary(a => a.ProviderName, StringComparer.OrdinalIgnoreCase);

    public IMessagingProviderAdapter Get(string provider) =>
        _byName.TryGetValue(provider, out var adapter)
            ? adapter
            : throw new KeyNotFoundException($"No messaging adapter registered for provider '{provider}'.");

    public bool Supports(string provider) => _byName.ContainsKey(provider);

    public IEnumerable<string> Providers => _byName.Keys;
}
