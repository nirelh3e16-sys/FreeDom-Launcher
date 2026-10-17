using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FreeDomLauncher.Core.Util;

public static class ColorUtil
{
    private static readonly Regex Hex = new("^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);
    public static bool IsValidHex(string? s) => !string.IsNullOrWhiteSpace(s) && Hex.IsMatch(s.Trim());
}

public static class DownloadFormat
{
    public static string Count(long n)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        if (n >= 1_000_000) return (n / 1_000_000.0).ToString("0.#", ru) + " млн";
        if (n >= 1_000) return (n / 1_000.0).ToString("0.#", ru) + " тыс.";
        return n.ToString(ru);
    }

    public static string Size(long bytes)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        if (bytes >= 1024 * 1024) return (bytes / 1048576.0).ToString("0.##", ru) + " МБ";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("0.#", ru) + " КБ";
        return bytes + " Б";
    }
}

/// <summary>Безопасная работа с путями: защита от выхода за пределы папки сборки (../, абсолютные пути, спецсимволы).</summary>
public static class SafePath
{
    /// <summary>Склеивает путь и гарантирует, что результат находится внутри root.</summary>
    public static string Combine(string root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Пустой корневой путь.", nameof(root));
        var fullRoot = Path.GetFullPath(root);
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var combined = fullRoot;
        foreach (var p in parts)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (Path.IsPathRooted(p)) throw new InvalidOperationException("Недопустимый путь: абсолютный путь внутри папки сборки.");
            combined = Path.Combine(combined, p);
        }
        var full = Path.GetFullPath(combined);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.Equals(fullRoot, cmp) && !full.StartsWith(rootWithSep, cmp))
            throw new InvalidOperationException("Недопустимый путь: выход за пределы папки сборки.");
        return full;
    }

    public static bool IsInside(string root, string path)
    {
        try { Combine(root, Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path))); return true; }
        catch { return false; }
    }

    /// <summary>Оставляет только имя файла, убирает недопустимые символы. Бросает исключение для пустых и служебных имён.</summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Пустое имя файла.");
        var n = name.Replace('\\', '/');
        n = n[(n.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in n) sb.Append(Array.IndexOf(invalid, c) >= 0 || c < 32 ? '_' : c);
        var res = sb.ToString().Trim().TrimEnd('.', ' ');
        if (res.Length == 0 || res is "." or "..") throw new InvalidOperationException("Недопустимое имя файла.");
        if (res.Length > 180) res = res[^180..];
        return res;
    }

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (c is ' ' or '-' or '_' or '.') { if (sb.Length > 0 && sb[^1] != '-') sb.Append('-'); }
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "profile" : (s.Length > 32 ? s[..32] : s);
    }
}

/// <summary>Преобразует Markdown/HTML описание проекта в безопасный простой текст (ничего не исполняется и не рендерится как HTML).</summary>
public static class MarkdownText
{
    private static readonly Regex Html = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Img = new(@"!\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s*", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Emph = new(@"(\*\*|__|\*|_|~~|`)", RegexOptions.Compiled);
    private static readonly Regex Blank = new(@"\n{3,}", RegexOptions.Compiled);

    public static string ToPlain(string? md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var s = md.Replace("\r\n", "\n");
        s = Regex.Replace(s, "<(script|style)[^>]*>.*?</\\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<!--.*?-->", "", RegexOptions.Singleline);
        s = Regex.Replace(s, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Img.Replace(s, m => m.Groups[1].Value);
        s = Link.Replace(s, "$1");
        s = Html.Replace(s, "");
        s = Heading.Replace(s, "");
        s = Emph.Replace(s, "");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = Blank.Replace(s, "\n\n");
        return s.Trim();
    }
}

/// <summary>Удаляет секреты из текста перед записью в лог.</summary>
public static class Redactor
{
    private static readonly Regex[] Patterns =
    {
        new(@"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{5,}", RegexOptions.Compiled),
        new(@"(?i)(--accessToken|--uuid|--xuid|--clientId)\s+\S+", RegexOptions.Compiled),
        new(@"(?i)(access_token|refresh_token|accessToken|refreshToken|device_code|identityToken)""?\s*[:=]\s*""?[^\s"",}]+", RegexOptions.Compiled),
        new(@"(?i)Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.Compiled),
        new(@"(?i)XBL3\.0\s+x=[^\s""]+", RegexOptions.Compiled),
    };

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = text;
        foreach (var p in Patterns) s = p.Replace(s, "[скрыто]");
        return s;
    }
}
