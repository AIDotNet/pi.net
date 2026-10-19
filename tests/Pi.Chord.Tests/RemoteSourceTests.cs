using Pi.Chord.Context;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Xunit;

namespace Pi.Chord.Tests;

/// <summary>外部远程服务源绑定测试。</summary>
public class RemoteSourceTests
{
    /// <summary>假源：可编程目录与门面。</summary>
    private sealed class FakeSource(
        IReadOnlyList<ServiceCatalogueEntry>? entries = null,
        bool acceptsUnavailable = false) : IRemoteServiceSource
    {
        public List<string> OpenedServices { get; } = [];

        public bool ReadyCalled { get; private set; }

        public Dictionary<string, object> Facades { get; } = [];

        public Task<IReadOnlyList<ServiceCatalogueEntry>> CatalogueAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(entries ?? (IReadOnlyList<ServiceCatalogueEntry>)[]);

        public IRemoteServices Open(RemoteServicesOptions options)
        {
            foreach (var service in options.Services) OpenedServices.Add(service);
            return new FakeServices(this);
        }

        public bool AcceptsUnavailableServices { get; } = acceptsUnavailable;

        private sealed class FakeServices(FakeSource owner) : IRemoteServices
        {
            public Task ReadyAsync(CancellationToken cancellationToken = default)
            {
                owner.ReadyCalled = true;
                return Task.CompletedTask;
            }

            public object? UseRaw(string serviceId)
                => owner.Facades.GetValueOrDefault(serviceId);
        }
    }

    [Fact]
    public async Task ExternalServiceBindsFacadeAndReadies()
    {
        var source = new FakeSource([new ServiceCatalogueEntry("remote-svc", ServiceMode.Singleton)]);
        source.Facades["remote-svc"] = new Dictionary<string, object?> { ["remote"] = true };
        Dictionary<string, object?>? resolved = null;
        var kernel = new FacetKernel(new FacetOptions
        {
            Facets =
            [
                new SlotTestFacet("consumer", env =>
                {
                    env.Require(new Service<Dictionary<string, object?>>("remote-svc"));
                    env.OnActivate(() =>
                    {
                        resolved = env.Use(new Service<Dictionary<string, object?>>("remote-svc"));
                        return Task.CompletedTask;
                    });
                }),
            ],
            ServiceSources = [source],
        });
        await kernel.ActivateAsync();

        Assert.Equal(["remote-svc"], source.OpenedServices);
        Assert.True(source.ReadyCalled);
        Assert.Same(source.Facades["remote-svc"], resolved);
    }

