using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Auth;
using Plinth.App.Modules.RemoteDemo;
using Plinth.App.Modules.RemoteDemo.Data;
using Plinth.App.Modules.RemoteDemo.Services;
using Plinth.App.Modules.RemoteDemo.ViewModels;
using Plinth.App.Modules.RemoteDemo.Views;
using Plinth.App.Navigation;
using Plinth.App.Remote;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class RemoteDemoTests
{
    private static readonly Guid RecordId = Guid.Parse("be6f295b-081d-49c5-97de-a199e296df58");
    private static readonly RemoteDemoInput Input = new("C1", "Name", "");
    private static string RecordJson(int version = 1) =>
        JsonSerializer.Serialize(new { id = RecordId, code = "C1", name = "Name", remark = "", version });
    private static string PageJson(int page = 1, int size = 50, long total = 1, string? items = null) =>
        $"{{\"page\":{page},\"pageSize\":{size},\"totalCount\":{total},\"items\":{items ?? "[" + RecordJson() + "]"}}}";
    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        response.Headers.Add("X-Correlation-ID", "server-test-1");
        return response;
    }

    [Fact]
    public async Task Query_is_server_paged_escaped_and_has_no_IfMatch()
    {
        using var fixture = new Fixture((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/demo-records", request.RequestUri!.AbsolutePath);
            Assert.Equal("?page=2&pageSize=25&search=a%26b%20%2F&sortField=Name&descending=true", request.RequestUri.Query);
            Assert.Empty(request.Headers.IfMatch);
            return Task.FromResult(Json(PageJson(2, 25, 26)));
        });
        var page = await fixture.Service.SearchPageAsync("a&b /", 2, 25, "Name", true);
        Assert.Equal(26, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Empty_out_of_range_page_and_intmax_total_do_not_overflow()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(PageJson(int.MaxValue, 200, int.MaxValue, "[]"))));
        var result = await fixture.Service.SearchPageAsync(page: int.MaxValue, pageSize: 200);
        Assert.Equal(int.MaxValue, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData(0, 50, "Code")]
    [InlineData(1, 201, "Code")]
    [InlineData(1, 0, "Code")]
    [InlineData(1, 50, "Remark")]
    [InlineData(1, 50, "code")]
    public async Task Invalid_query_never_sends(int page, int size, string sort)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(PageJson())));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SearchPageAsync(page: page, pageSize: size, sortField: sort));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    public static IEnumerable<object[]> InvalidPages()
    {
        yield return [PageJson(2)];
        yield return [PageJson(size: 25)];
        yield return [PageJson(total: -1)];
        yield return [PageJson(total: (long)int.MaxValue + 1)];
        yield return [PageJson(items: "[]")];
        yield return [PageJson(total: 0)];
        yield return [PageJson(items: "[" + RecordJson(0) + "]")];
        yield return [PageJson(items: "[" + RecordJson().Replace(RecordId.ToString(), Guid.Empty.ToString()) + "]")];
        yield return [PageJson(items: "[" + RecordJson().Replace("\"C1\"", "\"\"") + "]")];
        yield return [PageJson(items: "[" + RecordJson().Replace("\"C1\"", "\"" + new string('x', 51) + "\"") + "]")];
        yield return [PageJson(total: 2, items: "[" + RecordJson() + "," + RecordJson() + "]")];
        yield return [PageJson().Replace("\"totalCount\":1", "\"totalCount\":1.5")];
        yield return [PageJson().Replace("\"page\":1", "\"page\":1,\"page\":1")];
        yield return [PageJson().Replace("\"C1\"", "null")];
        yield return [PageJson().Replace("\"C1\"", "\"\\uD800\"")];
        yield return ["{\"page\":1}"];
        yield return ["not json: private server text"];
    }

    [Theory]
    [MemberData(nameof(InvalidPages))]
    public async Task Malformed_response_is_safe_and_rejected(string json)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(json)));
        var error = await Assert.ThrowsAsync<RemoteApiException>(() => fixture.Service.SearchPageAsync());
        Assert.Equal("remoteDemo.invalidResponse.message", error.Error.MessageKey);
        Assert.Equal("server-test-1", error.Error.CorrelationId);
        Assert.DoesNotContain("private server text", error.ToString());
    }

    [Fact]
    public async Task Create_put_and_delete_use_fixed_routes_and_strong_versions()
    {
        using var fixture = new Fixture(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/demo-records", request.RequestUri!.AbsolutePath);
                Assert.Empty(request.Headers.IfMatch);
            }
            else
            {
                Assert.Equal("/api/demo-records/" + RecordId.ToString("D"), request.RequestUri!.AbsolutePath);
                var tag = Assert.Single(request.Headers.IfMatch);
                Assert.False(tag.IsWeak);
                Assert.Equal("\"3\"", tag.Tag);
            }
            if (request.Method != HttpMethod.Delete)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("C1", body.RootElement.GetProperty("code").GetString());
                Assert.False(body.RootElement.TryGetProperty("version", out _));
            }
            return request.Method == HttpMethod.Delete ? new(HttpStatusCode.NoContent) : Json(RecordJson(4));
        });
        await fixture.Service.CreateAsync(Input);
        await fixture.Service.UpdateAsync(RecordId, 3, Input);
        await fixture.Service.DeleteAsync(RecordId, 3);
        Assert.Equal(3, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(401, "remote.unauthorized")]
    [InlineData(403, "remote.forbidden")]
    [InlineData(409, "remote.conflict")]
    [InlineData(412, "remote.conflict")]
    [InlineData(503, "remote.http_error")]
    public async Task Failed_writes_are_never_retried_or_expose_server_text(int status, string key)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json("{\"message\":\"private server body\"}", (HttpStatusCode)status)));
        var error = await Assert.ThrowsAsync<RemoteApiException>(() => fixture.Service.UpdateAsync(RecordId, 1, Input));
        Assert.Equal(key + ".message", error.Error.MessageKey);
        Assert.Equal(status, error.Error.StatusCode);
        Assert.Equal("server-test-1", error.Error.CorrelationId);
        Assert.False(error.Error.CanRetry);
        Assert.DoesNotContain("private server body", error.ToString());
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Invalid_id_version_and_utf16_are_rejected_before_network()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(RecordJson())));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.UpdateAsync(Guid.Empty, 1, Input));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.DeleteAsync(RecordId, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateAsync(Input with { Name = "\uD800" }));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SearchPageAsync("\uDC00"));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Missing_permission_and_declined_production_confirmation_send_nothing()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(RecordJson())), production: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.CreateAsync(Input));
        fixture.Users.User = RemoteUserContext.Anonymous;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SearchPageAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.DeleteAsync(RecordId, 1));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Actor_changed_during_confirmation_prevents_write()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(RecordJson())), production: true);
        fixture.Sensitive.ConfirmProductionAsync = () =>
        {
            fixture.Users.ChangeActor();
            return Task.FromResult(true);
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.CreateAsync(Input));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actor_changed_during_read_or_write_rejects_response(bool write)
    {
        Fixture? owner = null;
        using var fixture = new Fixture((_, _) =>
        {
            owner!.Users.ChangeActor();
            return Task.FromResult(Json(write ? RecordJson() : PageJson()));
        });
        owner = fixture;
        var error = await Assert.ThrowsAsync<RemoteApiException>(async () =>
        {
            if (write) await fixture.Service.CreateAsync(Input);
            else await fixture.Service.SearchPageAsync();
        });
        Assert.Equal("remoteDemo.actorChanged", error.Error.Code);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_reaches_fake_transport()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (_, ct) =>
        {
            entered.SetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            return Json(PageJson());
        });
        using var cancel = new CancellationTokenSource();
        var pending = fixture.Service.SearchPageAsync(cancellationToken: cancel.Token);
        await entered.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Every_write_verb_has_exactly_one_attempt_on_transient_failure(string verb)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable)));
        var error = await Assert.ThrowsAsync<RemoteApiException>(async () =>
        {
            if (verb == "POST") await fixture.Service.CreateAsync(Input);
            else if (verb == "PUT") await fixture.Service.UpdateAsync(RecordId, 1, Input);
            else await fixture.Service.DeleteAsync(RecordId, 1);
        });
        Assert.False(error.Error.CanRetry);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Actor_changed_while_getting_token_never_sends_request()
    {
        var users = new Users();
        using var handler = new Handler((_, _) => Task.FromResult(Json(PageJson())));
        using var http = RemoteHttp.CreateClient(handler);
        var tokens = new RemoteDemoModuleRegistration.ActorBoundTokens(new SwitchingTokens(users), users);
        var api = new RemoteApiClient(http, new Uri("https://api.example/"), tokens, users: users);
        var error = await Assert.ThrowsAsync<RemoteApiException>(() => api.SendAsync(HttpMethod.Get, "demo-records"));
        Assert.Equal("remoteDemo.actorChanged", error.Error.Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Cancellation_after_a_response_still_discards_result()
    {
        using var cancel = new CancellationTokenSource();
        using var fixture = new Fixture((_, _) =>
        {
            cancel.Cancel();
            return Task.FromResult(Json(PageJson()));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.SearchPageAsync(cancellationToken: cancel.Token));
    }

    [Fact]
    public async Task Http_client_rejects_nonpositive_or_read_ETags()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(PageJson())));
        using var http = RemoteHttp.CreateClient(handler);
        var api = new RemoteApiClient(http, new Uri("https://api.example/"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            api.SendAsync(HttpMethod.Put, "demo-records", expectedVersion: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            api.SendAsync(HttpMethod.Get, "demo-records", expectedVersion: 1));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Page_disposal_cancels_load_and_ignores_late_results()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture((_, ct) =>
        {
            ct.Register(() => release.TrySetCanceled(ct));
            entered.SetResult(true);
            return release.Task;
        });
        var vm = new RemoteDemoListViewModel(fixture.Service, new UnsavedChangesService(), fixture.Users);
        await entered.Task;
        vm.Dispose();
        Assert.True(release.Task.IsCanceled);
        Assert.Empty(vm.Items);
        Assert.False(vm.CanInteract);
    }

    [Fact]
    public async Task Dirty_form_cancel_preserves_new_search_and_page_transitions()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(PageJson())));
        var changes = new UnsavedChangesService { PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Cancel) };
        using var vm = new RemoteDemoListViewModel(fixture.Service, changes, fixture.Users);
        await vm.NewCommand.ExecuteAsync(null);
        vm.EditCode = "unsaved";
        vm.EditName = "draft";
        vm.SearchText = "other";
        var calls = fixture.Handler.Calls;
        await vm.SearchCommand.ExecuteAsync(null);
        await vm.NewCommand.ExecuteAsync(null);
        Assert.False(await vm.TryLeaveAsync());
        Assert.Equal("unsaved", vm.EditCode);
        Assert.True(vm.IsDirty);
        Assert.Equal(calls, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Delete_requires_204_even_when_other_status_is_successful()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json("{}", HttpStatusCode.OK)));
        var error = await Assert.ThrowsAsync<RemoteApiException>(() => fixture.Service.DeleteAsync(RecordId, 1));
        Assert.Equal("remoteDemo.invalidResponse", error.Error.Code);
        Assert.Equal("remote.suggestion.verify_result", error.Error.SuggestionKey);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Interrupted_write_requires_verifying_server_result()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (_, ct) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            return Json(RecordJson());
        });
        using var cancel = new CancellationTokenSource();
        var pending = fixture.Service.CreateAsync(Input, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        var error = await Assert.ThrowsAsync<RemoteApiException>(() => pending);
        Assert.Equal("remoteDemo.writeUncertain.message", error.Error.MessageKey);
        Assert.Equal("remote.suggestion.verify_result", error.Error.SuggestionKey);
        Assert.False(error.Error.CanRetry);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Account_change_clears_old_data_and_refresh_binds_new_account()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(PageJson())));
        using var vm = new RemoteDemoListViewModel(fixture.Service,
            new UnsavedChangesService(), fixture.Users);
        await vm.RefreshAsync();
        await vm.SelectRecordAsync(Assert.Single(vm.Items));
        vm.EditName = "old account draft";
        fixture.Users.ChangeActor();
        vm.RefreshAccess();
        Assert.Empty(vm.Items);
        Assert.False(vm.IsEditing);
        Assert.Empty(vm.EditName);
        await vm.RefreshAsync();
        Assert.Single(vm.Items);
        Assert.Empty(vm.ErrorMessage);
    }

    [Fact]
    public async Task Same_account_new_session_clears_hidden_page_draft_before_refresh()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(PageJson())));
        using var vm = new RemoteDemoListViewModel(fixture.Service,
            new UnsavedChangesService(), fixture.Users);
        await vm.RefreshAsync();
        await vm.SelectRecordAsync(Assert.Single(vm.Items));
        vm.EditName = "previous session draft";
        fixture.Users.NewSession();
        vm.RefreshAccess();
        Assert.Empty(vm.Items);
        Assert.Empty(vm.EditName);
        Assert.False(vm.IsEditing);
        await vm.RefreshAsync();
        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task Same_account_new_session_during_confirmation_prevents_write()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(RecordJson())), production: true);
        fixture.Sensitive.ConfirmProductionAsync = () =>
        {
            fixture.Users.NewSession();
            return Task.FromResult(true);
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.CreateAsync(Input));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData("create", "switch")]
    [InlineData("update", "session")]
    [InlineData("delete", "revoke")]
    [InlineData("create", "revoke")]
    [InlineData("update", "revoke")]
    [InlineData("create", "decline")]
    [InlineData("update", "decline")]
    [InlineData("delete", "decline")]
    [InlineData("create", "stable")]
    [InlineData("update", "stable")]
    [InlineData("delete", "stable")]
    public async Task Confirmation_rechecks_before_sending_each_write(string operation, string change)
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(RecordJson(2))), production: true);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmations = 0;
        fixture.Sensitive.ConfirmProductionAsync = () =>
        {
            confirmations++;
            entered.TrySetResult(true);
            return release.Task;
        };
        Task pending = operation switch
        {
            "create" => fixture.Service.CreateAsync(Input),
            "update" => fixture.Service.UpdateAsync(RecordId, 1, Input),
            _ => fixture.Service.DeleteAsync(RecordId, 1),
        };
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, fixture.Handler.Calls);
            switch (change)
            {
                case "switch": fixture.Users.ChangeActor(); break;
                case "session": fixture.Users.NewSession(); break;
                case "revoke": fixture.Users.User = fixture.Users.User with { Permissions = ImmutableHashSet<string>.Empty }; break;
            }
        }
        finally { release.TrySetResult(change != "decline"); }
        if (change == "stable")
            await pending;
        else if (change == "decline")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pending);
        Assert.Equal(1, confirmations);
        Assert.Equal(change == "stable" ? 1 : 0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Query_can_be_closed_before_transport_finishes()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (_, ct) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            return Json(PageJson());
        });
        using var vm = new RemoteDemoListViewModel(fixture.Service,
            new UnsavedChangesService(), fixture.Users);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsBusy);
        Assert.True(await vm.TryLeaveAsync());
    }

    [Fact]
    public void Registration_is_opt_in_and_does_not_register_page_in_root()
    {
        var services = new ServiceCollection();
        Assert.Empty(services);
        services.AddRemoteDemoModule(new Uri("https://api.example/api/"));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(RemoteDemoListViewModel));
        Assert.Contains(services, d => d.ServiceType == typeof(NavigationItem));
        using var provider = services.BuildServiceProvider();
        var commands = provider.GetServices<CommandMenuContribution>().ToArray();
        CommandMenuContribution.Validate(commands);
        Assert.Equal(4, commands.Length);
        Assert.All(commands, command =>
        {
            Assert.Equal(RemoteDemoModuleRegistration.PageId, command.PageId);
            Assert.Null(command.ResolveCommand(new object()));
        });
        Assert.Equal(Enum.GetValues<PageCommandAction>().Order(), commands.Select(c => c.Action!.Value).Order());
    }

    private static CommandMenuContribution[] PageCommands()
    {
        var services = new ServiceCollection();
        services.AddRemoteDemoModule(new Uri("https://api.example/api/"));
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<CommandMenuContribution>().ToArray();
    }

    [Fact]
    public async Task Page_actions_preserve_dirty_input_and_cancel_delete_before_closing_form()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(PageJson())));
        var prompts = 0;
        var changes = new UnsavedChangesService
        {
            PromptAsync = () =>
            {
                prompts++;
                return Task.FromResult(UnsavedChangesChoice.Cancel);
            },
        };
        using var vm = new RemoteDemoListViewModel(fixture.Service, changes, fixture.Users);
        await vm.RefreshAsync();
        var commands = PageCommands();
        CommandMenuContribution Action(PageCommandAction action) => commands.Single(c => c.Action == action);
        Assert.Null(Action(PageCommandAction.Save).ResolveCommand(vm));
        Assert.Null(Action(PageCommandAction.Delete).ResolveCommand(vm));
        Assert.Null(Action(PageCommandAction.Cancel).ResolveCommand(vm));
        Assert.Same(vm.NewCommand, Action(PageCommandAction.New).ResolveCommand(vm));
        await vm.SelectRecordAsync(Assert.Single(vm.Items));
        vm.EditName = "keep draft";
        var calls = fixture.Handler.Calls;
        Action(PageCommandAction.Delete).ResolveCommand(vm)!.Execute(null);
        Assert.True(vm.IsConfirmingDelete);
        Assert.Equal(calls, fixture.Handler.Calls);
        Assert.Same(vm.CancelDeleteCommand, Action(PageCommandAction.Cancel).ResolveCommand(vm));
        Action(PageCommandAction.Cancel).ResolveCommand(vm)!.Execute(null);
        Assert.False(vm.IsConfirmingDelete);
        Assert.True(vm.IsEditing);
        Assert.Equal(0, prompts);
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(
            Action(PageCommandAction.Cancel).ResolveCommand(vm)).ExecuteAsync(null);
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(
            Action(PageCommandAction.New).ResolveCommand(vm)).ExecuteAsync(null);
        Assert.Equal(2, prompts);
        Assert.Equal("keep draft", vm.EditName);
        Assert.True(vm.IsDirty);
        Assert.Equal(calls, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Loading_disables_page_actions_and_empty_state_and_still_allows_close()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (_, ct) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            return Json(PageJson());
        });
        using var vm = new RemoteDemoListViewModel(fixture.Service, new UnsavedChangesService(), fixture.Users);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsBusy);
        Assert.False(vm.IsEmpty);
        Assert.All(PageCommands(), command => Assert.Null(command.ResolveCommand(vm)));
        Assert.True(await vm.TryLeaveAsync());
        vm.Dispose();
        Assert.All(PageCommands(), command => Assert.Null(command.ResolveCommand(vm)));
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task Empty_state_tracks_success_error_recovery_and_session_invalidation()
    {
        var fail = false;
        using var fixture = new Fixture((_, _) => Task.FromResult(fail
            ? Json("{}", HttpStatusCode.BadRequest) : Json(PageJson(total: 0, items: "[]"))));
        using var vm = new RemoteDemoListViewModel(fixture.Service, new UnsavedChangesService(), fixture.Users);
        await vm.RefreshAsync();
        Assert.True(vm.IsEmpty);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        fail = true;
        await vm.RefreshAsync();
        Assert.False(vm.IsEmpty);
        Assert.NotEmpty(vm.ErrorMessage);
        fail = false;
        await vm.RefreshAsync();
        Assert.True(vm.IsEmpty);
        Assert.Empty(vm.ErrorMessage);
        Assert.Contains(nameof(vm.IsEmpty), notifications);
        fixture.Users.NewSession();
        vm.RefreshAccess();
        Assert.False(vm.IsEmpty);
        await vm.RefreshAsync();
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task Pending_write_blocks_page_actions_and_disposal_cancels_without_retry()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Json(PageJson());
            using var registration = ct.Register(() => cancelled.TrySetResult(true));
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            return Json(RecordJson());
        });
        using var vm = new RemoteDemoListViewModel(fixture.Service, new UnsavedChangesService(), fixture.Users);
        await vm.RefreshAsync();
        await vm.NewCommand.ExecuteAsync(null);
        vm.EditCode = "NEW";
        vm.EditName = "pending";
        var calls = fixture.Handler.Calls;
        var saving = vm.SaveCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(PageCommands(), command => Assert.Null(command.ResolveCommand(vm)));
        Assert.False(await vm.TryLeaveAsync());
        vm.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await saving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.CanInteract);
        Assert.Equal(calls + 1, fixture.Handler.Calls);
    }

    [AvaloniaFact]
    public async Task Shell_shortcuts_use_remote_page_commands_and_search_focus_with_real_table()
    {
        using var shell = new Shell();
        using var fixture = new Fixture((request, _) => Task.FromResult(
            Json(request.Method == HttpMethod.Get ? PageJson() : RecordJson())));
        var host = ActivatorUtilities.CreateInstance<MainViewModel>(shell.Provider, (object)PageCommands());
        using var vm = new RemoteDemoListViewModel(fixture.Service, host.Changes, fixture.Users);
        try
        {
            await vm.RefreshAsync();
            shell.Window.DataContext = host;
            var tab = new WorkspaceTabViewModel(RemoteDemoModuleRegistration.PageId, "Remote", vm);
            host.Tabs.Add(tab);
            host.SelectedTab = tab;
            shell.Layout();
            var view = shell.PageView<RemoteDemoListView>();
            var table = view.FindControl<TableView>("RecordsTable")!;
            Assert.Same(vm.Items, table.ItemsSource);
            Assert.Equal(3, table.Columns.Count);
            var search = view.FindControl<TextBox>("SearchBox")!;
            vm.SearchText = "find me";
            shell.TabHost.Focusable = true;
            Assert.True(shell.TabHost.Focus());
            shell.Press(Key.F, PhysicalKey.F, RawInputModifiers.Control);
            Assert.True(search.IsFocused);
            Assert.Equal(0, search.SelectionStart);
            Assert.Equal(search.Text!.Length, search.SelectionEnd);
            shell.Press(Key.N, PhysicalKey.N, RawInputModifiers.Control);
            if (vm.NewCommand.ExecutionTask is { } creating) await creating;
            Assert.True(vm.IsEditing);
            vm.EditCode = "NEW";
            vm.EditName = "Created via Shell";
            var calls = fixture.Handler.Calls;
            shell.Press(Key.S, PhysicalKey.S, RawInputModifiers.Control);
            if (vm.SaveCommand.ExecutionTask is { } saving) await saving;
            Assert.False(vm.IsEditing);
            Assert.Equal(calls + 2, fixture.Handler.Calls); // POST, then GET; no write retry.
            table.SelectedItem = Assert.Single(vm.Items);
            Assert.True(vm.IsEditing);
            Assert.True(shell.TabHost.Focus());
            calls = fixture.Handler.Calls;
            shell.Press(Key.Delete, PhysicalKey.Delete);
            Assert.True(vm.IsConfirmingDelete);
            shell.Press(Key.Escape, PhysicalKey.Escape);
            Assert.False(vm.IsConfirmingDelete);
            Assert.True(vm.IsEditing);
            vm.EditName = "keep this";
            host.Changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Cancel);
            shell.Press(Key.Escape, PhysicalKey.Escape);
            if (vm.CancelCommand.ExecutionTask is { } cancelling) await cancelling;
            Assert.True(vm.IsDirty);
            Assert.Equal("keep this", vm.EditName);
            Assert.Equal(calls, fixture.Handler.Calls);
        }
        finally
        {
            host.Changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Discard);
            host.ReleaseWorkspace();
            shell.Window.DataContext = shell.ViewModel;
        }
    }

    [AvaloniaFact]
    public async Task Table_selection_cannot_replace_a_dirty_draft_when_switch_is_declined()
    {
        var secondId = Guid.Parse("c2593cd6-cfc0-4bb5-8df7-0d68133e767d");
        var secondJson = RecordJson().Replace(RecordId.ToString(), secondId.ToString()).Replace("\"C1\"", "\"C2\"");
        using var fixture = new Fixture((_, _) => Task.FromResult(
            Json(PageJson(total: 2, items: "[" + RecordJson() + "," + secondJson + "]"))));
        var changes = new UnsavedChangesService { PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Cancel) };
        using var vm = new RemoteDemoListViewModel(fixture.Service, changes, fixture.Users);
        await vm.RefreshAsync();
        var view = new RemoteDemoListView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 720 };
        try
        {
            window.Show();
            var table = view.FindControl<TableView>("RecordsTable")!;
            table.SelectedItem = vm.Items[0];
            vm.EditName = "unsaved row";
            table.SelectedItem = vm.Items[1];
            Assert.Same(vm.Items[0], vm.SelectedItem);
            Assert.Same(vm.Items[0], table.SelectedItem);
            Assert.Equal("unsaved row", vm.EditName);
            Assert.True(vm.IsDirty);
            changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Discard);
            table.SelectedItem = vm.Items[1];
            Assert.Same(vm.Items[1], vm.SelectedItem);
            Assert.Same(vm.Items[1], table.SelectedItem);
            Assert.Equal("C2", vm.EditCode);
            Assert.False(vm.IsDirty);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task Anonymous_page_can_refresh_after_login()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(PageJson())));
        var authenticated = fixture.Users.User;
        fixture.Users.User = RemoteUserContext.Anonymous;
        using var vm = new RemoteDemoListViewModel(fixture.Service, new UnsavedChangesService(), fixture.Users);
        Assert.Equal(0, fixture.Handler.Calls);
        fixture.Users.User = authenticated;
        await vm.RefreshAsync();
        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task Invalid_form_utf16_uses_text_error_and_preserves_input()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(PageJson())));
        using var vm = new RemoteDemoListViewModel(fixture.Service, new UnsavedChangesService(), fixture.Users);
        await vm.RefreshAsync();
        await vm.NewCommand.ExecuteAsync(null);
        vm.EditCode = "\uD800";
        vm.EditName = "valid";
        var calls = fixture.Handler.Calls;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(Plinth.App.Localization.Loc.Get("remoteDemo.invalidText.message"), vm.CodeError);
        Assert.True(vm.IsDirty);
        Assert.Equal(calls, fixture.Handler.Calls);
    }

    private sealed class Users : IRemoteUserContext
    {
        public RemoteUserContext User = new("actor-1", ImmutableHashSet<string>.Empty,
            ImmutableHashSet.Create("demo:read", "demo:save", "demo:delete"));
        public RemoteUserContext CurrentUser => User;
        public long SessionVersion { get; private set; }
        public void ChangeActor() => User = User with { Subject = "actor-2" };
        public void NewSession() => SessionVersion++;
    }

    private sealed class Audit : ISecurityAudit
    {
        public void Write(AuditEvent entry) { }
    }

    private sealed class SwitchingTokens(Users users) : IAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            users.ChangeActor();
            return Task.FromResult("never-sent");
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return send(request, cancellationToken);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Users Users { get; } = new();
        public Handler Handler { get; }
        public SensitiveOperationService Sensitive { get; }
        public RemoteDemoService Service { get; }
        private readonly HttpClient _http;
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, bool production = false)
        {
            Handler = new(send);
            _http = RemoteHttp.CreateClient(Handler);
            var audit = new Audit();
            var authorized = new AuthorizedOperation(new PermissionService(Users, audit), Users, audit);
            Sensitive = new(authorized, audit, authenticationEnabled: true, production: production);
            Service = new(new RemoteApiClient(_http, new Uri("https://api.example/api/"), users: Users),
                authorized, Sensitive, Users);
        }
        public void Dispose() { _http.Dispose(); Handler.Dispose(); }
    }
}
