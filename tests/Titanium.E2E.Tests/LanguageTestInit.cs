using System.Runtime.CompilerServices;
using Titanium.Inspector.Localization;

namespace Titanium.E2E.Tests;

internal static class LanguageTestInit
{
    [ModuleInitializer]
    internal static void PinEnglish() => LanguageService.PinForTests(LanguageService.English);
}
