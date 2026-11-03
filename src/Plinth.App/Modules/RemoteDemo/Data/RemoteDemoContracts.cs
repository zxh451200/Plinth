using System;
using System.Collections.Generic;

namespace Plinth.App.Modules.RemoteDemo.Data;

public sealed record RemoteDemoRecord(Guid Id, string Code, string Name, string Remark, int Version);
public sealed record RemoteDemoInput(string Code, string Name, string Remark);
public sealed record RemoteDemoPage(int Page, int PageSize, int TotalCount, IReadOnlyList<RemoteDemoRecord> Items);
