using Pi.Chord.Services;

namespace Pi.Chord.Facets;

/// <summary>facet 运行时记录（依赖/供给/生命周期）。对应 TS <c>FacetRuntime</c>。</summary>
internal sealed class FacetRuntime
{
    public string FacetId { get; }
    public List<FacetServiceReference> Requires { get; } = [];
    public List<FacetServiceReference> Provides { get; } = [];
    public FacetLifecycle Lifecycle { get; }
    public List<object> Implementations { get; } = [];
    public List<FacetKeyedProvision> KeyedProvisions { get; } = [];

    public FacetRuntime(string facetId)
    {
        FacetId = facetId;
        Lifecycle = new FacetLifecycle(facetId);
    }
}

internal sealed record FacetKeyedProvision(
    string ServiceId,
    bool Local,
    Action<LocalKeyedServiceRegistry>? ConnectLocal,
    Action<RemoteServiceProvider>? ConnectRemote);

/// <summary>
/// Facet 内核：原子启动/热重载/拆除的依赖与生命周期管理。
/// 对应 TS <c>FacetKernel</c>（facets/host.ts）的核心层：
/// 依赖图校验（validateFacets + 拓扑排序 + 环检测）、按序激活、失败反向清理、
/// reload 形状保持校验。服务槽与外部源的装配（HostServiceSlots/RemoteServiceSource）
/// 将在 provider 集成阶段接通。
/// </summary>
public sealed class FacetKernel
{
    private readonly IReadOnlyList<IFacet> _initialFacets;
    private readonly IReadOnlyList<IRemoteServiceSource> _serviceSources;
    private readonly IReadOnlyDictionary<string, Func<RemoteServiceFacade, object>>? _typedServiceViews;
    private readonly Dictionary<IRemoteServiceSource, IRemoteServices> _sourceBindings = [];
    private readonly Action<Exception> _onError;
    private readonly Dictionary<string, FacetRuntime> _facets = [];
    private readonly HostServiceSlots _serviceSlots = new();
    private readonly List<(string ServiceId, bool Local, object Implementation)> _singletonProvisions = [];
    private RemoteServiceProvider? _provider;
    private RemoteServiceBinding? _internalServices;
    private IReadOnlyList<string> _activationOrder = [];
    private bool _active;
    private bool _servicesAvailable;

    public FacetKernel(FacetOptions options)
    {
        var ids = options.Facets.Select(facet => facet.Id).ToList();
        if (ids.Any(id => id.Length == 0)) throw new ArgumentException("Facet ID must not be empty");
        if (ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Facet IDs must be unique within a generation");
        _initialFacets = options.Facets;
        _serviceSources = options.ServiceSources;
        _typedServiceViews = options.TypedServiceViews;
        _onError = options.OnError ?? (_ => { });
    }

    /// <summary>远程服务提供者（装配完成后可用）。对应 TS <c>provider</c>。</summary>
    public RemoteServiceProvider Provider
    {
        get
        {
            if (_provider is null)
                throw new InvalidOperationException("Facet service provider is not assembled");
            return _provider;
        }
    }

    /// <summary>本地 keyed 注册表（装配完成后可用）。</summary>
    public LocalKeyedServiceRegistry? LocalKeyedServices { get; private set; }

    /// <summary>原子启动：装配 → 依赖校验 → 拓扑序激活；失败反向清理。对应 TS <c>activate()</c>。</summary>
    public async Task ActivateAsync()
    {
        var records = new List<FacetRuntime>();
        try
        {
            foreach (var facet in _initialFacets)
            {
                var record = new FacetRuntime(facet.Id);
                _facets[facet.Id] = record;
                records.Add(record);
                SetupFacet(facet, record);
            }

            var externalServices = await ResolveExternalServicesAsync().ConfigureAwait(false);
            _activationOrder = ValidateFacets(records,
                externalServices.ToDictionary(kv => kv.Key, kv => kv.Value.Mode));
            AssembleProviders();
            BindExternalServices(externalServices);
            // 外部源就绪门。
            foreach (var services in _sourceBindings.Values)
            {
                await services.ReadyAsync().ConfigureAwait(false);
            }
            if (_internalServices is not null)
            {
                await _internalServices.ReadyAsync().ConfigureAwait(false);
            }
            _servicesAvailable = true;
            foreach (var id in _activationOrder)
            {
                await _facets[id].Lifecycle.ActivateAsync().ConfigureAwait(false);
            }
            _active = true;
        }
        catch (Exception error)
        {
            _servicesAvailable = false;
            var cleanupErrors = DisposeFacetRecords(records.AsEnumerable().Reverse());
            if (_internalServices is not null)
            {
                try
                {
                    await _internalServices.DisposeAsync(Pi.Chord.Context.Context.Background).ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    cleanupErrors.Add(cleanupError);
                }
                _internalServices = null;
            }
            _serviceSlots.Dispose();
            LocalKeyedServices?.Dispose();
            LocalKeyedServices = null;
            _sourceBindings.Clear();
            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException("Facet generation startup and cleanup failed",
                    cleanupErrors.Prepend(error).ToArray());
            }
            throw;
        }
    }

