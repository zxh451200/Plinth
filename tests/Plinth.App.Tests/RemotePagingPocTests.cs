using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Auth;
using Plinth.App.Modules.RemoteDemo.Data;
using Plinth.App.Modules.RemoteDemo.Services;
using Plinth.App.Remote;
using Plinth.App.Security;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

// §6.4/13.2: real server totals, bounded current-page loading; no seed or write calls.
// Compile only with -p:PlinthRunPoc=true. Then supply process environment only:
// PLINTH_REMOTE_POC_ENABLED=true, PLINTH_REMOTE_POC_ENVIRONMENT=test|staging,
// PLINTH_REMOTE_POC_BASE_URL=https://<dedicated-test-host>/api/,
// PLINTH_REMOTE_POC_TOKEN=<short-lived read-only token>,
// PLINTH_REMOTE_POC_EXPECTED_TOTAL=100000|1000000,
// PLINTH_REMOTE_POC_DEVICE=CPU model; physical RAM; resolution; scaling; device tier.
// Run separately against stable, independently counted server datasets of each size.
// Do not put tokens in command arguments, files, test parameters or captured shell traces.
[Collection("PerformancePoc")]
public sealed class RemotePagingPocTests(ITestOutputHelper output)
{
    private const string Prefix = "PLINTH_REMOTE_POC_";
    private const int PageSize = 50;

    [Fact]
    [Trait("Category", "RemotePagingPoc")]
    public async Task Real_api_first_middle_last_pages_match_server_total()
    {
        Assert.SkipWhen(Read("ENABLED") != "true",
            "未执行真实远端 POC：需显式设置 PLINTH_REMOTE_POC_ENABLED=true。");
        var address = Read("BASE_URL");
        var token = Read("TOKEN");
        var totalText = Read("EXPECTED_TOTAL");
        var environment = Read("ENVIRONMENT");
        var device = Read("DEVICE");
        Assert.SkipWhen(new[] { address, token, totalText, environment, device }.Any(string.IsNullOrWhiteSpace),
            "未执行真实远端 POC：缺少专用测试环境 BASE_URL/TOKEN/EXPECTED_TOTAL/ENVIRONMENT/DEVICE；没有 mock 回退。");

        // No deployment configuration, credential store, login, discovery or production fallback.
        Require(environment is "test" or "staging", "仅允许明确的 test/staging 环境。");
        Require(Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && RemoteApiClient.IsHttpsAddress(uri) && uri.Query.Length == 0
            && uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal),
            "BASE_URL 必须是无凭据、查询和片段且以 / 结尾的 HTTPS 测试地址。");
        // The operator selects a dedicated test endpoint explicitly. Host naming does
        // not establish environment ownership and must not reject valid QA hosts or IPs.
        Require(int.TryParse(totalText, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedTotal)
            && expectedTotal is 100000 or 1000000, "EXPECTED_TOTAL 必须是 100000 或 1000000。");
        Require(token!.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/' or '='),
            "TOKEN 格式无效；不输出其值。");

        Require(device!.Length <= 512 && !device.Any(char.IsControl)
            && !device.Contains(token, StringComparison.Ordinal), "DEVICE 必须为不含凭据的单行设备描述，最长 512 字符。");

        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        using var http = new HttpClient(handler) { MaxResponseContentBufferSize = 1024 * 1024 };
        var session = new EnvironmentSession(token!);
        var audit = new SecurityAudit(NullLogger<SecurityAudit>.Instance);
        var authorized = new AuthorizedOperation(new PermissionService(session, audit), session, audit);
        var api = new RemoteApiClient(http, uri!, session, audit, session);
        var service = new RemoteDemoService(api, authorized,
            new SensitiveOperationService(authorized, audit, authenticationEnabled: true, production: false), session);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        output.WriteLine($"device={device}");
        output.WriteLine($"UTC={DateTimeOffset.UtcNow:O}; OS={RuntimeInformation.OSDescription}; " +
            $"architecture={RuntimeInformation.ProcessArchitecture}; runtime={RuntimeInformation.FrameworkDescription}; " +
            $"logicalCPU={Environment.ProcessorCount}; 64bit={Environment.Is64BitProcess}; " +
            $"gcAvailableBytes={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes}; configuration={configuration}; " +
            $"expectedServerTotal={expectedTotal}; pageSize={PageSize}");
        output.WriteLine("只读 API 样本；设备描述由操作者提供，应含 CPU 型号/物理 RAM/分辨率/缩放与设备档位。" +
            "耗时包括网络、鉴权、缓冲及解析；内存为测试宿主阶段采样，并非桌面峰值。无性能阈值。" +
            "不覆盖冷启动、UI 首屏、滚动、IME、编辑、取消及长期资源趋势。");

        var last = (expectedTotal - 1) / PageSize + 1;
        var pages = new[] { 1, (last + 1) / 2, last };
        var seen = new HashSet<Guid>(); // At most three pages, never the server dataset.
        foreach (var page in pages)
        {
            var result = await MeasurePage(service, page);
            Require(result.TotalCount == expectedTotal, "服务端总量与独立确认的预期总量不一致。");
            Require(result.Page == page && result.PageSize == PageSize, "服务端分页元数据不一致。");
            var count = Math.Min(PageSize, expectedTotal - (page - 1) * PageSize);
            Require(result.Items.Count == count, "首/中/末页行数不符合服务端总量。");
            foreach (var row in result.Items)
                Require(seen.Add(row.Id), "采样页之间出现重复 ID，可能忽略了分页或数据集不稳定。");
            output.WriteLine($"validatedPage={page}; serverTotal={result.TotalCount}; rows={result.Items.Count}");
        }
        output.WriteLine("真实远端分页契约样本通过；仅代表本次设备、数据集及三次请求，不能据此声明性能达标。");
    }

    private async Task<RemoteDemoPage> MeasurePage(RemoteDemoService service, int page)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingBefore = process.WorkingSet64;
        var managedBefore = GC.GetTotalMemory(false);
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var watch = Stopwatch.StartNew();
        try
        {
            // Real sockets only; production service performs Bearer injection and response validation.
            return await service.SearchPageAsync(page: page, pageSize: PageSize, sortField: "Code");
        }
        catch (Exception)
        {
            // Never allow server bodies, correlation IDs or exception messages to echo a token.
            throw new InvalidOperationException("真实远端 POC 请求失败；未通过。请在测试服务端安全排查鉴权、网络或契约。");
        }
        finally
        {
            watch.Stop();
            process.Refresh();
            output.WriteLine($"page={page}; elapsedMs={watch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}; " +
                $"workingSetBytes(before/after)={workingBefore}/{process.WorkingSet64}; " +
                $"managedBytes(before/after)={managedBefore}/{GC.GetTotalMemory(false)}; " +
                $"allocatedBytesDelta={GC.GetTotalAllocatedBytes(false) - allocatedBefore}");
        }
    }

    private static string? Read(string name) => Environment.GetEnvironmentVariable(Prefix + name);
    private static void Require(bool condition, string safeMessage)
    {
        if (!condition) throw new InvalidOperationException(safeMessage);
    }

    // Test-only local read grant; the actual API independently validates the supplied token.
    // No JWT decoding, credential persistence, token refresh or fabricated API responses.
    private sealed class EnvironmentSession(string token) : IAccessTokenProvider, IRemoteUserContext
    {
        public RemoteUserContext CurrentUser { get; } = new("remote-poc-reader",
            ImmutableHashSet<string>.Empty, ImmutableHashSet.Create("demo:read"));
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(token);
        }
    }
}