    [Fact]
    public async Task DuplicateOfferAcrossSourcesRejected()
    {
        var sourceA = new FakeSource([new ServiceCatalogueEntry("svc", ServiceMode.Singleton)]);
        var sourceB = new FakeSource([new ServiceCatalogueEntry("svc", ServiceMode.Singleton)]);
        var kernel = new FacetKernel(new FacetOptions
        {
            Facets = [new SlotTestFacet("a", _ => { })],
            ServiceSources = [sourceA, sourceB],
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.ActivateAsync());
    }

    [Fact]
    public async Task DeferredSourceResolvesUnlistedRequirement()
    {
        var deferred = new FakeSource(acceptsUnavailable: true);
        deferred.Facades["late-svc"] = new Dictionary<string, object?> { ["late"] = true };
        Dictionary<string, object?>? resolved = null;
        var kernel = new FacetKernel(new FacetOptions
        {
            Facets =
            [
                new SlotTestFacet("consumer", env =>
                {
                    env.Require(new Service<Dictionary<string, object?>>("late-svc"));
                    env.OnActivate(() =>
                    {
                        resolved = env.Use(new Service<Dictionary<string, object?>>("late-svc"));
                        return Task.CompletedTask;
                    });
                }),
            ],
            ServiceSources = [deferred],
        });
        await kernel.ActivateAsync();
        Assert.Equal(["late-svc"], deferred.OpenedServices);
        Assert.Same(deferred.Facades["late-svc"], resolved);
    }

    [Fact]
    public async Task LocalRequirementDoesNotHitSources()
    {
        var source = new FakeSource([new ServiceCatalogueEntry("local-svc", ServiceMode.Singleton)]);
        var kernel = new FacetKernel(new FacetOptions
        {
            Facets =
            [
                new SlotTestFacet("provider", env =>
                {
                    env.Provide(new Service<Dictionary<string, object?>>("local-svc"),
                        new Dictionary<string, object?>());
                    env.OnActivate(() => Task.CompletedTask);
                }),
            ],
            ServiceSources = [source],
        });
        await kernel.ActivateAsync();
        Assert.Empty(source.OpenedServices); // 本地供给不打开外部源
    }

    [Fact]
    public async Task TypedServiceViewReplacesTheBoundFacade()
    {
        // C374: TS 的 use(Service) 返回结构上满足接口的 Proxy；C# 的远程服务是成员字典，
        // 消费者声明的类型化契约由 host 通过 TypedServiceViews 适配。注册了视图的服务绑适配器。
        var provider = new RemoteServiceProvider(
            [new RemoteServiceProvider.ProviderDefinition("remote-svc")]);
        provider.Provide("remote-svc", new Dictionary<string, object>
        {
            ["echo"] = (Func<object?[], object?>)(args => args[0]),
        });
        var binding = new RemoteServiceBinding(new RemoteServiceBindingOptions
        {
            Services = ["remote-svc"],
            Transport = LoopbackServiceTransport.Create(provider),
        });
        var facade = binding.Use(new Service<object>("remote-svc"));
        try
        {
            var source = new FakeSource([new ServiceCatalogueEntry("remote-svc", ServiceMode.Singleton)]);
            source.Facades["remote-svc"] = facade;
            object? resolved = null;
            var kernel = new FacetKernel(new FacetOptions
            {
                Facets =
                [
                    new SlotTestFacet("consumer", env =>
                    {
                        env.Require(new Service<object>("remote-svc"));
                        env.OnActivate(() =>
                        {
                            resolved = env.Use(new Service<object>("remote-svc"));
                            return Task.CompletedTask;
                        });
                    }),
                ],
                ServiceSources = [source],
                TypedServiceViews = new Dictionary<string, Func<RemoteServiceFacade, object>>
                {
                    ["remote-svc"] = adapted => new TypedViewForTest(adapted),
                },
            });
            await kernel.ActivateAsync();

            var view = Assert.IsType<TypedViewForTest>(resolved);
            Assert.Same(facade, view.Facade);
        }
        finally
        {
            await binding.DisposeAsync(Context.Context.Background);
        }
    }

    [Fact]
    public async Task WithoutATypedViewTheRawFacadeIsBound()
    {
        // 未注册视图的服务保持原状：绑的就是源给出的门面本身。
        var provider = new RemoteServiceProvider(
            [new RemoteServiceProvider.ProviderDefinition("remote-svc")]);
        provider.Provide("remote-svc", new Dictionary<string, object>
        {
            ["echo"] = (Func<object?[], object?>)(args => args[0]),
        });
        var binding = new RemoteServiceBinding(new RemoteServiceBindingOptions
        {
            Services = ["remote-svc"],
            Transport = LoopbackServiceTransport.Create(provider),
        });
        var facade = binding.Use(new Service<object>("remote-svc"));
        try
        {
            var source = new FakeSource([new ServiceCatalogueEntry("remote-svc", ServiceMode.Singleton)]);
            source.Facades["remote-svc"] = facade;
            object? resolved = null;
            var kernel = new FacetKernel(new FacetOptions
            {
                Facets =
                [
                    new SlotTestFacet("consumer", env =>
                    {
                        env.Require(new Service<object>("remote-svc"));
                        env.OnActivate(() =>
                        {
                            resolved = env.Use(new Service<object>("remote-svc"));
                            return Task.CompletedTask;
                        });
                    }),
                ],
                ServiceSources = [source],
            });
            await kernel.ActivateAsync();

            Assert.Same(facade, resolved);
        }
        finally
        {
            await binding.DisposeAsync(Context.Context.Background);
        }
    }

    [Fact]
    public async Task TypedServiceViewIgnoresNonFacadeTargets()
    {
        // 源给出的不是 RemoteServiceFacade（自定义 IRemoteServices 实现）时保持原绑定。
        var source = new FakeSource([new ServiceCatalogueEntry("remote-svc", ServiceMode.Singleton)]);
        var plain = new Dictionary<string, object?> { ["remote"] = true };
        source.Facades["remote-svc"] = plain;
        object? resolved = null;
        var kernel = new FacetKernel(new FacetOptions
        {
            Facets =
            [
                new SlotTestFacet("consumer", env =>
                {
                    env.Require(new Service<object>("remote-svc"));
                    env.OnActivate(() =>
                    {
                        resolved = env.Use(new Service<object>("remote-svc"));
                        return Task.CompletedTask;
                    });
                }),
            ],
            ServiceSources = [source],
            TypedServiceViews = new Dictionary<string, Func<RemoteServiceFacade, object>>
            {
                ["remote-svc"] = adapted => new TypedViewForTest(adapted),
            },
        });
        await kernel.ActivateAsync();

        Assert.Same(plain, resolved);
    }

    /// <summary>记录被适配门面的类型化视图（测试替身）。</summary>
    private sealed class TypedViewForTest(RemoteServiceFacade facade)
    {
        public RemoteServiceFacade Facade { get; } = facade;
    }

    private sealed class SlotTestFacet(string id, Action<IFacetEnvironment> setup) : IFacet
    {
        public string Id => id;

        public void Setup(IFacetEnvironment environment) => setup(environment);
    }
}
