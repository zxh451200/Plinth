using System;
using Plinth.App.Modules.Demo.Data;

namespace Plinth.App.Modules.Demo.Services;

/// <summary>稳定的字段标识；Form 表示并发冲突等非字段错误。</summary>
public enum DemoSaveField { Form, Code, Name, Remark, Quantity, DueDate, Priority }

public sealed record DemoSaveError(DemoSaveField Field, string Message);

/// <summary>接收尚未校验的表单输入，避免实体 setter 提前编码非法 UTF-16。</summary>
public sealed record DemoSaveInput(Guid Id, int Version, string Code, string Name, string Remark,
    bool MustExist = false,
    int Quantity = 0, DateOnly? DueDate = null, DemoPriority Priority = DemoPriority.Normal,
    bool Active = true);
