using Novalist.Sdk.Services;

namespace Novalist.Extensions.AiAssistant.Services;

internal static class PageStrings
{
    /// <summary>
    /// Adds the language and reading direction the locale declares, so a page
    /// can mirror itself for right-to-left languages without the host telling it.
    /// </summary>
    internal static Dictionary<string, string> WithPage(
        this Dictionary<string, string> strings, IExtensionLocalization loc)
    {
        strings["pageDir"] = loc.T("page.direction");
        strings["pageLang"] = loc.T("page.lang");
        return strings;
    }
}
