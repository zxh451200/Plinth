using Xunit;

namespace Plinth.App.Tests;

/// <summary>
/// Loc 是进程级状态:切换或重置界面语言的用例必须串行,且不能与其他用例并发,
/// 否则读中文标题的断言会被别的用例改成英文。
/// </summary>
[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationCollection
{
}