    /// <summary>
    /// 热重载：形状必须保持（requires/provides 引用集合一致），新候选按旧激活序激活，
    /// 失败反向清理。对应 TS <c>reload()</c>。
    /// </summary>
    public async Task ReloadAsync(IReadOnlyList<IFacet> facets)
    {
        if (!_active) throw new InvalidOperationException("Facet host cannot reload before activation");
        var ids = facets.Select(facet => facet.Id).ToList();
        if (ids.Any(id => id.Length == 0)) throw new ArgumentException("Facet ID must not be empty");
        if (ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Reloaded facet IDs must be unique");
        foreach (var id in ids)
        {
            if (!_facets.ContainsKey(id)) throw new InvalidOperationException($"Facet {id} is not active");
        }

        var staged = new List<FacetRuntime>();
        var candidates = new List<FacetRuntime>();
        try
        {
            foreach (var facet in facets)
            {
                var record = new FacetRuntime(facet.Id);
                staged.Add(record);
                SetupFacet(facet, record);
                var previous = _facets[facet.Id];
                if (!SameFacetShape(previous, record))
                {
                    throw new InvalidOperationException(
                        $"Reloaded facet {facet.Id} must preserve its service requirements and provisions");
                }
                candidates.Add(record);
            }
        }
        catch (Exception error)
        {
            var cleanupErrors = DisposeFacetRecords(staged.AsEnumerable().Reverse());
            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException("Facet reload setup and cleanup failed",
                    cleanupErrors.Prepend(error).ToArray());
            }
            throw;
        }

        var replacements = candidates.ToDictionary(record => record.FacetId, record => record);
        var candidateOrder = _activationOrder
            .Where(replacements.ContainsKey)
            .Select(id => replacements[id])
            .ToList();
        try
        {
            foreach (var candidate in candidateOrder)
            {
                await candidate.Lifecycle.ActivateAsync().ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            var cleanupErrors = DisposeFacetRecords(candidateOrder.AsEnumerable().Reverse());
            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException("Facet reload activation and cleanup failed",
                    cleanupErrors.Prepend(error).ToArray());
            }
            throw;
        }

        // 原子换入：旧记录 dispose。
        var replaced = candidates.Select(record => _facets[record.FacetId]).ToList();
        foreach (var record in candidates) _facets[record.FacetId] = record;
        var disposeErrors = DisposeFacetRecords(replaced);
        if (disposeErrors.Count > 0)
        {
            foreach (var error in disposeErrors) Report(error);
        }
        foreach (var provision in candidates.SelectMany(record => record.KeyedProvisions))
        {
            ConnectKeyedProvision(provision, Provider);
        }
    }

