using System;
using System.IO;

namespace Plinth.App.Tests;

/// <summary>测试里需要读源码文件(.resx、.axaml、.cs)时,用它找到仓库根目录。</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Src => Path.Combine(Root, "src", "Plinth.App");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Plinth.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录(Plinth.sln)。");
    }
}
