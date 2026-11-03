using System.Threading.Tasks;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class UnsavedChangesServiceTests
{
    [Fact]
    public async Task Missing_prompt_handler_cancels_without_discarding()
    {
        Assert.Equal(UnsavedChangesChoice.Cancel, await new UnsavedChangesService().AskAsync());
    }

    [Fact]
    public async Task Concurrent_requests_show_only_one_prompt_at_a_time()
    {
        var firstResponse = new TaskCompletionSource<UnsavedChangesChoice>();
        var secondResponse = new TaskCompletionSource<UnsavedChangesChoice>();
        var calls = 0;
        var service = new UnsavedChangesService
        {
            PromptAsync = () => ++calls == 1 ? firstResponse.Task : secondResponse.Task,
        };
        var first = service.AskAsync();
        var second = service.AskAsync();
        Assert.Equal(1, calls);
        firstResponse.SetResult(UnsavedChangesChoice.Save);
        Assert.Equal(UnsavedChangesChoice.Save, await first);
        secondResponse.SetResult(UnsavedChangesChoice.Discard);
        Assert.Equal(UnsavedChangesChoice.Discard, await second);
        Assert.Equal(2, calls);
    }
}