    /// <summary>拆除全部 facet（激活序的逆序）。对应 TS <c>dispose()</c>。</summary>
    public async Task<List<Exception>> DisposeAsync()
    {
        if (!_active && _facets.Count == 0) return [];
        _active = false;
        _servicesAvailable = false;
        var errors = DisposeFacetRecords(_activationOrder
            .Select(id => _facets.GetValueOrDefault(id))
            .OfType<FacetRuntime>()
            .Reverse());
        _facets.Clear();
        if (_internalServices is not null)
        {
            try
            {
                await _internalServices.DisposeAsync(Pi.Chord.Context.Context.Background).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            _internalServices = null;
        }
        _serviceSlots.Dispose();
        LocalKeyedServices?.Dispose();
        LocalKeyedServices = null;
        _sourceBindings.Clear();
        return errors;
    }

    /// <summary>
    /// 解析外部服务依赖：目录汇总（重复 offered 拒绝）→ 未本地供给的 requires
    /// 从 offered（或唯一 deferred 源）解析 → 按源分组打开。对应 TS <c>resolveExternalServices</c>。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ExternalServiceBinding>> ResolveExternalServicesAsync()
    {
        var catalogues = new List<(IRemoteServiceSource Source, IReadOnlyList<ServiceCatalogueEntry> Entries)>();
        foreach (var source in _serviceSources)
        {
            catalogues.Add((source, await source.CatalogueAsync().ConfigureAwait(false)));
        }
        var offered = new Dictionary<string, (ServiceMode Mode, IRemoteServiceSource Source)>();
        foreach (var (source, entries) in catalogues)
        {
            foreach (var entry in entries)
            {
                if (offered.ContainsKey(entry.ServiceId))
                    throw new InvalidOperationException($"Facet host service {entry.ServiceId} is offered by more than one source");
                offered[entry.ServiceId] = (entry.Mode, source);
            }
        }

        var local = _singletonProvisions.Select(provision => provision.ServiceId)
            .Concat(_facets.Values.SelectMany(record => record.KeyedProvisions)
                .Select(provision => provision.ServiceId))
            .ToHashSet(StringComparer.Ordinal);
        var external = new Dictionary<string, ExternalServiceBinding>();
        foreach (var record in _facets.Values)
        {
            foreach (var requirement in record.Requires)
            {
                if (local.Contains(requirement.ServiceId) || external.ContainsKey(requirement.ServiceId)) continue;
                if (!offered.TryGetValue(requirement.ServiceId, out var source))
                {
                    var deferred = _serviceSources.Where(s => s.AcceptsUnavailableServices).ToList();
                    if (deferred.Count > 1)
                    {
                        throw new InvalidOperationException(
                            $"Facet host service {requirement.ServiceId} has more than one deferred source");
                    }
                    if (deferred.Count == 1)
                        source = (requirement.Mode, deferred[0]);
                }
                if (source.Mode is { } mode && source.Source is { } sourceRef)
                {
                    external[requirement.ServiceId] = new ExternalServiceBinding(
                        requirement.ServiceId, mode, sourceRef);
                }
            }
        }

        var serviceIdsBySource = new Dictionary<IRemoteServiceSource, List<string>>();
        foreach (var binding in external.Values)
        {
            if (!serviceIdsBySource.TryGetValue(binding.Source, out var serviceIds))
                serviceIdsBySource[binding.Source] = serviceIds = [];
            serviceIds.Add(binding.ServiceId);
        }
        foreach (var (source, serviceIds) in serviceIdsBySource)
        {
            _sourceBindings[source] = source.Open(new RemoteServicesOptions
            {
                Services = serviceIds,
                AssertAccess = () =>
                {
                    if (!_servicesAvailable) throw new InvalidOperationException("Facet host is not active");
                },
                OnError = _onError,
            });
        }
        return external;
    }

    /// <summary>
    /// 把外部服务绑定到本地槽（门面由源实现提供）。对应 TS <c>bindServices</c> 的外部路径。
    /// <para>注册了类型化视图（<see cref="FacetOptions.TypedServiceViews"/>）的服务绑适配器而非裸门面：
    /// TS 的 Proxy 结构上满足消费者声明的接口，C# 的成员字典不满足，故由调用方提供显式适配（C374）。</para>
    /// </summary>
    private void BindExternalServices(IReadOnlyDictionary<string, ExternalServiceBinding> externalServices)
    {
        foreach (var (serviceId, binding) in externalServices)
        {
            if (!_sourceBindings.TryGetValue(binding.Source, out var services))
            {
                throw new InvalidOperationException($"Facet host service source for {serviceId} is not open");
            }
            if (binding.Mode == ServiceMode.Keyed)
            {
                _serviceSlots.BindKeyed(serviceId, new RemoteKeyedServiceSource(services));
                continue;
            }

            var facade = services.UseRaw(serviceId)
                ?? throw new InvalidOperationException($"Facet host service {serviceId} has no singleton binding");
            if (_typedServiceViews is not null
                && _typedServiceViews.TryGetValue(serviceId, out var view)
                && facade is RemoteServiceFacade remoteFacade)
            {
                facade = view(remoteFacade);
            }

            _serviceSlots.BindSingleton(serviceId, facade);
        }
    }

    /// <summary>
    /// 装配服务提供者：远程单例装进 RemoteServiceProvider（成员字典实现）、
    /// 本地单例绑定槽。对应 TS <c>assembleProviders/bindServices</c> 的核心路径。
    /// </summary>
    private void AssembleProviders()
    {
        // 仅成员字典实现（方法/复制状态）可远程发布。
        static bool IsPublishable(object implementation)
            => implementation is IReadOnlyDictionary<string, object> members
                && members.Count > 0
                && members.Values.All(v => v is Func<object?[], object?> or IReplicatedStateInternals);
        var keyedProvisions = _facets.Values.SelectMany(record => record.KeyedProvisions).ToList();
        var remoteDefinitions = _singletonProvisions
            .Where(p => !p.Local && IsPublishable(p.Implementation))
            .Select(p => new RemoteServiceProvider.ProviderDefinition(p.ServiceId, ServiceMode.Singleton))
            .Concat(keyedProvisions
                .Where(provision => !provision.Local)
                .Select(provision => new RemoteServiceProvider.ProviderDefinition(
                    provision.ServiceId, ServiceMode.Keyed)))
            .ToList();
        if (remoteDefinitions.Select(definition => definition.ServiceId)
            .Distinct(StringComparer.Ordinal).Count() != remoteDefinitions.Count)
        {
            throw new InvalidOperationException("Facet host has duplicate remote service providers");
        }
        // TS 无条件构造 provider（哪怕没有任何远程供给）：一个只提供本地服务的 host 也
        // 有一个（空的）provider，`FacetHost.Services` 因此始终可用。
        var provider = new RemoteServiceProvider(remoteDefinitions);
        _provider = provider;
        var internalServices = new RemoteServiceBinding(new RemoteServiceBindingOptions
        {
            Services = remoteDefinitions.Select(definition => definition.ServiceId).ToList(),
            Transport = LoopbackServiceTransport.Create(provider),
            AssertAccess = () =>
            {
                if (!_servicesAvailable) throw new InvalidOperationException("Facet host is not active");
            },
            OnError = _onError,
        });
        _internalServices = internalServices;
        var localKeyedIds = keyedProvisions
            .Where(provision => provision.Local)
            .Select(provision => provision.ServiceId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        LocalKeyedServices = localKeyedIds.Count > 0
            ? new LocalKeyedServiceRegistry(localKeyedIds, _onError)
            : null;

        foreach (var (serviceId, local, implementation) in _singletonProvisions)
        {
            if (local)
            {
                _serviceSlots.BindSingleton(serviceId, implementation);
                continue;
            }
            if (implementation is IReadOnlyDictionary<string, object> members
                && members.Count > 0
                && members.Values.All(v => v is Func<object?[], object?> or IReplicatedStateInternals))
            {
                provider.Provide(serviceId, members);
            }
            _serviceSlots.BindSingleton(serviceId, implementation);
        }

        foreach (var provision in keyedProvisions)
        {
            ConnectKeyedProvision(provision, provider);
            if (provision.Local)
            {
                _serviceSlots.BindKeyed(provision.ServiceId, LocalKeyedServices!);
            }
            else
            {
                _serviceSlots.BindKeyed(provision.ServiceId, new RemoteKeyedServiceSource(internalServices));
            }
        }
    }

    private void ConnectKeyedProvision(FacetKeyedProvision provision, RemoteServiceProvider provider)
    {
        if (provision.Local)
        {
            var registry = LocalKeyedServices
                ?? throw new InvalidOperationException("Local keyed services are not assembled");
            (provision.ConnectLocal
                ?? throw new InvalidOperationException($"Local keyed service {provision.ServiceId} is not connectable"))
                (registry);
        }
        else
        {
            (provision.ConnectRemote
                ?? throw new InvalidOperationException($"Remote keyed service {provision.ServiceId} is not connectable"))
                (provider);
        }
    }

    private void SetupFacet(IFacet facet, FacetRuntime record)
    {
        var environment = new Environment(this, record);
        facet.Setup(environment);
        record.Lifecycle.Prepared();
    }

    private static List<Exception> DisposeFacetRecords(IEnumerable<FacetRuntime> records)
    {
        var errors = new List<Exception>();
        foreach (var record in records)
        {
            errors.AddRange(record.Lifecycle.DisposeAsync().GetAwaiter().GetResult());
        }
        return errors;
    }

    private void Report(Exception error)
    {
        try
        {
            _onError(error);
        }
        catch (Exception reportError)
        {
            ReplicatedStates.ReportErrorAsync(reportError);
        }
    }

    private sealed class RemoteKeyedServiceSource(IRemoteServices services) : IKeyedServiceSource
    {
        public IDisposable Observe<T>(Service<T> service, Func<T, Pi.Chord.Context.Context, Task?> handler)
            => services.ObserveRaw(service.Id, (instance, context) =>
            {
                if (instance is not T typed)
                {
                    throw new InvalidOperationException(
                        $"Remote keyed service {service.Id} cannot be represented as {typeof(T).Name}");
                }
                return handler(typed, context);
            });
    }

    // ─── 依赖图校验（完整移植） ──────────────────────────────────────────

    /// <summary>供给唯一性 + 依赖存在性 + 模式匹配 + 拓扑排序。对应 TS <c>validateFacets</c>。</summary>
    private static IReadOnlyList<string> ValidateFacets(
        IReadOnlyList<FacetRuntime> records,
        IReadOnlyDictionary<string, ServiceMode> externalServices)
    {
        var providers = new Dictionary<string, (string? FacetId, ServiceMode? Mode)>();
        foreach (var (serviceId, mode) in externalServices)
        {
            providers[serviceId] = (null, mode);
        }
        foreach (var record in records)
        {
            foreach (var provision in record.Provides)
            {
                if (providers.TryGetValue(provision.ServiceId, out var existing))
                {
                    if (existing.Mode is { } existingMode && existingMode != provision.Mode)
                    {
                        throw new InvalidOperationException(
                            $"Service {provision.ServiceId} is provided as both singleton and keyed");
                    }
                    if (existing.FacetId is null)
                    {
                        throw new InvalidOperationException(
                            $"Service {provision.ServiceId} is provided by both the host and {record.FacetId}");
                    }
                    throw new InvalidOperationException(
                        $"Service {provision.ServiceId} is provided by both {existing.FacetId} and {record.FacetId}");
                }
                providers[provision.ServiceId] = (record.FacetId, provision.Mode);
            }
        }

        var dependencies = records.ToDictionary(record => record.FacetId, _ => new HashSet<string>());
        var dependents = records.ToDictionary(record => record.FacetId, _ => new HashSet<string>());
        foreach (var record in records)
        {
            foreach (var requirement in record.Requires)
            {
                if (!providers.TryGetValue(requirement.ServiceId, out var provider))
                {
                    throw new InvalidOperationException(
                        $"Facet {record.FacetId} requires local/{requirement.ServiceId}/{requirement.Mode}, but no facet provides it");
                }
                if (provider.Mode is { } providerMode && providerMode != requirement.Mode)
                {
                    throw new InvalidOperationException(
                        $"Facet {record.FacetId} requires {requirement.ServiceId} as {requirement.Mode}, but {provider.FacetId ?? "the host"} provides it as {providerMode}");
                }
                if (provider.FacetId is null || provider.FacetId == record.FacetId) continue;
                dependencies[record.FacetId].Add(provider.FacetId);
                dependents[provider.FacetId].Add(record.FacetId);
            }
        }
        return TopologicalOrder(records, dependencies, dependents);
    }

    /// <summary>Kahn 拓扑排序；环依赖报错。对应 TS <c>topologicalOrder</c>。</summary>
    private static IReadOnlyList<string> TopologicalOrder(
        IReadOnlyList<FacetRuntime> records,
        IReadOnlyDictionary<string, HashSet<string>> dependencies,
        IReadOnlyDictionary<string, HashSet<string>> dependents)
    {
        var remaining = dependencies.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        var ready = records.Select(record => record.FacetId).Where(id => remaining.GetValueOrDefault(id) == 0).ToList();
        var order = new List<string>(records.Count);
        while (ready.Count > 0)
        {
            var id = ready[0];
            ready.RemoveAt(0);
            order.Add(id);
            foreach (var dependent in dependents[id])
            {
                var count = remaining[dependent] - 1;
                remaining[dependent] = count;
                if (count == 0) ready.Add(dependent);
            }
        }
        if (order.Count != records.Count)
        {
            var cycle = records.Select(record => record.FacetId).Where(id => remaining[id] > 0);
            throw new InvalidOperationException($"Facet dependency cycle: {string.Join(", ", cycle)}");
        }
        return order;
    }

    /// <summary>reload 形状保持：requires/provides 引用集合一致。对应 TS <c>sameFacetShape</c>。</summary>
    private static bool SameFacetShape(FacetRuntime left, FacetRuntime right)
        => SameReferences(left.Requires, right.Requires) && SameReferences(left.Provides, right.Provides);

    private static bool SameReferences(
        IReadOnlyList<FacetServiceReference> left, IReadOnlyList<FacetServiceReference> right)
        => left.Count == right.Count
            && left.All(reference => right.Any(other =>
                other.ServiceId == reference.ServiceId && other.Mode == reference.Mode));

    /// <summary>
    /// 装配环境实现：记录 requires/provides、注册生命周期、创建复制状态。
    /// 服务槽解析（Use/Observe/ProvideMany 的实例接线）在 provider 集成阶段接通。
    /// </summary>
    private sealed class Environment(FacetKernel kernel, FacetRuntime record) : IFacetEnvironment
    {
        private readonly HashSet<(string ServiceId, ServiceMode Mode)> _declared = [];

        private void RecordReference(List<FacetServiceReference> target, string serviceId, ServiceMode mode)
        {
            if (target.Any(reference => reference.ServiceId == serviceId && reference.Mode == mode)) return;
            target.Add(new FacetServiceReference(serviceId, mode));
        }

        public void Require<T>(Service<T> service)
            => RecordReference(record.Requires, service.Id, ServiceMode.Singleton);

        public T Use<T>(Service<T> service)
        {
            RecordReference(record.Requires, service.Id, ServiceMode.Singleton);
            // 槽在 AssembleProviders 阶段绑定（先于激活回调），绑定前的解析由槽自身报错。
            return kernel._serviceSlots.GetSingleton(service, () => { });
        }

        public void Observe<T>(Service<T> service, Func<T, Context.Context, Task?> handler)
        {
            RecordReference(record.Requires, service.Id, ServiceMode.Keyed);
            record.Lifecycle.Observe(() => kernel._serviceSlots.Observe(service, () =>
            {
                if (record.Lifecycle.State != LifecycleState.Active)
                    throw new InvalidOperationException($"Facet {record.FacetId} cannot access services while inactive");
            }, handler));
        }

        public void Provide<T>(Service<T> service, T implementation)
        {
            RecordReference(record.Provides, service.Id, ServiceMode.Singleton);
            record.Implementations.Add(implementation!);
            kernel._singletonProvisions.Add((service.Id, service.Local, implementation!));
        }

        public IServiceSpawner<T> ProvideMany<T>(Service<T> service) where T : class
        {
            record.Lifecycle.AssertSettingUp("provide service instances");
            RecordReference(record.Provides, service.Id, ServiceMode.Keyed);
            var spawner = new StagedServiceSpawner<T>(
                new FacetLifecycleAccessor(
                    operation =>
                    {
                        if (record.Lifecycle.State != LifecycleState.Active)
                            throw new InvalidOperationException($"Facet host cannot {operation} before activation");
                    },
                    disposal => record.Lifecycle.Own(() =>
                    {
                        disposal();
                        return Task.CompletedTask;
                    })),
                (key, implementation) =>
                {
                    if (key.Length == 0)
                        throw new ArgumentException("Facet service instance key must not be empty");
                    if (implementation is System.Collections.IList)
                        throw new ArgumentException($"Facet service {service.Id} implementation must be an object");
                    if (!service.Local && implementation is not IReadOnlyDictionary<string, object>)
                    {
                        throw new ArgumentException(
                            $"Remote service {service.Id} implementation must be a member dictionary");
                    }
                });
            record.KeyedProvisions.Add(service.Local
                ? new FacetKeyedProvision(service.Id, true,
                    registry => spawner.Connect((key, implementation) => registry.Spawn(service, key, implementation)),
                    null)
                : new FacetKeyedProvision(service.Id, false, null,
                    provider => spawner.Connect((key, implementation) =>
                    {
                        if (implementation is not IReadOnlyDictionary<string, object> members)
                        {
                            throw new InvalidOperationException(
                                $"Remote service {service.Id} implementation must be a member dictionary");
                        }
                        return provider.Spawn(service.Id, key, members);
                    })));
            return spawner;
        }

        public Action SpawnDeferred<T>(Service<T> service, Func<string, T> factory)
            where T : class
        {
            var spawner = ProvideMany(service);
            return () => spawner.Spawn("__deferred__", factory("__deferred__"));
        }

        public MutableReplicatedState<T> ReplicatedState<T>(T initial) where T : class
        {
            var state = new MutableReplicatedState<T>(initial);
            record.Lifecycle.Own(() => Task.CompletedTask);
            return state;
        }

        public void Own(Func<Task> disposal) => record.Lifecycle.Own(disposal);

        public void ReportError(Exception error) => kernel._onError(error);

        public void OnActivate(Func<Task> callback) => record.Lifecycle.OnActivate(callback);

        public void OnDeactivate(Func<Task> callback) => record.Lifecycle.OnDeactivate(callback);
    }
}
