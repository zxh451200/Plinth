using System;
using System.Text;

namespace Plinth.App.Data;

/// <summary>
/// 编码唯一性只折叠 ASCII A-Z，其他字符原样保留，不做 Unicode 归一化。
/// 用大写 UTF-8 十六进制保存比较键，不依赖数据库语言排序规则。
/// 不在这里 Trim：输入入口负责去空白，历史编码必须原样迁移。
/// </summary>
public static class CodeIdentity
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Create(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var characters = code.ToCharArray();
        for (var i = 0; i < characters.Length; i++)
            if (characters[i] is >= 'A' and <= 'Z') characters[i] = (char)(characters[i] + ('a' - 'A'));
        return Convert.ToHexString(Utf8.GetBytes(characters));
    }

    public static bool IsValid(string code)
    {
        try { _ = Utf8.GetByteCount(code); return true; }
        catch (EncoderFallbackException) { return false; }
    }
}
