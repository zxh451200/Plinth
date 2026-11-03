using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Auth;
using Plinth.App.Localization;
using Plinth.App.Modules.RemoteDemo.Services;
using Plinth.App.Modules.RemoteDemo.ViewModels;
using Plinth.App.Navigation;
using Plinth.App.Remote;
using Plinth.App.Security;

namespace Plinth.App.Modules.RemoteDemo;

public static class RemoteDemoModuleRegistration
{
    public const string PageId = "remoteDemo.list";

    public static IServiceCollection AddRemoteDemoModule(this IServiceCollection services, Uri baseUri)
    {
        if (!RemoteApiClient.IsHttpsAddress(baseUri) || baseUri.Query.Length != 0 || !baseUri.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("remoteDemo.invalidBaseUri", nameof(baseUri));
        services.AddSingleton<RemoteDemoConnection>(provider => new(baseUri,
            provider.GetRequiredService<IAccessTokenProvider>(), provider.GetRequiredService<ISecurityAudit>(),
            provider.GetRequiredService<IRemoteUserContext>()));
        services.AddTransient(provider => ActivatorUtilities.CreateInstance<RemoteDemoService>(provider,
            provider.GetRequiredService<RemoteDemoConnection>().Api));
        // Resolve only the active page; keep dirty guards and delete confirmation in its commands.
        services.AddSingleton(new CommandMenuContribution(PageId, "demo.new",
            page => page is RemoteDemoListViewModel { CanInteract: true } vm ? vm.NewCommand : null,
            PageCommandAction.New));
        services.AddSingleton(new CommandMenuContribution(PageId, "demo.save",
            page => page is RemoteDemoListViewModel { CanInteract: true, IsEditing: true } vm ? vm.SaveCommand : null,
            PageCommandAction.Save));
        services.AddSingleton(new CommandMenuContribution(PageId, "demo.delete",
            page => page is RemoteDemoListViewModel { CanInteract: true, CanDelete: true } vm ? vm.AskDeleteCommand : null,
            PageCommandAction.Delete));
        services.AddSingleton(new CommandMenuContribution(PageId, "demo.cancel",
            page => page is not RemoteDemoListViewModel { CanInteract: true, IsEditing: true } vm ? null
                : vm.IsConfirmingDelete ? vm.CancelDeleteCommand : vm.CancelCommand,
            PageCommandAction.Cancel));
        services.AddSingleton<NavigationItem>(provider => new(PageId, Loc.Get("remoteDemo.title"),
            () => ActivatorUtilities.CreateInstance<RemoteDemoListViewModel>(provider))
        { Order = 220, Icon = "\uE774" });
        return services;
    }

    private sealed class RemoteDemoConnection : IDisposable
    {
        private readonly HttpClient _http = RemoteHttp.CreateClient();
        public RemoteApiClient Api { get; }
        public RemoteDemoConnection(Uri uri, IAccessTokenProvider tokens, ISecurityAudit audit, IRemoteUserContext users) =>
            Api = new(_http, uri, new ActorBoundTokens(tokens, users), audit, users);
        public void Dispose() => _http.Dispose();
    }

    internal sealed class ActorBoundTokens(IAccessTokenProvider tokens, IRemoteUserContext users) : IAccessTokenProvider
    {
        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            var actor = users.CurrentUser.Subject;
            var session = users.SessionVersion;
            if (string.IsNullOrWhiteSpace(actor))
                throw RemoteApiClient.Failure("remoteDemo.actorChanged", Guid.NewGuid().ToString("N"));
            var token = await tokens.GetAccessTokenAsync(cancellationToken);
            if (!string.Equals(actor, users.CurrentUser.Subject, StringComparison.Ordinal)
                || session != users.SessionVersion)
                throw RemoteApiClient.Failure("remoteDemo.actorChanged", Guid.NewGuid().ToString("N"));
            return token;
        }
    }
}
